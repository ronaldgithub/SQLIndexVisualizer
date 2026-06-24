using System.Collections.ObjectModel;

namespace SQLIndexVisualizer.Models;

public class TableGroup
{
    public string Schema { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public ObservableCollection<IndexItem> Indexes { get; set; } = new();
    public string DisplayName => $"{Schema}.{TableName}";
}
