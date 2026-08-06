using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlTelemetry;

public class TelemetryVisitor(TelemetryOptions options) : TSqlFragmentVisitor
{
    private readonly TelemetryOptions _options = options;
    private readonly List<ParameterInfo> _parameters = [];
    private readonly List<string> _declaredVariables = [];
    private readonly Dictionary<string, int> _variableDeclarationOffsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ExecutableStatementInfo> _executableStatements = [];
    private readonly HashSet<int> _wrapBeginOffsets = [];
    private readonly HashSet<int> _wrapEndOffsets = [];
    private int _procedureBodyStartOffset = -1;
    private bool _hasTopLevelBegin = false;

    // To preserve SCOPE_IDENTITY (insert via exec, different scope) and ROWCOUNT (use dummy variable), use capture these variables.
    private const string RowCountVar = "@__t9_rowcount";
    private const string DummyVar = "@__t9_dummy";
    private const string StmtVar = "@__t9_statement";
    private const string VarsVar = "@__t9_vars";
    private const string SqlVar = "@__t9_dynamicsql";
    private const string ParamsVar = "@__t9_dynamicparams";

    public int ExecutableStatementCount => _executableStatements.Count;
    public int InjectedTelemetryCount { get; private set; }

    public override void Visit(CreateProcedureStatement node)
    {
        if (node.StatementList != null)
        {
            _procedureBodyStartOffset = node.StatementList.StartOffset;

            var firstStatement = node.StatementList.Statements[0];
            _hasTopLevelBegin = firstStatement is BeginEndBlockStatement;
        }
        base.Visit(node);
    }

    public override void Visit(AlterProcedureStatement node)
    {
        if (node.StatementList != null)
        {
            _procedureBodyStartOffset = node.StatementList.StartOffset;

            var firstStatement = node.StatementList.Statements[0];
            _hasTopLevelBegin = firstStatement is BeginEndBlockStatement;
        }
        base.Visit(node);
    }

    // Capture Stored Procedure parameters
    public override void Visit(ProcedureParameter node)
    {
        var paramName = node.VariableName?.Value ?? string.Empty;
        var dataType = GetFragmentText(node.DataType);
        var defaultValue = node.Value != null ? GetFragmentText(node.Value) : GetDefaultValueForType(dataType);

        if (!string.IsNullOrEmpty(paramName))
        {
            _parameters.Add(new ParameterInfo
            {
                Name = paramName,
                DataType = dataType,
                DefaultValue = defaultValue
            });
        }

        base.Visit(node);
    }

    public override void Visit(IfStatement node)
    {
        if (node.ThenStatement != null && node.ThenStatement is not BeginEndBlockStatement)
        {
            _wrapBeginOffsets.Add(node.ThenStatement.StartOffset);
            _wrapEndOffsets.Add(node.ThenStatement.StartOffset + node.ThenStatement.FragmentLength);
        }

        if (node.ElseStatement != null && node.ElseStatement is not BeginEndBlockStatement && node.ElseStatement is not IfStatement)
        {
            _wrapBeginOffsets.Add(node.ElseStatement.StartOffset);
            _wrapEndOffsets.Add(node.ElseStatement.StartOffset + node.ElseStatement.FragmentLength);
        }

        base.Visit(node);
    }

    // Capture local DECLARE variables
    public override void Visit(DeclareVariableElement node)
    {
        var varName = node.VariableName?.Value;
        if (!string.IsNullOrEmpty(varName))
        {
            _declaredVariables.Add(varName);

            if (!_variableDeclarationOffsets.ContainsKey(varName))
            {
                _variableDeclarationOffsets[varName] = node.StartOffset + node.FragmentLength;
            }
        }

        base.Visit(node);
    }

