using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlTelemetry;

public class TelemetryRewriter(TelemetryOptions options)
{
    private readonly TelemetryOptions _options = options;

    public string ProcessSql(string rawSqlInput, out int executableCount, out int injectedTelemetryCount)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true, SqlEngineType.Standalone);

        using var reader = new StringReader(rawSqlInput);
        TSqlFragment fragment = parser.Parse(reader, out IList<ParseError> errors);

        if (errors.Count > 0)
        {
            var errorDetails = string.Join("\n", errors.Select(e => $"Line {e.Line}, Col {e.Column}: {e.Message}"));
            throw new InvalidOperationException($"T-SQL Syntax / Parse Errors:\n{errorDetails}");
        }

        var visitor = new TelemetryVisitor(_options);
        fragment.Accept(visitor);

        executableCount = visitor.ExecutableStatementCount;
        injectedTelemetryCount = visitor.InjectedTelemetryCount;

        return visitor.TransformToInstrumentedScript(rawSqlInput);
    }
}

