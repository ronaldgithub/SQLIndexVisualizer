using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ScottPlot;
using SPColor = ScottPlot.Color;
using ScottPlot.Avalonia;
using SQLIndexVisualizer.Models;
using SQLIndexVisualizer.ViewModels;

namespace SQLIndexVisualizer.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _vm;
    private AvaPlot?             _chart;
    private DispatcherTimer?     _elapsedTimer;
    private DateTime             _analysisStart;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _chart = this.FindControl<AvaPlot>("IndexChart");
        if (_chart != null) ApplyDarkStyle(_chart.Plot);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
        {
            _vm.ChartDataChanged -= OnChartDataChanged;
            _vm.PropertyChanged  -= OnVmPropertyChanged;
            _vm.MaintenanceLog.CollectionChanged -= OnMaintenanceLogChanged;
        }

        _vm = DataContext as MainWindowViewModel;

        if (_vm != null)
        {
            _vm.ChartDataChanged += OnChartDataChanged;
            _vm.PropertyChanged  += OnVmPropertyChanged;
            _vm.MaintenanceLog.CollectionChanged += OnMaintenanceLogChanged;
        }
    }

    private void OnMaintenanceLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var sv = this.FindControl<ScrollViewer>("LogScrollViewer");
        if (sv == null) return;
        Dispatcher.UIThread.Post(
            () => sv.Offset = new Vector(0, double.MaxValue),
            DispatcherPriority.Background);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Spinner + elapsed timer
    // ─────────────────────────────────────────────────────────────────────────

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.IsAnalyzing)) return;

        if (_vm!.IsAnalyzing) StartSpinner();
        else                  StopSpinner();
    }

    private void StartSpinner()
    {
        _analysisStart = DateTime.Now;

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) =>
        {
            var t = DateTime.Now - _analysisStart;
            if (_vm != null)
                _vm.ElapsedSeconds = $"Elapsed: {(int)t.TotalSeconds}s";
        };
        _elapsedTimer.Start();
    }

    private void StopSpinner()
    {
        _elapsedTimer?.Stop();
        _elapsedTimer = null;
        if (_vm != null) _vm.ElapsedSeconds = string.Empty;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Context menu handlers
    // ─────────────────────────────────────────────────────────────────────────

    private void OnAnalyzeClick(object? sender, RoutedEventArgs e)
    {
        if (GetIndexItemFromSender(sender) is { } item)
            _vm?.AnalyzeIndexCommand.Execute(item);
    }

    private void OnReorganizeClick(object? sender, RoutedEventArgs e)
    {
        if (GetIndexItemFromSender(sender) is { } item && _vm != null)
        {
            _vm.SelectedIndexItem = item;
            _vm.SelectedTabIndex  = 1;
        }
    }

    private void OnRebuildClick(object? sender, RoutedEventArgs e)
    {
        if (GetIndexItemFromSender(sender) is { } item && _vm != null)
        {
            _vm.SelectedIndexItem = item;
            _vm.SelectedTabIndex  = 1;
        }
    }

    private void OnRebuildOnlineClick(object? sender, RoutedEventArgs e)
    {
        if (GetIndexItemFromSender(sender) is { } item && _vm != null)
        {
            _vm.SelectedIndexItem = item;
            _vm.SelectedTabIndex  = 1;
        }
    }

    private static IndexItem? GetIndexItemFromSender(object? sender)
    {
        // ContextMenu closes before Click fires, so mi.Parent is already detached.
        // DataContext on the MenuItem is inherited from the DataTemplate and survives.
        if (sender is MenuItem { DataContext: IndexItem idx })
            return idx;
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Chart rendering
    // ─────────────────────────────────────────────────────────────────────────

    private void OnChartDataChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(RefreshChart);
    }

    private void RefreshChart()
    {
        if (_chart == null || _vm == null) return;

        var plot = _chart.Plot;
        plot.Clear();
        ApplyDarkStyle(plot);
        plot.HideLegend();

        var pages = _vm.PageData;
        if (pages.Count == 0) { _chart.Refresh(); return; }

        var xs = pages.Select(p => (double)p.PageSort).ToArray();
        var ys = pages.Select(p => p.PageDensity).ToArray();

        // Page density scatter — blue + markers
        var scatter         = plot.Add.ScatterPoints(xs, ys);
        scatter.Color       = SPColor.FromHex("#4472C4");
        scatter.MarkerSize  = pages.Count > 5000 ? 3 : 5;
        scatter.MarkerShape = MarkerShape.Cross;

        // Rolling average — drawn on top of scatter; thicker so it shows through the dense cloud
        int windowSize = Math.Max(5, pages.Count / 100);
        var rolling    = _vm.CalculateRollingAverage(windowSize);
        if (rolling.Count == xs.Length)
        {
            var ra         = plot.Add.Scatter(xs, rolling.ToArray());
            ra.LineWidth   = 3f;
            ra.MarkerSize  = 5;
            ra.MarkerShape = MarkerShape.FilledCircle;
            ra.LineColor   = SPColor.FromHex("#E05252");
            ra.MarkerColor = SPColor.FromHex("#1A1A1A");
        }

        // Average density
        double avg          = _vm.AvgPageDensity;
        var avgLine         = plot.Add.HorizontalLine(avg);
        avgLine.Color       = SPColor.FromHex("#4EC9B0");
        avgLine.LineWidth   = 1.5f;
        avgLine.LinePattern = LinePattern.Dashed;

        // Fill factor
        int ff = _vm.SelectedIndexItem?.FillFactor ?? 0;
        if (ff > 0)
        {
            var ffLine         = plot.Add.HorizontalLine(ff);
            ffLine.Color       = SPColor.FromHex("#DCDCAA");
            ffLine.LineWidth   = 1.5f;
            ffLine.LinePattern = LinePattern.Dotted;
        }

        plot.XLabel("Logical Page Order");
        plot.YLabel("Page Density (%)");
        plot.Title(_vm.CurrentIndexInfo?.FullName ?? string.Empty);
        plot.Axes.SetLimitsY(0, 105);
        plot.Axes.SetLimitsX(0, xs.Max());

        _chart.Refresh();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Dark theme
    // ─────────────────────────────────────────────────────────────────────────

    private static void ApplyDarkStyle(Plot plot)
    {
        plot.FigureBackground.Color = SPColor.FromHex("#1E1E1E");
        plot.DataBackground.Color   = SPColor.FromHex("#252526");
        plot.Axes.Color(SPColor.FromHex("#AAAAAA"));
        plot.Grid.MajorLineColor    = SPColor.FromHex("#3C3C3C");
        plot.Grid.MinorLineColor    = SPColor.FromHex("#2A2A2A");
        plot.Grid.MinorLineWidth    = 0.5f;
    }
}
