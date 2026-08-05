using Microsoft.Extensions.Configuration;
using SqlTelemetry;

Console.WriteLine("==========================================================");
Console.WriteLine(" T-SQL Telemetry Instrumentation Console Application");
Console.WriteLine(" Powered by Microsoft.SqlServer.TransactSql.ScriptDom");
Console.WriteLine("==========================================================");

// Load settings from appsettings.json
IConfiguration config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .Build();

var options = config.GetSection("TelemetrySettings").Get<TelemetryOptions>()
              ?? new TelemetryOptions();

// Resolve folder paths
string inputDir = Path.GetFullPath(options.InputDirectory);
string outputDir = Path.GetFullPath(options.OutputDirectory);
string archiveDir = Path.GetFullPath(options.ArchiveDirectory);

Directory.CreateDirectory(inputDir);
Directory.CreateDirectory(outputDir);
Directory.CreateDirectory(archiveDir);

Console.WriteLine($"[Config] Input Directory:   {inputDir}");
Console.WriteLine($"[Config] Output Directory:  {outputDir}");
Console.WriteLine($"[Config] Archive Directory: {archiveDir}");
Console.WriteLine($"[Config] Statement N:       Every {options.StatementFrequency} executable statement(s)");
Console.WriteLine($"[Config] SQL File Filter:   {(options.OnlySqlFiles ? "*.sql" : "*.*")}");
Console.WriteLine();

// Search files in input folder
string pattern = options.OnlySqlFiles ? "*.sql" : "*.*";
string[] files = Directory.GetFiles(inputDir, pattern, SearchOption.TopDirectoryOnly);

if (files.Length == 0)
{
    Console.WriteLine($"[Notice] No {pattern} files found in '{inputDir}'.");
    Console.WriteLine("Place your Stored Procedure .sql files into the input folder and run again.");
    return;
}

Console.WriteLine($"Found {files.Length} file(s) to process...\n");

var rewriter = new TelemetryRewriter(options);

foreach (string filePath in files)
{
    string fileName = Path.GetFileName(filePath);
    Console.WriteLine($"---> Processing: {fileName}");

    try
    {
        string rawSql = File.ReadAllText(filePath);
        string instrumentedSql = rewriter.ProcessSql(rawSql, out int executableCount, out int injectedCount);

        // Write to output directory
        string outputPath = Path.Combine(outputDir, fileName);
        File.WriteAllText(outputPath, instrumentedSql);
        Console.WriteLine($"     [Success] Saved modified script ({executableCount} exec stmts, {injectedCount} logs) -> {outputPath}");

        // Archive original file
        if (options.ArchiveOriginalFile)
        {
            string archiveFileName = $"{Path.GetFileNameWithoutExtension(fileName)}_{DateTime.Now:yyyyMMdd_HHmmss}{Path.GetExtension(fileName)}";
            string archivePath = Path.Combine(archiveDir, archiveFileName);
            File.Move(filePath, archivePath);
            Console.WriteLine($"     [Archived] Moved original file -> {archivePath}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"     [Error] Failed to process {fileName}: {ex.Message}");
    }

    Console.WriteLine();
}

Console.WriteLine("Telemetry Instrumentation complete");
