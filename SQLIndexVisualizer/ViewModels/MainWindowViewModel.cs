using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLIndexVisualizer.Models;
using SQLIndexVisualizer.Services;

namespace SQLIndexVisualizer.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly SqlServerService _sqlService = new();
    private CancellationTokenSource? _cts;

    private static readonly string SaveDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SQLIndexVisualizer");
    private static string OlaSqlPath    => Path.Combine(SaveDir, "ola_sql.txt");
    private static string CustomSqlPath => Path.Combine(SaveDir, "custom_sql.txt");

    public MainWindowViewModel()
    {
        if (File.Exists(CustomSqlPath))
            _customSql = File.ReadAllText(CustomSqlPath);
        if (File.Exists(OlaSqlPath))
            _indexOptimizeSql = File.ReadAllText(OlaSqlPath);
        _measurements.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMeasurements));
    }

    private void SaveEditorText(string path, string content)
    {
        try { Directory.CreateDirectory(SaveDir); File.WriteAllText(path, content); }
        catch { /* best-effort */ }
    }

    // ── Connection ────────────────────────────────────────────────────────────
    [ObservableProperty] private string _serverName = "localhost";
    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectButtonText))]
    private bool _isConnecting;

    // ── Databases ─────────────────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<string> _databases = new();
    [ObservableProperty] private string? _selectedDatabase = "StackOverflow2013";

    // ── Tables / Indexes ──────────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<TableGroup> _tableGroups = new();
    [ObservableProperty] private bool _isLoadingTables;

    // ── Analysis state ────────────────────────────────────────────────────────
    [ObservableProperty] private bool       _isAnalyzing;
    [ObservableProperty] private IndexInfo? _currentIndexInfo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFillFactor))]
    [NotifyPropertyChangedFor(nameof(InfoTitle))]
    private IndexItem? _selectedIndexItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasData))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(RollingWindowSize))]
    private List<PageData> _pageData = new();

    [ObservableProperty] private long _tableRowCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndexSizeDisplay))]
    private double _indexSizeMb;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvgFragmentation))]
    private double _avgPageDensity;
    [ObservableProperty] private double _minPageDensity;
    [ObservableProperty] private double _maxPageDensity;
    [ObservableProperty] private int    _totalPagesSampled;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIndexFragmentation))]
    private double _indexFragmentation = -1;
    [ObservableProperty] private double _avgPageRead;

    // ── Analysis display ──────────────────────────────────────────────────────
    [ObservableProperty] private string _currentSql     = string.Empty;
    [ObservableProperty] private string _elapsedSeconds = string.Empty;

    // ── Activity monitor ─────────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<SplitLockMeasurement> _measurements = new();
    [ObservableProperty] private bool   _hasBaseline;
    [ObservableProperty] private string _baselineTime     = string.Empty;
    [ObservableProperty] private string _measurementLabel = string.Empty;
    private IndexOperationalStats? _baseline;

    public bool HasMeasurements => Measurements.Count > 0;

    public bool CanTakeBaseline() => IsConnected && SelectedIndexItem != null;
    public bool CanMeasure()      => HasBaseline  && SelectedIndexItem != null;

    // ── Chart refresh triggers ─────────────────────────────────────────────
    public event EventHandler? ActivityChartChanged;

    // ── Maintenance ───────────────────────────────────────────────────────────
    [ObservableProperty] private string _indexOptimizeSql     = string.Empty;
    [ObservableProperty] private string _customSql            = "-- T-SQL runs against the selected database\r\n\r\nSELECT @@VERSION;";
    [ObservableProperty] private int    _selectedTabIndex;
    [ObservableProperty] private int    _maintenanceSubTabIndex;
    public ObservableCollection<string> MaintenanceLog { get; } = new();

    // ── Status ────────────────────────────────────────────────────────────────
    [ObservableProperty] private string _statusText   = "Not connected. Enter server name and click Connect.";
    [ObservableProperty] private string _statusServer = string.Empty;
    [ObservableProperty] private string _statusDb     = string.Empty;
    [ObservableProperty] private bool   _hasError;

    // ── T-SQL selection delegate (set by code-behind) ────────────────────────
    public Func<string?>? GetSelectedTsql { get; set; }

    // ── Chart refresh trigger ─────────────────────────────────────────────────
    public event EventHandler? ChartDataChanged;

    public string InfoTitle => SelectedIndexItem != null
        ? $"{SelectedIndexItem.FullTableName} → [{SelectedIndexItem.IndexName}]"
        : "SQL Index Visualizer";

    public string IndexSizeDisplay => IndexSizeMb switch
    {
        0    => "—",
        var mb when mb >= 1024 => $"{mb / 1024:F1} GB",
        var mb when mb < 1     => $"{mb * 1024:F0} KB",
        var mb                 => $"{mb:F1} MB"
    };

    public bool   HasData               => PageData.Count > 0;
    public bool   ShowEmptyState        => !HasData && !IsAnalyzing;
    public bool   HasFillFactor         => SelectedIndexItem?.FillFactor > 0;
    public bool   HasIndexFragmentation => IndexFragmentation >= 0;
    public double AvgFragmentation      => 100.0 - AvgPageDensity;
    public int    RollingWindowSize     => Math.Max(5, PageData.Count / 100);
    public string ConnectButtonText     => IsConnecting ? "⟳" : "Connect";

    partial void OnSelectedIndexItemChanged(IndexItem? value)
    {
        IndexOptimizeSql = BuildIndexOptimizeSql(value);
        MaintenanceLog.Clear();
        ReAnalyzeCommand.NotifyCanExecuteChanged();
        // Reset activity baseline — stats belong to a specific index
        _baseline    = null;
        HasBaseline  = false;
        BaselineTime = string.Empty;
        TakeBaselineCommand.NotifyCanExecuteChanged();
        MeasureCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsConnectedChanged(bool value)
    {
        TakeBaselineCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedDatabaseChanged(string? value)
    {
        IndexOptimizeSql = BuildIndexOptimizeSql(SelectedIndexItem);
    }

    private string BuildIndexOptimizeSql(IndexItem? item)
    {
        var db    = SelectedDatabase ?? "YourDatabase";
        var idx   = item != null
            ? $"{item.Schema}.{item.TableName}.{item.IndexName}"
            : "Schema.Table.IndexName";

        return $"""
            EXEC master.dbo.IndexOptimize
                @Databases                       = N'{db}',
                @Indexes                         = N'{idx}',
                @FragmentationLow                = NULL,
                @FragmentationMedium             = N'INDEX_REORGANIZE,INDEX_REBUILD_ONLINE,INDEX_REBUILD_OFFLINE',
                @FragmentationHigh               = N'INDEX_REBUILD_ONLINE,INDEX_REBUILD_OFFLINE',
                @FragmentationLevel1             = 5,
                @FragmentationLevel2             = 30,
                @MinNumberOfPages                = 0,
                @MaxNumberOfPages                = NULL,
                @SortInTempdb                    = N'Y',
                @MaxDOP                          = NULL,
                @FillFactor                      = NULL,
                @PadIndex                        = NULL,
                @LOBCompaction                   = N'Y',
                @UpdateStatistics                = NULL,
                @OnlyModifiedStatistics          = N'N',
                @StatisticsSample                = NULL,
                @StatisticsResample              = N'N',
                @PartitionLevel                  = N'Y',
                @MSShippedObjects                = N'N',
                @TimeLimit                       = NULL,
                @Delay                           = NULL,
                @WaitAtLowPriorityMaxDuration    = NULL,
                @WaitAtLowPriorityAbortAfterWait = NULL,
                @Resumable                       = N'N',
                @LockTimeout                     = NULL,
                @LockMessageSeverity             = 16,
                @LogToTable                      = N'Y',
                @Execute                         = N'Y';
            """;
    }

    private void AppendLog(string message) =>
        MaintenanceLog.Add($"[{DateTime.Now:HH:mm:ss}]  {message}");

    // ─────────────────────────────────────────────────────────────────────────
    //  Commands
    // ─────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(ServerName)) return;

        IsConnecting = true;
        HasError     = false;
        StatusText   = $"Connecting to {ServerName}...";
        IsConnected  = false;

        try
        {
            _sqlService.SetServer(ServerName);
            await _sqlService.TestConnectionAsync();

            IsConnected  = true;
            StatusServer = ServerName;
            StatusText   = $"Connected to {ServerName}. Loading databases...";

            var dbs = await _sqlService.GetDatabasesAsync();
            Databases.Clear();
            foreach (var db in dbs) Databases.Add(db);

            if (SelectedDatabase != null && !Databases.Contains(SelectedDatabase))
                SelectedDatabase = Databases.FirstOrDefault();

            StatusText = $"Connected: {ServerName} | {Databases.Count} database(s)";
        }
        catch (Exception ex)
        {
            HasError    = true;
            StatusText  = $"Connection failed: {ex.Message}";
            IsConnected = false;
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand]
    private async Task LoadTablesAsync()
    {
        if (string.IsNullOrEmpty(SelectedDatabase)) return;

        IsLoadingTables = true;
        TableGroups.Clear();
        StatusText = $"Loading tables from {SelectedDatabase}...";

        try
        {
            _sqlService.SetDatabase(ServerName, SelectedDatabase);
            StatusDb = SelectedDatabase;

            var groups = await _sqlService.GetTablesAndIndexesAsync();
            foreach (var g in groups) TableGroups.Add(g);

            var indexCount = groups.Sum(g => g.Indexes.Count);
            StatusText = $"{ServerName} | {SelectedDatabase} | {TableGroups.Count} table(s), {indexCount} index(es)";
        }
        catch (Exception ex)
        {
            HasError   = true;
            StatusText = $"Failed to load tables: {ex.Message}";
        }
        finally
        {
            IsLoadingTables = false;
        }
    }

    [RelayCommand]
    private async Task AnalyzeIndexAsync(IndexItem? item)
    {
        item ??= SelectedIndexItem;
        if (item == null) return;

        SelectedIndexItem = item;
        await RunAnalysisAsync(item);
    }

    [RelayCommand(CanExecute = nameof(CanReAnalyze))]
    private async Task ReAnalyzeAsync()
    {
        if (SelectedIndexItem != null)
            await RunAnalysisAsync(SelectedIndexItem);
    }
    private bool CanReAnalyze() => SelectedIndexItem != null && !IsAnalyzing;

    [RelayCommand(CanExecute = nameof(CanExecuteOptimize))]
    private async Task ExecuteIndexOptimizeAsync()
    {
        if (string.IsNullOrWhiteSpace(IndexOptimizeSql)) return;

        SaveEditorText(OlaSqlPath, IndexOptimizeSql);
        SelectedTabIndex = 1;
        IsAnalyzing      = true;
        HasError         = false;
        MaintenanceLog.Clear();
        AppendLog("Executing dbo.IndexOptimize…");
        StatusText = "Running IndexOptimize…";

        try
        {
            _cts = new CancellationTokenSource();
            await _sqlService.ExecuteWithMessagesAsync(
                IndexOptimizeSql,
                msg =>
                {
                    if (!string.IsNullOrWhiteSpace(msg))
                        Dispatcher.UIThread.Post(() => AppendLog(msg));
                },
                useMasterConnection: true,
                _cts.Token);

            AppendLog("IndexOptimize complete.");
            StatusText = "IndexOptimize complete.";
        }
        catch (OperationCanceledException)
        {
            AppendLog("Cancelled.");
            StatusText = "Operation cancelled.";
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
            HasError   = true;
            StatusText = $"IndexOptimize failed: {ex.Message}";
        }
        finally
        {
            IsAnalyzing = false;
            ReAnalyzeCommand.NotifyCanExecuteChanged();
            ExecuteIndexOptimizeCommand.NotifyCanExecuteChanged();
        }
    }
    private bool CanExecuteOptimize() => !string.IsNullOrWhiteSpace(IndexOptimizeSql) && !IsAnalyzing;

    [RelayCommand(CanExecute = nameof(CanExecuteTsql))]
    private async Task ExecuteTsqlAsync()
    {
        if (string.IsNullOrWhiteSpace(CustomSql)) return;

        var sqlToRun = GetSelectedTsql?.Invoke() is { Length: > 0 } sel ? sel : CustomSql;
        SaveEditorText(CustomSqlPath, CustomSql);
        SelectedTabIndex         = 1;
        MaintenanceSubTabIndex   = 1;
        IsAnalyzing              = true;
        HasError                 = false;
        MaintenanceLog.Clear();
        AppendLog($"Executing T-SQL against [{SelectedDatabase}]…");
        StatusText = $"Running T-SQL against {SelectedDatabase}…";

        try
        {
            _cts = new CancellationTokenSource();
            await _sqlService.ExecuteWithMessagesAsync(
                sqlToRun,
                msg =>
                {
                    if (!string.IsNullOrWhiteSpace(msg))
                        Dispatcher.UIThread.Post(() => AppendLog(msg));
                },
                useMasterConnection: false,
                _cts.Token);

            AppendLog("Done.");
            StatusText = "T-SQL complete.";
        }
        catch (OperationCanceledException)
        {
            AppendLog("Cancelled.");
            StatusText = "Operation cancelled.";
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
            HasError   = true;
            StatusText = $"T-SQL failed: {ex.Message}";
        }
        finally
        {
            IsAnalyzing = false;
            ReAnalyzeCommand.NotifyCanExecuteChanged();
            ExecuteIndexOptimizeCommand.NotifyCanExecuteChanged();
            ExecuteTsqlCommand.NotifyCanExecuteChanged();
        }
    }
    private bool CanExecuteTsql() => !string.IsNullOrWhiteSpace(CustomSql) && !IsAnalyzing
                                      && !string.IsNullOrEmpty(SelectedDatabase);

    [RelayCommand]
    private void CancelOperation()
    {
        _cts?.Cancel();
        StatusText = "Operation cancelled.";
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Activity monitor commands
    // ─────────────────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanTakeBaseline))]
    private async Task TakeBaselineAsync()
    {
        try
        {
            _baseline    = await _sqlService.GetIndexOperationalStatsAsync(
                               SelectedIndexItem!.ObjectId, SelectedIndexItem.IndexId);
            BaselineTime = DateTime.Now.ToString("HH:mm:ss");
            HasBaseline  = true;
            MeasureCommand.NotifyCanExecuteChanged();
            StatusText   = $"Baseline captured for [{SelectedIndexItem.IndexName}] at {BaselineTime}";
        }
        catch (Exception ex)
        {
            StatusText = $"Baseline failed: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanMeasure))]
    private async Task MeasureAsync()
    {
        try
        {
            var after = await _sqlService.GetIndexOperationalStatsAsync(
                            SelectedIndexItem!.ObjectId, SelectedIndexItem.IndexId);

            var m = new SplitLockMeasurement
            {
                Label      = string.IsNullOrWhiteSpace(MeasurementLabel)
                                 ? $"#{Measurements.Count + 1}"
                                 : MeasurementLabel,
                PageSplits = after.LeafAllocations - _baseline!.LeafAllocations,
                LockWaits  = (after.RowLockWaits  + after.PageLockWaits)
                           - (_baseline.RowLockWaits + _baseline.PageLockWaits),
                LockWaitMs = (after.RowLockWaitMs + after.PageLockWaitMs)
                           - (_baseline.RowLockWaitMs + _baseline.PageLockWaitMs),
                MeasuredAt = DateTime.Now
            };
            Measurements.Add(m);
            MeasurementLabel = string.Empty;
            _baseline = after;   // advance baseline for the next delta
            ActivityChartChanged?.Invoke(this, EventArgs.Empty);
            StatusText = $"{m.Label}: {m.PageSplits:N0} splits, {m.LockWaits:N0} lock waits";
        }
        catch (Exception ex)
        {
            StatusText = $"Measure failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearMeasurements()
    {
        Measurements.Clear();
        _baseline    = null;
        HasBaseline  = false;
        BaselineTime = string.Empty;
        MeasureCommand.NotifyCanExecuteChanged();
        ActivityChartChanged?.Invoke(this, EventArgs.Empty);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Core analysis
    // ─────────────────────────────────────────────────────────────────────────

    private async Task RunAnalysisAsync(IndexItem item)
    {
        IsAnalyzing    = true;
        HasError       = false;
        TableRowCount  = 0;
        IndexSizeMb    = 0;
        PageData       = new List<PageData>();
        ChartDataChanged?.Invoke(this, EventArgs.Empty);

        StatusText = $"Analyzing {item.FullTableName} → [{item.IndexName}]… This may take several minutes.";

        try
        {
            _cts = new CancellationTokenSource();

            CurrentSql = $"-- Table: {item.FullTableName}  Index: [{item.IndexName}] (ID: {item.IndexId})\r\nDECLARE @pObjectID INT = {item.ObjectId};\r\nDECLARE @pIndexID  INT = {item.IndexId};";

            var fragTask    = _sqlService.GetIndexFragmentationAsync(item.ObjectId, item.IndexId, _cts.Token);
            var statsTask   = _sqlService.GetRowCountAndSizeAsync(item.ObjectId, item.IndexId, _cts.Token);

            var pages = await _sqlService.AnalyzeIndexAsync(item.ObjectId, item.IndexId, _cts.Token);

            try   { IndexFragmentation = await fragTask; }
            catch { IndexFragmentation = -1; }

            try
            {
                var (rc, sz) = await statsTask;
                TableRowCount = rc;
                IndexSizeMb   = sz;
            }
            catch { /* non-critical */ }

            CurrentIndexInfo = new IndexInfo
            {
                ServerName = ServerName,
                DBName     = SelectedDatabase ?? string.Empty,
                SchemaName = item.Schema,
                ObjectName = item.TableName,
                IndexName  = item.IndexName,
                SampleDT   = DateTime.Now.ToString("dd MMM yyyy HH:mm:ss")
            };

            PageData          = pages;
            TotalPagesSampled = pages.Count;

            if (pages.Count > 0)
            {
                AvgPageDensity = pages.Average(p => p.PageDensity);
                MinPageDensity = pages.Min(p => p.PageDensity);
                MaxPageDensity = pages.Max(p => p.PageDensity);
                AvgPageRead    = pages.Average(p => p.PageRead);
            }

            StatusText = $"{ServerName} | {SelectedDatabase} | {item.FullTableName} → [{item.IndexName}]  " +
                         $"| {pages.Count:N0} pages sampled | Avg density: {AvgPageDensity:F1}%";

            ChartDataChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Analysis cancelled.";
        }
        catch (Exception ex)
        {
            HasError   = true;
            StatusText = $"Analysis failed: {ex.Message}";
        }
        finally
        {
            IsAnalyzing = false;
            ReAnalyzeCommand.NotifyCanExecuteChanged();
            ExecuteIndexOptimizeCommand.NotifyCanExecuteChanged();
            ExecuteTsqlCommand.NotifyCanExecuteChanged();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Chart helpers
    // ─────────────────────────────────────────────────────────────────────────

    public List<double> CalculateRollingAverage(int windowSize)
    {
        if (PageData.Count == 0) return new List<double>();
        int half   = windowSize / 2;
        var result = new List<double>(PageData.Count);
        for (int i = 0; i < PageData.Count; i++)
        {
            int start = Math.Max(0, i - half);
            int end   = Math.Min(PageData.Count - 1, i + half);
            double sum = 0;
            for (int j = start; j <= end; j++) sum += PageData[j].PageDensity;
            result.Add(sum / (end - start + 1));
        }
        return result;
    }
}
