# SQL Index Visualizer

A dark-mode Windows desktop app for visualising SQL Server index page density, fragmentation, and live page-split / lock-wait activity — built on [Avalonia](https://avaloniaui.net/) and [ScottPlot](https://scottplot.net/).

## What it does

Connect to any SQL Server, pick a database, select an index, and click **Analyse**. The app samples every Nth page of the index using `DBCC PAGE` and plots **page density** (how full each 8 KB page is) against **logical page order**, together with a rolling average and fill-factor reference line. The result is the "DNA" of that index's physical state.

![SQL Index Visualizer screenshot](pictures/enh_03.jpg)

### Why this matters

`avg_fragmentation_in_percent` is a single number that hides *where* fragmentation lives. The scatter plot reveals the pattern:

| Pattern | Meaning |
| --- | --- |
| Uniform high density | Healthy, rarely updated index — raise the fill factor |
| Last-page density drop | Sequential inserts (identity / `NEWSEQUENTIALID`) — only the tail needs attention |
| Density variable throughout | Random key inserts (NEWID GUIDs) or distributed updates — lower fill factor globally |
| Mid-range density dip | Deletes without re-inserts — REORGANIZE is sufficient |

## Prerequisites

| Requirement | Notes |
| --- | --- |
| .NET 10 SDK | [Download](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Windows | WinExe target; Avalonia Desktop |
| SQL Server 2008+ | Windows auth (Trusted Connection) |
| `sysadmin` or `CONTROL SERVER` | Required for `DBCC PAGE` |

## Build & run

```powershell
git clone https://github.com/ronaldgithub/SQLIndexVisualizer.git
cd SQLIndexVisualizer
dotnet run --project SQLIndexVisualizer/SQLIndexVisualizer.csproj
```

Or open `SQLIndexVisualizer.slnx` in Visual Studio 2022 (17.12+) or Rider and hit Run.

## Usage

1. **Connect** — enter your server name (default: `localhost`) and click **Connect**.
2. **Load** — select a database from the dropdown and click **Load** to populate the index tree.
3. **Analyse** — select any index in the tree and click **Analyse**. The app samples the index pages and plots density. Large indexes can take several minutes.
4. **Maintenance tab** — two sub-tabs:
   - **Ola** — pre-filled `dbo.IndexOptimize` call (Ola Hallengren) for the selected index; edit parameters and click Execute.
   - **T-SQL** — free-form editor that runs against the selected database; select text to execute only the selection.
5. **Activity tab** — measure page splits and lock waits caused by a specific action:
   - Select an index, click **Set Baseline** to snapshot `sys.dm_db_index_operational_stats`.
   - Run your T-SQL (inserts, updates, etc.) in the Maintenance tab.
   - Click **Measure** — the delta (splits and lock waits) appears in the bar chart and history table.
   - Each Measure advances the baseline, so every action shows its own delta independently.

## Chart legend (Analysis tab)

| Series | Colour | Meaning |
| --- | --- | --- |
| Scatter points | Blue | Raw page density per sampled page |
| Rolling average | Red | Sliding-window smoothed density |
| Avg line (dashed) | Teal | Mean density across all sampled pages |
| Fill factor (dotted) | Yellow | Configured fill factor for this index |

## Stats bar

After analysis: pages sampled, table row count, index size, average / min / max page density, fill factor, average free space, index fragmentation (`sys.dm_db_index_physical_stats`), and average page read time in µs (`bReadMicroSec` from `DBCC PAGE`). Page read time is zero for pages already in the buffer pool — expected on a warm server.

## Activity tab metrics

Both counters come from `sys.dm_db_index_operational_stats`, measured as before/after deltas:

| Metric | Source column | What it means |
| --- | --- | --- |
| **Page Splits** | `leaf_allocation_count` | New leaf pages allocated — each split leaves a ~50 % full page |
| **Lock Waits** | `row_lock_wait_count` + `page_lock_wait_count` | Lock acquisitions that blocked — high numbers indicate contention |

## Scripts

The `scripts/` folder contains `IndexPageInfo.sql` — the T-SQL used to sample index pages (same logic as the embedded query).

## Stack

- [Avalonia 12](https://avaloniaui.net/) — cross-platform XAML UI framework
- [ScottPlot 5](https://scottplot.net/) — high-performance plotting
- [Microsoft.Data.SqlClient 5](https://github.com/dotnet/SqlClient) — SQL Server connectivity
- [CommunityToolkit.Mvvm 8](https://github.com/CommunityToolkit/dotnet) — MVVM source generators

## License

MIT