    // Identify ONLY Executable statements (excluding structural DECLARE, BEGIN/END, TRY/CATCH)
    public override void Visit(InsertStatement node) => TrackExecutable(node, "INSERT");
    public override void Visit(UpdateStatement node) => TrackExecutable(node, "UPDATE");
    public override void Visit(DeleteStatement node) => TrackExecutable(node, "DELETE");
    public override void Visit(MergeStatement node) => TrackExecutable(node, "MERGE");
    public override void Visit(SelectStatement node) => TrackExecutable(node, "SELECT");
    public override void Visit(SetVariableStatement node) => TrackExecutable(node, "SET");
    public override void Visit(ExecuteStatement node) => TrackExecutable(node, "EXEC");
    public override void Visit(TruncateTableStatement node) => TrackExecutable(node, "TRUNCATE");

    private void TrackExecutable(TSqlStatement statement, string type)
    {
        _executableStatements.Add(new ExecutableStatementInfo
        {
            StatementType = type,
            StartLine = statement.StartLine,
            StartOffset = statement.StartOffset,
            FragmentLength = statement.FragmentLength,
            SqlText = GetFragmentText(statement),
            StatementFragment = statement
        });
    }

    public string TransformToInstrumentedScript(string rawSql)
    {
        var sb = new StringBuilder();

        // Collect all unique variables
        var allVariables = _parameters.Select(p => p.Name)
            .Concat(_declaredVariables)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Apply custom variable filtering
        var watchedVariables = allVariables.Where(v =>
        {
            if (_options.ExcludedVariables.Any(ex => ex.Equals(v, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
            if (_options.WatchedVariables.Count > 0)
            {
                return _options.WatchedVariables.Any(inc => inc.Equals(v, StringComparison.OrdinalIgnoreCase));
            }
            return true;
        }).ToList();

        var parameterNames = new HashSet<string>(_parameters.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

        sb.AppendLine("-- ==========================================================================");
        sb.AppendLine("-- TELEMETRY INSTRUMENTED SCRIPT (Generated by TransactSql.ScriptDom)");
        sb.AppendLine($"-- Generated At: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"-- Frequency (N): Every {_options.StatementFrequency} executable statement(s)");
        sb.AppendLine($"-- Watched Variables ({watchedVariables.Count}): {(watchedVariables.Count < 10 ? string.Join(", ", watchedVariables) : "[MANY]")}");
        sb.AppendLine("-- ==========================================================================");
        sb.AppendLine();

        // Convert SP Parameters to DECLARE Statements (we DON'T want to change the actual procedure to one that depends on a debugging table)
        if (_parameters.Count > 0)
        {
            sb.AppendLine("-- --------------------------------------------------------------------------");
            sb.AppendLine("-- Parameter Declarations (Converted from Stored Procedure Parameters)");
            sb.AppendLine("-- --------------------------------------------------------------------------");
            foreach (var param in _parameters)
            {
                sb.AppendLine($"DECLARE {param.Name} {param.DataType} = {param.DefaultValue};");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"DECLARE {RowCountVar} INT = 0");
        sb.AppendLine($"DECLARE {DummyVar} SQL_VARIANT = NULL");
        sb.AppendLine($"DECLARE {StmtVar} XML");
        sb.AppendLine($"DECLARE {VarsVar} XML");

        // This dynamic SQL never changes
        sb.AppendLine($"DECLARE {SqlVar} NVARCHAR(MAX) = N'INSERT INTO {_options.TelemetryTableName} ([timestamp],[lineNumber],[statement],[variables]) " +
            "VALUES (SYSDATETIMEOFFSET(), @p_line, @p_stmt, @p_vars)'");
        sb.AppendLine($"DECLARE {ParamsVar} NVARCHAR(500) = N'@p_line INT, @p_stmt XML, @p_vars XML'");
        sb.AppendLine();

        // Global Temp Table Creation
        sb.AppendLine($"IF OBJECT_ID('tempdb..{_options.TelemetryTableName}') IS NOT NULL");
        sb.AppendLine($"    DROP TABLE {_options.TelemetryTableName};");
        sb.AppendLine();
        sb.AppendLine($"CREATE TABLE {_options.TelemetryTableName} (");
        sb.AppendLine("    [timestamp] DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),");
        sb.AppendLine("    [lineNumber] INT NULL,");
        sb.AppendLine("    [statement] XML NULL,");
        sb.AppendLine("    [variables] XML NULL");
        sb.AppendLine(");");
        sb.AppendLine();

        // Begin Transaction Wrapper
        if (_options.IncludeTransactionWrapper)
        {
            sb.AppendLine("BEGIN TRANSACTION;");
            sb.AppendLine();
        }

        sb.AppendLine("BEGIN TRY");
        sb.AppendLine();

        sb.AppendLine("-- --------------------------------------------------------------------------");
        sb.AppendLine("-- Begin Procedure Content");
        sb.AppendLine("-- --------------------------------------------------------------------------");

        // If this was CREAET PROCEDURE Whatever AS BEGIN END, removing the create procedure creates invalid SQL. This is weird but valid...
        if (_hasTopLevelBegin)
        {
            sb.AppendLine("IF 1 = 1");
        }

        // Inject Telemetry while PRESERVING ALL ORIGINAL SQL STRUCTURE (IF, ELSE, BEGIN, END, etc.)
        int bodyStart = _procedureBodyStartOffset >= 0 ? _procedureBodyStartOffset : 0;
        int currentPos = bodyStart;
        int execCounter = 0;
        int stepNum = 0;

        var sortedExecutables = _executableStatements.OrderBy(s => s.StartOffset).ToList();

        foreach (var stmt in sortedExecutables)
        {
            if (stmt.StartOffset < currentPos)
            {
                continue;
            }

            int endOffset = stmt.StartOffset + stmt.FragmentLength;

            bool needsBegin = _wrapBeginOffsets.Contains(stmt.StartOffset);
            bool needsEnd = _wrapEndOffsets.Contains(endOffset);

            if (needsBegin)
            {
                int prefixLen = stmt.StartOffset - currentPos;
                if (prefixLen > 0)
                {
                    sb.Append(rawSql.AsSpan(currentPos, prefixLen));
                }
                sb.AppendLine();
                sb.AppendLine("BEGIN");
                sb.Append(rawSql.AsSpan(stmt.StartOffset, stmt.FragmentLength));
            }
            else
            {
                string segment = rawSql.Substring(currentPos, endOffset - currentPos);
                sb.Append(segment);
            }

            currentPos = endOffset;

            execCounter++;
            if (execCounter % _options.StatementFrequency == 0)
            {
                stepNum++;
                InjectedTelemetryCount = stepNum;

                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine($"-- [Telemetry Step #{stepNum} - Line {stmt.StartLine}]");

                // Snapshot rowcount because the telemetry insert will set it to 1
                sb.AppendLine($"/* -- */ SET {RowCountVar} = @@ROWCOUNT");

                // Escape CDATA end sequence
                string safeSql = stmt.SqlText.Replace("]]>", "]]>]]&gt;").Replace("'", "''");

                // Load statement and variable list into variables
                sb.AppendLine($"/* -- */ SET {StmtVar} = N'<statement><![CDATA[{safeSql}]]></statement>'");

                // Only log vars if current location is below where it's declared (input parameters are declared right away)
                var variablesToLog = watchedVariables.Where(v => parameterNames.Contains(v) ||
                    (_variableDeclarationOffsets.TryGetValue(v, out int declaredAt) && declaredAt <= endOffset)).ToList();

                if (variablesToLog.Count > 0)
                {

                    var varCols = string.Join(",\n",
                        variablesToLog.Select(v => $"/* -- */       {v} AS [{v.Replace("@", "")}]"));
                    sb.AppendLine($"/* -- */ SET {VarsVar} = (SELECT \n{varCols}\n/* -- */    FOR XML PATH('variables'), TYPE)");
                }
                else
                {
                    sb.AppendLine($"SET {VarsVar} = NULL");
                }

                // This puts the statement in its own scope, so it doesn't overwrite SCOPE_IDENTITY() to make it NULL like a straight insert does
                sb.AppendLine($"/* -- */ EXEC sys.sp_executesql {SqlVar}, {ParamsVar}, @p_line = {stmt.StartLine}, @p_stmt = {StmtVar}, @p_vars = {VarsVar}");

                // ROWCOUNT gets overwritten by the telemetry insert even if the statement appears in a different scope (there's no SCOPE_ROWCOUNT.
                // This hack resets it (up to all columns x all columns count). It's ugly and it has obvious weaknesses but AFAIK it's our only option.
                sb.AppendLine($"/* -- */ ;WITH __t9_rowcountCTE AS (SELECT TOP ({RowCountVar}) 1 AS x FROM sys.all_columns a, sys.all_columns b) SELECT {DummyVar} = x FROM __t9_rowcountCTE;");

                sb.AppendLine($"-- [Telemetry Step #{stepNum} END]");
                sb.AppendLine();
            }

            if (needsEnd)
            {
                sb.AppendLine("END");
                sb.AppendLine();
            }
        }

        if (currentPos < rawSql.Length)
        {
            string remaining = rawSql.Substring(currentPos);
            // Comment out standalone GO statements to maintain variable scope in straight test scripts
            remaining = System.Text.RegularExpressions.Regex.Replace(
                remaining,
                @"^\s*GO\b",
                "-- GO",
                System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
            sb.Append(remaining);
        }

        sb.AppendLine();

        sb.AppendLine("-- --------------------------------------------------------------------------");
        sb.AppendLine("-- End Procedure Content");
        sb.AppendLine("-- --------------------------------------------------------------------------");

        sb.AppendLine();
        sb.AppendLine($"     SELECT * FROM {_options.TelemetryTableName} ORDER BY 1");

        sb.AppendLine("END TRY");
        sb.AppendLine("BEGIN CATCH");
        sb.AppendLine("     SELECT ERROR_NUMBER() AS Error, ERROR_MESSAGE() AS Message, ERROR_LINE() AS Line;");
        sb.AppendLine($"     SELECT * FROM {_options.TelemetryTableName} ORDER BY 1;");

        // End with Rollback Transaction
        if (_options.IncludeTransactionWrapper)
        {
            sb.AppendLine("     IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
        }

        sb.AppendLine("     THROW;");
        sb.AppendLine("END CATCH");

        // End with Rollback Transaction
        if (_options.IncludeTransactionWrapper)
        {
            sb.AppendLine("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
        }

        return sb.ToString();
    }

    private static string GetFragmentText(TSqlFragment fragment)
    {
        if (fragment == null) return string.Empty;
        var generator = new Sql160ScriptGenerator();
        generator.GenerateScript(fragment, out string script);
        return script.Trim();
    }

    private static string GetDefaultValueForType(string dataType)
    {
        var dt = dataType.ToUpperInvariant();
        if (dt.Contains("INT") || dt.Contains("BIT") || dt.Contains("DECIMAL") || dt.Contains("NUMERIC") || dt.Contains("FLOAT") || dt.Contains("MONEY"))
            return "0";
        if (dt.Contains("CHAR") || dt.Contains("TEXT"))
            return "''";
        if (dt.Contains("DATE") || dt.Contains("TIME"))
            return "SYSDATETIMEOFFSET()";
        return "NULL";
    }

    class ParameterInfo
    {
        public string Name { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public string DefaultValue { get; set; } = string.Empty;
    }

    class ExecutableStatementInfo
    {
        public string StatementType { get; set; } = string.Empty;
        public int StartLine { get; set; }
        public int StartOffset { get; set; }
        public int FragmentLength { get; set; }
        public string SqlText { get; set; } = string.Empty;
        public TSqlStatement StatementFragment { get; set; } = null!;
    }
}
