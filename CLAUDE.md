# CLAUDE.md — SQL Index Visualizer

## Build & run

```powershell
dotnet build SQLIndexVisualizer/SQLIndexVisualizer.csproj
dotnet run --project SQLIndexVisualizer/SQLIndexVisualizer.csproj
```

Target: `net10.0`, `WinExe` (Windows only).

## Stack

| Concern | Package |
| --- | --- |
| UI framework | Avalonia 12.0.4 + FluentTheme, dark mode |
| Charting | ScottPlot.Avalonia 5.1.59 |
| SQL Server | Microsoft.Data.SqlClient 5.2.2 |
| MVVM | CommunityToolkit.Mvvm 8.3.2 |

Solution file is `.slnx` (new .NET 9+ format).

## Project layout

```text
SQLIndexVisualizer/
  Models/           IndexInfo, PageData (PageSort, PageDensity, PageRead), TableGroup, IndexItem
  Services/         SqlServerService.cs  (all DB I/O)
  ViewModels/       MainWindowViewModel.cs
  Views/            MainWindow.axaml + MainWindow.axaml.cs
                    ReorganizeDialog.axaml + .cs
                    RebuildDialog.axaml + .cs
scripts/            IndexPageInfo.sql, demo scripts
pictures/           development screenshots
```

## Architecture notes

- **MVVM pattern** — ViewModel exposes `ChartDataChanged` event; code-behind subscribes and calls `RefreshChart()` via `Dispatcher.UIThread.Post`.
- **Chart rendering lives entirely in code-behind** (`RefreshChart` in `MainWindow.axaml.cs`). ScottPlot's `AvaPlot` needs imperative calls.
- **Elapsed timer** — a single `DispatcherTimer` (1 s interval) drives `ElapsedSeconds` while `IsAnalyzing` is true; started/stopped in `OnVmPropertyChanged`.
- **Context menu resolution** — code-behind walks `MenuItem → ContextMenu → PlacementTarget.DataContext` to get the `IndexItem`.
- **Dialog registration pattern** — ViewModel exposes `Func<…>` properties (`ShowReorganizeDialogAsync`, `ShowRebuildDialogAsync`) that are set by `MainWindow` code-behind in `OnDataContextChanged`. This keeps the ViewModel free of any Window reference.

## Gotchas — read before editing

### `AvaPlot` must never start hidden

`AvaPlot` initialises its Skia render surface on first layout pass. If `IsVisible=false` at startup it never initialises and the chart stays blank. The overlay states (welcome, analyzing, error) are placed **on top** of the always-visible `AvaPlot` using opaque `Border` wrappers in a stacked `Grid`.

### `FILLFACTOR` is a reserved SQL Server keyword

The column alias **must** be bracketed: `i.fill_factor AS [FillFactor]`. Unbracketed causes a syntax error.

### `ScottPlot.Color` vs `Avalonia.Media.Color` ambiguity

Both namespaces define `Color`. Resolved with:

```csharp
using SPColor = ScottPlot.Color;
using Avalonia.Media;          // kept for Brush etc.
```

All ScottPlot color calls use `SPColor.FromHex(...)`.

### Activity tab — AvaPlot in TabItem

Avalonia 12's `TabControl` keeps all `TabItem` content in the visual tree from startup, with non-selected items hidden via `IsVisible=false`. This triggers the same ScottPlot Skia init problem as the Analysis tab. Fix: `ActivityChart` has no `IsVisible` binding; the empty state uses an opaque `Border` overlay (same pattern as `IndexChart`).

### Avalonia 12 TreeView hierarchy

Use `Window.DataTemplates` with separate `TreeDataTemplate` and `DataTemplate` entries. Do **not** set `ItemTemplate` inside `TreeDataTemplate` — it causes AVLN2000 compile errors.

### Inline SQL and `CommandTimeout`

Analysis runs `IndexPageInfo.sql` as inline T-SQL (not a stored procedure). The `DECLARE @pObjectID` / `@pIndexID` lines are prepended with the actual `int` values before execution — safe because both are integers, not user strings. `CommandTimeout` is set to `600` seconds; the script calls `DBCC PAGE` in a loop and can run for many minutes on large indexes.

### `PageRead` can be zero

`bReadMicroSec` from `DBCC PAGE` is zero for pages still in the buffer pool (never read from disk in the current server uptime). `AvgPageRead` will therefore be low or zero on a warm server — this is expected, not a bug.

### Dialog result pattern

