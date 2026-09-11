-- GUID Demo Code
DROP TABLE IF EXISTS dbo.BadGUID;
CREATE TABLE dbo.BadGUID
(
  SomeGuid UNIQUEIDENTIFIER DEFAULT NEWID()
 , CONSTRAINT pk_badguid PRIMARY KEY CLUSTERED (SomeGuid)
);

SET NOCOUNT ON;
DECLARE @Counter INT = 1;

WHILE @Counter <= 100000
BEGIN
	INSERT INTO dbo.BadGUID DEFAULT VALUES
	SELECT @Counter += 1
END
GO

SELECT index_id
, index_level
, avg_fragmentation_in_percent 
, avg_fragment_size_in_pages
, avg_page_space_used_in_percent
, SizeMB =  page_count/128.0
FROM sys.dm_db_index_physical_stats (DB_ID(),OBJECT_ID('dbo.BadGUID'),NULL,NULL,'DETAILED');

/*
index_id  index_level  avg_fragmentation_in_percent  avg_fragment_size_in_pages  avg_page_space_used_in_percent  SizeMB
1         0            98.99396378269618              1                          62.12233753397578               3.882812
1         1            0                               1                          76.72967630343464               0.015625
1         2            0                               1                          0.5930318754633062              0.007812

Analysis:
Night-and-day difference from GoodINT. Leaf level (index_level 0) sits at
only 62.12% density with 98.99% fragmentation after the same 100,000
inserts. A random UNIQUEIDENTIFIER key (NEWID()) lands on a random existing
leaf page every single time, not just at the end of the table like an
IDENTITY does. Once that random page is full, SQL Server has to split it -
so nearly every page in the table has been split at least once, leaving
each one roughly half-empty and physically out of order relative to its
neighbors in the key chain. That's exactly what avg_fragmentation_in_percent
is measuring: 98.99% of pages are NOT followed on disk by their logical
next page.

Notice the table also needed a third b-tree level (index_level 2, the true
root) that GoodINT never needed - because pages are only ~62% full instead
of ~99.7% full, BadGUID needs roughly 1.6x as many leaf pages to hold the
same 100,000 rows (3.88 MB vs GoodINT's 1.26 MB, and that's even before
accounting for the wider 16-byte GUID key vs a 4-byte int), which pushes
the intermediate level past what a single root page can index directly.

index_level 1 here is a real, populated intermediate level (76.73% density)
- unlike GoodINT's index_level 1, which WAS the root and barely mattered.
index_level 2 (0.59% density) is now BadGUID's actual root page - still
just a handful of pointer rows, still not meaningful on its own.

This is the textbook "random hot spot" DNA pattern from CLAUDE.md: density
variable throughout, no spatial pattern, contrast this against GoodINT's
"last-page hot spot", where only the single rightmost page is ever
mid-fill and every other page stays packed. The fix Ola Hallengren's
IndexOptimize would apply here (>30% fragmentation) is REBUILD, not
REORGANIZE - REORGANIZE can compact pages back toward the fill factor, but
it can't stop the NEXT insert from splitting a different random page all
over again. The only real fixes are a sequential key, a lower fill factor
to absorb the churn, or accepting the maintenance cost.
*/

exec [dbo].[usp_IndexPageInfo] 'dbo.BadGUID'

/*
================================================================================
SAMPLE RUN -- exec [dbo].[usp_IndexPageInfo] 'dbo.BadGUID'
================================================================================
Result (497 sampled pages, PageSort 0-496):

  Min PageDensity   0.3087944    (PageSort 36  - a near-empty leftover page)
  Max PageDensity  99.4318181    (PageSort 191 - a rare, still-intact page)
  Typical range    ~50% - 70%    (the bulk of all 497 sampled pages)
  Average          ~62%          (matches the DMV's avg_page_space_used_in_percent
                                   of 62.12% for the leaf level almost exactly)
  PageRead              1132     (identical for every sampled page - see
                                   goodINT.sql's VISUAL PRIMER for what
                                   PageRead means)

Analysis (for students):
Compare this to GoodINT's sample run: there, 160 of 161 pages sat at an
IDENTICAL 99.88% and only the very last page dipped. Here, density jumps
around unpredictably from one PageSort to the next with no relationship to
position in the table - a page near the START (PageSort 36) can be almost
empty (0.31%) while a page in the MIDDLE (PageSort 191) can be almost full
(99.43%). That randomness IS the signature of a random clustered key: each
of the 100,000 NEWID() inserts could land anywhere in the tree, so every
page's "story" (how many times it has split, how recently, how full it
got before the next split hit it) is independent of its neighbors'.

The couple of pages sitting near 90-99% density (PageSort 191, 246, 298,
301, 306, 307, 385, 388, 393, 404...) aren't a "good" region - they're
just pages that haven't been hit by a split yet, purely by chance. Given
enough further random inserts, those pages would eventually get split
down to ~50% too. That's the core problem Ola Hallengren's REBUILD fixes
and REORGANIZE can't: REORGANIZE only compacts pages that already exist,
it can't stop tomorrow's random insert from splitting a page that looks
fine today.
================================================================================
*/

-- ============================================================================
-- STATISTICS IO / TIME -- real read cost of a full clustered index scan
-- ============================================================================
-- Same query as goodINT.sql's version, against BadGUID instead. COUNT(*) has
-- no choice but to scan every leaf page of the (only) clustered index, so
-- "logical reads" here IS the leaf page count - expect it to land close to
-- BadGUID's ~497 leaf pages vs GoodINT's ~161, even though both tables hold
-- exactly 100,000 rows. That gap IS the real-world cost of low page density:
-- more pages to read (and cache) for the same data.
SET STATISTICS IO ON;
SET STATISTICS TIME ON;

SELECT COUNT(*) AS TotalRows FROM dbo.BadGUID;

SET STATISTICS IO OFF;
SET STATISTICS TIME OFF;

/*
================================================================================
RESULT

Table 'BadGUID'. Scan count 1, logical reads 501, physical reads 0,
read-ahead reads 0, lob reads 0.
SQL Server Execution Times: CPU time = 15 ms, elapsed time = 8 ms.

Analysis (for students):
501 logical reads for the exact same 100,000 rows GoodINT covered in 163
(see goodINT.sql). That's the real, measurable cost of 62% average page
density instead of 99.7%: roughly 3x as many pages have to be read - and
cached - to return identical data. This is what Paul Randal's fragmentation
argument is actually about: not the percentage itself, but what it costs
in I/O and buffer pool pressure every single time this table gets scanned.

As with GoodINT, physical reads = 0 here (both tables were already warm in
the buffer pool), so this run isn't showing disk cost directly - on a
busier server, or right after a restart, BadGUID's extra ~340 pages would
mean ~340 extra physical disk reads too, not just extra cache lookups.
Also don't over-read the CPU/elapsed time gap (15 ms / 8 ms) - for queries
this cheap, timing noise dominates run to run; GoodINT's own elapsed time
(16 ms) was actually HIGHER despite doing 1/3 the logical reads, which is
exactly why logical reads - not wall-clock time - is the number worth
trusting here. On a real multi-GB table this same ~3x gap in reads-per-scan
is the difference between a scan finishing in seconds vs feeling like
forever, and it's why Ola Hallengren's IndexOptimize rebuilds indexes like
this one instead of leaving them alone.
================================================================================
*/



