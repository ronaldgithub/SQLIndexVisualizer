using System;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
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
    private RotateTransform?     _spinnerTransform;
    private DispatcherTimer?     _spinnerTimer;
    private DispatcherTimer?     _elapsedTimer;
    private DateTime             _analysisStart;
    private double               _spinAngle;

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

        var spinnerBorder = this.FindControl<Border>("SpinnerBorder");
        _spinnerTransform = spinnerBorder?.RenderTransform as RotateTransform;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
        {
            _vm.ChartDataChanged   -= OnChartDataChanged;
            _vm.PropertyChanged    -= OnVmPropertyChanged;
        }

        _vm = DataContext as MainWindowViewModel;

        if (_vm != null)
        {
            _vm.ChartDataChanged   += OnChartDataChanged;
            _vm.PropertyChanged    += OnVmPropertyChanged;
        }
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
        _spinAngle     = 0;
        _analysisStart = DateTime.Now;

        _spinnerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _spinnerTimer.Tick += (_, _) =>
        {
            _spinAngle = (_spinAngle + 4) % 360;
            if (_spinnerTransform != null)
                _spinnerTransform.Angle = _spinAngle;
        };
        _spinnerTimer.Start();

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
        _spinnerTimer?.Stop();
        _spinnerTimer = null;
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
        if (GetIndexItemFromSender(sender) is { } item)
        {
            _vm!.SelectedIndexItem = item;
            _vm.ReorganizeCommand.Execute(null);
        }
    }

    private void OnRebuildClick(object? sender, RoutedEventArgs e)
    {
        if (GetIndexItemFromSender(sender) is { } item)
        {
            _vm!.SelectedIndexItem = item;
            _vm.RebuildCommand.Execute(null);
        }
    }

    private void OnRebuildOnlineClick(object? sender, RoutedEventArgs e)
    {
        if (GetIndexItemFromSender(sender) is { } item)
        {
            _vm!.SelectedIndexItem = item;
            _vm.RebuildOnlineCommand.Execute(null);
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

        var pages = _vm.PageData;
        if (pages.Count == 0) { _chart.Refresh(); return; }

        var xs = pages.Select(p => (double)p.PageSort).ToArray();
        var ys = pages.Select(p => p.PageDensity).ToArray();

        // Page density scatter — blue + markers
        var scatter         = plot.Add.ScatterPoints(xs, ys);
        scatter.Color       = SPColor.FromHex("#4472C4");
        scatter.MarkerSize  = pages.Count > 5000 ? 3 : 5;
        scatter.MarkerShape = MarkerShape.Cross;
        scatter.LegendText  = "Page Density %";

        // Linear trend line (least-squares) — tells the story of page density across the index
        double n    = xs.Length;
        double sumX = xs.Sum();
        double sumY = ys.Sum();
        double sumXY = 0; for (int i = 0; i < xs.Length; i++) sumXY += xs[i] * ys[i];
        double sumX2 = 0; for (int i = 0; i < xs.Length; i++) sumX2 += xs[i] * xs[i];
        double denom = n * sumX2 - sumX * sumX;
        if (Math.Abs(denom) > 1e-10)
        {
            double slope     = (n * sumXY - sumX * sumY) / denom;
            double intercept = (sumY - slope * sumX) / n;
            double xMin = xs[0];
            double xMax = xs[xs.Length - 1];
            var trend        = plot.Add.Line(xMin, slope * xMin + intercept,
                                             xMax, slope * xMax + intercept);
            trend.Color      = SPColor.FromHex("#E05252");
            trend.LineWidth  = 1.5f;
            trend.LegendText = $"Linear  (slope {slope:+0.0000;-0.0000} %/page)";
        }

        // Average density
        double avg          = _vm.AvgPageDensity;
        var avgLine         = plot.Add.HorizontalLine(avg);
        avgLine.Color       = SPColor.FromHex("#4EC9B0");
        avgLine.LineWidth   = 1.5f;
        avgLine.LinePattern = LinePattern.Dashed;
        avgLine.LegendText  = $"Avg: {avg:F1}%";

        // Fill factor
        int ff = _vm.SelectedIndexItem?.FillFactor ?? 0;
        if (ff > 0)
        {
            var ffLine         = plot.Add.HorizontalLine(ff);
            ffLine.Color       = SPColor.FromHex("#DCDCAA");
            ffLine.LineWidth   = 1.5f;
            ffLine.LinePattern = LinePattern.Dotted;
            ffLine.LegendText  = $"Fill factor: {ff}%";
        }

        plot.XLabel("Logical Page Order");
        plot.YLabel("Page Density (%)");
        plot.Title(_vm.CurrentIndexInfo?.FullName ?? string.Empty);
        plot.Axes.SetLimitsY(0, 105);
        plot.Axes.SetLimitsX(0, xs.Max());

        var legend = plot.ShowLegend();
        legend.Alignment = Alignment.UpperRight;

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
