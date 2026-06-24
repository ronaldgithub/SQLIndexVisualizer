namespace SQLIndexVisualizer.Models;

public class IndexInfo
{
    public string ServerName { get; set; } = string.Empty;
    public string DBName { get; set; } = string.Empty;
    public string SchemaName { get; set; } = string.Empty;
    public string ObjectName { get; set; } = string.Empty;
    public string IndexName { get; set; } = string.Empty;
    public string SampleDT { get; set; } = string.Empty;

    public string FullName => $"{SchemaName}.{ObjectName} → {IndexName}";
    public string Title => $"{ServerName} | {DBName} | {FullName} | {SampleDT}";
}
