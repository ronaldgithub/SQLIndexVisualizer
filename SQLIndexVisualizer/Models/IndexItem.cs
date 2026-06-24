namespace SQLIndexVisualizer.Models;

public class IndexItem
{
    public string Schema { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public string IndexName { get; set; } = string.Empty;
    public int IndexId { get; set; }
    public int ObjectId { get; set; }
    public string IndexType { get; set; } = string.Empty;
    public int FillFactor { get; set; }

    public bool IsClustered => IndexId == 1;
    public string TypeTag => IsClustered ? "CI" : "NCI";
    public string DisplayName => $"{IndexName}  [{TypeTag}]";
    public string FullTableName => $"[{Schema}].[{TableName}]";
}