`ReorganizeDialog` and `RebuildDialog` use the non-generic `ShowDialog(owner)` (returns `Task`). The dialogs expose a `Confirmed` property (and `SelectedFillFactor` for rebuild) that the caller reads after the dialog closes. Do **not** use `ShowDialog<bool>` or `ShowDialog<int?>` — Avalonia does not reliably unbox value types from `Close(value)`.

## Default connection values

| Setting | Default |
| --- | --- |
| Server | `localhost` |
| Database | `StackOverflow2013` |

## Chart layers (RefreshChart)

1. Scatter points — PageSort (X) vs PageDensity % (Y), blue `#4472C4`, cross markers
2. Rolling average line — red `#E05252`, 3 px, window = `max(5, count / 100)`
3. Average density horizontal line — teal `#4EC9B0`, dashed
4. Fill factor horizontal line — yellow `#DCDCAA`, dotted (hidden when `fill_factor = 0`)

## Index domain knowledge

### B-tree internals (Paul Randal)

- SQL Server stores table/index data in 8 KB **pages**; 8 contiguous pages form an **extent**.
- A B-tree index has **non-leaf levels** (navigation) and a **leaf level** (data rows for CI, key + RID/bookmark for NCI).
- A **clustered index (CI)** *is* the table — the leaf level holds the actual data rows.
- A **non-clustered index (NCI)** leaf holds the NCI key + a pointer back to the CI key (or heap RID).
- **Page splits**: when a new row must be inserted into a full page, SQL Server allocates a new page and moves ~50 % of the rows to it. This creates a new page that is only ~50 % full (internal fragmentation) and potentially a non-contiguous extent (external fragmentation).

### Two kinds of fragmentation

| Type | What it is | Measured by |
| --- | --- | --- |
| **External** (logical scan fragmentation) | Leaf pages are not contiguous on disk / in the extent chain | `avg_fragmentation_in_percent` in `sys.dm_db_index_physical_stats` |
| **Internal** (page density) | Leaf pages are not full | `avg_page_space_used_in_percent` / the DBCC PAGE-based `PageDensity` this app computes |

- External fragmentation hurts **sequential/range scans** because the storage engine cannot do efficient read-ahead.
- Internal fragmentation hurts **every workload** because more pages must be read (and cached) to return the same number of rows.
- Paul Randal's key insight: fragmentation only matters if SQL Server *chooses* a range scan. For singleton lookups (SEEK) external fragmentation is irrelevant.

### Hot spots and the "DNA" chart (Jeff Moden)

The scatter plot (PageSort on X, PageDensity on Y) is the index's "DNA":

| Pattern | What it means |
| --- | --- |
| Uniform high density (≥ fill factor) | Healthy, rarely updated index — good candidate for higher fill factor |
| Uniformly low density | Too-aggressive maintenance or fill factor too low — wasted buffer pool space |
| Sawtooth / stepped density | Regular page splits from sequential inserts followed by maintenance cycles |
| **Last-page hot spot**: last N pages drop to ~50 % density, rest is uniform | Sequential key inserts (identity / sequential GUID) — only the rightmost leaf page splits; rest stays stable. Ola will keep rebuilding the whole index when only the tail needs attention |
| **Random hot spot**: density variable throughout, no spatial pattern | NEWID()-style GUIDs or random updates causing splits uniformly across the tree — fill factor should be reduced globally |
| Mid-range density dip | Deletions without subsequent inserts in that key range — REORGANIZE reclaims; REBUILD overkill |

### Ola Hallengren maintenance thresholds

`IndexOptimize` uses `avg_fragmentation_in_percent` from `sys.dm_db_index_physical_stats(… 'LIMITED')` to decide:

| Fragmentation | Default action |
| --- | --- |
| < 5 % | Nothing |
| 5 – 30 % | `ALTER INDEX … REORGANIZE` (online, page-level compaction, no full lock) |
| > 30 % | `ALTER INDEX … REBUILD` (offline by default; `ONLINE=ON` for Enterprise) |

**REORGANIZE** compacts pages in-place: fixes external fragmentation and improves page density up toward fill factor, but cannot change the fill factor itself.

**REBUILD** drops and recreates the index from scratch: fixes both fragmentation types, applies a new fill factor, and updates statistics. It is the only way to change fill factor without dropping the index.

Gap this app fills: `avg_fragmentation_in_percent` is a single number — it hides *where* fragmentation lives. The DNA chart reveals whether fragmentation is uniform (random pattern → lower fill factor globally), tail-only (sequential inserts → partial rebuild or targeted maintenance), or post-delete gaps (REORGANIZE sufficient). This guides tuning the `@FragmentationLevel1` / `@FragmentationLevel2` / `@FillFactor` parameters passed to `IndexOptimize`.
