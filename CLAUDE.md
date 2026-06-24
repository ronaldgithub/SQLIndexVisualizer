# CLAUDE.md — SQL Index DNA Visualizer

## Build & run

```powershell
dotnet build SQLIndexVisualizer/SQLIndexVisualizer.csproj
dotnet run --project SQLIndexVisualizer/SQLIndexVisualizer.csproj
```

Target: `net10.0`, `WinExe` (Windows only).

## Stack

| Concern | Package |
|---|---|
| UI framework | Avalonia 12.0.4 + FluentTheme, dark mode |
| Charting | ScottPlot.Avalonia 5.1.59 |
| SQL Server | Microsoft.Data.SqlClient 5.2.2 |
| MVVM | CommunityToolkit.Mvvm 8.3.2 |

Solution file is `.slnx` (new .NET 9+ format).

## Project layout

```
SQLIndexVisualizer/
  Models/           IndexInfo, PageData, TableGroup, IndexItem
  Services/         SqlServerService.cs  (all DB I/O)
  ViewModels/       MainWindowViewModel.cs
  Views/            MainWindow.axaml + MainWindow.axaml.cs
examples/           sp_IndexDNA SQL, sample workbooks, demo scripts
scripts/            same as examples (git-status shows both as untracked)
pictures/           development screenshots
```

## Architecture notes

- **MVVM pattern** — ViewModel exposes `ChartDataChanged` event; code-behind subscribes and calls `RefreshChart()` via `Dispatcher.UIThread.Post`.
- **Chart rendering lives entirely in code-behind** (`RefreshChart` in `MainWindow.axaml.cs`). ScottPlot's `AvaPlot` needs imperative calls.
- **Spinner animation** — driven by two `DispatcherTimer` instances in code-behind (16 ms for rotation, 1 s for elapsed counter). The `RotateTransform` reference is cached in `OnLoaded`.
- **Context menu resolution** — code-behind walks `MenuItem → ContextMenu → PlacementTarget.DataContext` to get the `IndexItem`.

## Gotchas — read before editing

### `AvaPlot` must never start hidden
`AvaPlot` initialises its Skia render surface on first layout pass. If `IsVisible=false` at startup it never initialises and the chart stays blank. The overlay states (welcome, analyzing, error) are placed **on top** of the always-visible `AvaPlot` using opaque `Border` wrappers in a stacked `Grid`.

### `FILLFACTOR` is a reserved SQL Server keyword
The column alias **must** be bracketed: `i.fill_factor AS [FillFactor]`. Unbracketed causes a syntax error.

### `ScottPlot.Color` vs `Avalonia.Media.Color` ambiguity
Both namespaces define `Color`. Resolved with:
```csharp
using SPColor = ScottPlot.Color;
using Avalonia.Media;          // kept for RotateTransform etc.
```
All ScottPlot color calls use `SPColor.FromHex(...)`.

### Avalonia 12 TreeView hierarchy
Use `Window.DataTemplates` with separate `TreeDataTemplate` and `DataTemplate` entries. Do **not** set `ItemTemplate` inside `TreeDataTemplate` — it causes AVLN2000 compile errors.

### `sp_IndexDNA` is pre-installed
The stored procedure is already installed in `master` on the target SQL Server and marked as a system procedure via `sp_ms_marksystemobject`. No install step is needed. The `InstallSpIndexDnaAsync` method exists in the service but is not wired to the UI.

### `CommandTimeout` for `sp_IndexDNA`
Set to `600` seconds. The proc calls `DBCC PAGE` in a loop — on large indexes (millions of pages) it can run for many minutes.

## Default connection values

| Setting | Default |
|---|---|
| Server | `localhost` |
| Database | `StackOverflow2013` |

## Chart layers (RefreshChart)

1. Scatter points — PageSort (X) vs PageDensity % (Y), blue `#569CD6`, cross markers
2. Rolling average line — orange `#FF9800`, window ≈ `count / 50`
3. Average density horizontal line — teal `#4EC9B0`, dashed
4. Fill factor horizontal line — yellow `#DCDCAA`, dotted (hidden when `fill_factor = 0`)
