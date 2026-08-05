public class TelemetryOptions
{
    public string InputDirectory { get; set; } = "./input";
    public string OutputDirectory { get; set; } = "./output";
    public string ArchiveDirectory { get; set; } = "./archive";
    public int StatementFrequency { get; set; } = 1;
    public bool OnlySqlFiles { get; set; } = true;
    public bool ArchiveOriginalFile { get; set; } = true;
    public bool IncludeTransactionWrapper { get; set; } = true;
    public string TelemetryTableName { get; set; } = "##Telemetry";
    public List<string> ExcludedVariables { get; set; } = [];
    public List<string> WatchedVariables { get; set; } = [];
}
