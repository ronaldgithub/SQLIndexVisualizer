using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLIndexVisualizer.Models;
using SQLIndexVisualizer.Services;

namespace SQLIndexVisualizer.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly SqlServerService _sqlService = new();
    private CancellationTokenSource? _cts;

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
    [ObservableProperty] private IndexItem? _selectedIndexItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasData))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private List<PageData> _pageData = new();

    [ObservableProperty] private double _avgPageDensity;
    [ObservableProperty] private double _minPageDensity;
    [ObservableProperty] private double _maxPageDensity;
    [ObservableProperty] private int    _totalPagesSampled;

    // ── Analysis display ──────────────────────────────────────────────────────
    [ObservableProperty] private string _currentSql    = string.Empty;
    [ObservableProperty] private string _elapsedSeconds = string.Empty;

    // ── Status ────────────────────────────────────────────────────────────────
    [ObservableProperty] private string _statusText   = "Not connected. Enter server name and click Connect.";
    [ObservableProperty] private string _statusServer = string.Empty;
    [ObservableProperty] private string _statusDb     = string.Empty;
    [ObservableProperty] private bool   _hasError;

    // ── Chart refresh trigger ─────────────────────────────────────────────────
    public event EventHandler? ChartDataChanged;

    public bool HasData        => PageData.Count > 0;
    public bool ShowEmptyState => !HasData && !IsAnalyzing;
    public string ConnectButtonText => IsConnecting ? "⟳" : "Connect";

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

    [RelayCommand(CanExecute = nameof(CanMaintain))]
    private async Task ReorganizeAsync()
    {
        if (SelectedIndexItem == null) return;
        var item = SelectedIndexItem;

        IsAnalyzing = true;
        StatusText  = $"Reorganizing {item.FullTableName}.[{item.IndexName}]...";
        HasError    = false;

        try
        {
            _cts = new CancellationTokenSource();
            await _sqlService.ReorganizeIndexAsync(item.Schema, item.TableName, item.IndexName, _cts.Token);
            StatusText = "Reorganize complete. Re-analyzing...";
            await RunAnalysisAsync(item);
        }
        catch (Exception ex)
        {
            HasError    = true;
            StatusText  = $"Reorganize failed: {ex.Message}";
            IsAnalyzing = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanMaintain))]
    private async Task RebuildAsync()
    {
        if (SelectedIndexItem == null) return;
        var item = SelectedIndexItem;

        IsAnalyzing = true;
        StatusText  = $"Rebuilding {item.FullTableName}.[{item.IndexName}]...";
        HasError    = false;

        try
        {
            _cts = new CancellationTokenSource();
            await _sqlService.RebuildIndexAsync(item.Schema, item.TableName, item.IndexName,
                online: false, _cts.Token);
            StatusText = "Rebuild complete. Re-analyzing...";
            await RunAnalysisAsync(item);
        }
        catch (Exception ex)
        {
            HasError    = true;
            StatusText  = $"Rebuild failed: {ex.Message}";
            IsAnalyzing = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanMaintain))]
    private async Task RebuildOnlineAsync()
    {
        if (SelectedIndexItem == null) return;
        var item = SelectedIndexItem;

        IsAnalyzing = true;
        StatusText  = $"Rebuilding (ONLINE) {item.FullTableName}.[{item.IndexName}]...";
        HasError    = false;

        try
        {
            _cts = new CancellationTokenSource();
            await _sqlService.RebuildIndexAsync(item.Schema, item.TableName, item.IndexName,
                online: true, _cts.Token);
            StatusText = "Online rebuild complete. Re-analyzing...";
            await RunAnalysisAsync(item);
        }
        catch (Exception ex)
        {
            HasError    = true;
            StatusText  = $"Online rebuild failed: {ex.Message}";
            IsAnalyzing = false;
        }
    }

    private bool CanMaintain() => SelectedIndexItem != null && !IsAnalyzing;

    [RelayCommand]
    private void CancelOperation()
    {
        _cts?.Cancel();
        StatusText = "Operation cancelled.";
    }

    [RelayCommand]
    private async Task InstallSpIndexDnaAsync()
    {
        StatusText = "Installing sp_IndexDNA in master...";
        HasError   = false;
        try
        {
            await _sqlService.InstallSpIndexDnaAsync();
            StatusText = "sp_IndexDNA installed successfully.";
        }
        catch (Exception ex)
        {
            HasError   = true;
            StatusText = $"Install failed: {ex.Message}";
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Core analysis
    // ─────────────────────────────────────────────────────────────────────────

    private async Task RunAnalysisAsync(IndexItem item)
    {
        IsAnalyzing = true;
        HasError    = false;
        PageData    = new List<PageData>();
        ChartDataChanged?.Invoke(this, EventArgs.Empty);

        StatusText = $"Analyzing {item.FullTableName} → [{item.IndexName}]… This may take several minutes.";

        try
        {
            _cts = new CancellationTokenSource();

            CurrentSql = $"USE [{SelectedDatabase}]\r\nGO\r\nEXEC dbo.sp_IndexDNA\r\n    @pObjectID = {item.ObjectId},  -- {item.FullTableName}\r\n    @pIndexID  = {item.IndexId}     -- {item.IndexName}";

            var (info, pages) = await _sqlService.AnalyzeIndexAsync(item.ObjectId, item.IndexId, _cts.Token);

            CurrentIndexInfo  = info;
            PageData          = pages;
            TotalPagesSampled = pages.Count;

            if (pages.Count > 0)
            {
                AvgPageDensity = pages.Average(p => p.PageDensity);
                MinPageDensity = pages.Min(p => p.PageDensity);
                MaxPageDensity = pages.Max(p => p.PageDensity);
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
            ReorganizeCommand.NotifyCanExecuteChanged();
            RebuildCommand.NotifyCanExecuteChanged();
            RebuildOnlineCommand.NotifyCanExecuteChanged();
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
