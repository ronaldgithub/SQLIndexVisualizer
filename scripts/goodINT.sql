-- INT Demo Code
DROP TABLE IF EXISTS dbo.GoodINT;
CREATE TABLE dbo.GoodINT
(
  SomeInt INT IDENTITY(1,1)
 , CONSTRAINT pk_goodint PRIMARY KEY CLUSTERED (SomeInt)
);

SET NOCOUNT ON;
DECLARE @Counter INT = 1;

WHILE @Counter <= 100000
BEGIN
	INSERT INTO dbo.GoodINT DEFAULT VALUES
	SELECT @Counter += 1
END
GO

SELECT index_id
, index_level
, avg_fragmentation_in_percent 
, avg_fragment_size_in_pages
, avg_page_space_used_in_percent
, SizeMB =  page_count/128.0
FROM sys.dm_db_index_physical_stats (DB_ID(),OBJECT_ID('dbo.GoodINT'),NULL,NULL,'DETAILED');

/*
index_id  index_level  avg_fragmentation_in_percent  avg_fragment_size_in_pages  avg_page_space_used_in_percent  SizeMB
1         0            0.6211180124223602             40.25                       99.73480355819126               1.257812
1         1            0                               1                           25.833951074870274             0.007812

Analysis:
Leaf level (index_level 0) sits at 99.73% page density with only 0.62%
fragmentation after 100,000 sequential inserts. An ever-increasing IDENTITY
key always inserts at the rightmost edge of the clustered index, so pages
fill up in order and are written once, back-to-back on disk - no random
insert ever lands on an already-full page elsewhere in the tree, so there's
nothing to split. The only page below 100% is the current "hot" page at the
end of the leaf level, which is exactly what you'd expect mid-load.

The single non-leaf row (index_level 1) at 25.8% density is the root page -
its low fill % is irrelevant; root/intermediate pages are tiny relative to
the leaf and their density doesn't affect scan or seek performance the way
leaf density does.

This is the textbook "good" DNA pattern: uniform high density, near-zero
fragmentation, contrast this against BadGUID's random-GUID key, where each
insert lands on a random existing page, forcing constant 50/50 page splits
and driving density down and fragmentation up throughout the tree, not just
at one hot edge.
*/

/*
================================================================================
VISUAL PRIMER -- pages, fill level (density), and fragmentation
================================================================================

1) A PAGE is a fixed 8 KB slot. "Fill level" / "page density" = how much of
   that 8 KB is actually used by rows vs sitting empty.

     8 KB PAGE (100% full - GoodINT's normal leaf page)
     +--------------------------------------------------+
     | row | row | row | row | row | row | row | row |  |  <- ~0 free space
     +--------------------------------------------------+

     8 KB PAGE (50% full - right after a page split)
     +--------------------------------------------------+
     | row | row | row | row |                          |  <- ~50% free space
     +--------------------------------------------------+

   avg_page_space_used_in_percent is the average of this fill level across
   all pages. GoodINT = 99.73% -> pages are packed tight, almost no waste.

2) PAGE SPLIT: happens when a new row must go INTO a page that's already
   full. SQL Server allocates a new page and moves ~half the rows there.

     BEFORE (full page, new row must insert into the middle - BadGUID case)
     +-----------------+
     | A  B  C  D  ...H|   <- full, new row "D2" must go between D and E
     +-----------------+

     AFTER (split into two ~50% full pages)
     +----------+     +-------------+
     | A  B  C  D|---->| D2  E  F ...H|   <- 2 pages now, both ~50% full
     +----------+     +-------------+
       page 41            page 87         <- often NOT next to each other
                                              on disk!

   This is exactly what GoodINT avoids: an ever-increasing IDENTITY key
   never inserts "into the middle" of a page - it only ever appends to the
   last page, so nothing existing ever needs to split.

3) FRAGMENTATION: leaf pages are chained in logical key order (page 41
   points to the "next" page). Fragmentation = how often that NEXT logical
   page is NOT the physically next page on disk.

     NOT FRAGMENTED (GoodINT: 0.62%)          FRAGMENTED (BadGUID: high %)
     disk:   [P1][P2][P3][P4]                 disk:   [P1][P9][P3][P2]
     logical chain: P1->P2->P3->P4            logical chain: P1->P2->P3->P4
     (read-ahead streams straight              (each "next page" needs a
      through in physical order)                disk seek somewhere else -
                                                  kills range-scan speed)

   Sequential inserts write pages in order, so physical order matches
   logical order almost perfectly -> low fragmentation. Random-key inserts
   (BadGUID) allocate new pages wherever there's free space -> physical
   order scrambles relative to logical order -> high fragmentation.
================================================================================
*/
exec [dbo].[usp_IndexPageInfo] 'dbo.GoodINT'

/*
================================================================================
SAMPLE RUN -- exec [dbo].[usp_IndexPageInfo] 'dbo.GoodINT'
================================================================================
Result (161 sampled pages, PageSort 0-160):

  PageSort    PageDensity    PageRead
  0 .. 159    99.8764822     581       <- 160 pages, all identical
  160         77.0750988     581       <- the last page only

Analysis (for students):
160 of the 161 sampled pages sit at a near-identical 99.88% density. Under
a sequential IDENTITY key, once a page fills up it is never written to
again, so it stays packed forever. Page 160 - the very last page in key
order - is the one exception: it's the page currently accepting new rows,
so it's the only page ever caught mid-fill (77.08%).

This is the "last-page hot spot" pattern described in CLAUDE.md's DNA-chart
table: with an ever-increasing key, only the rightmost leaf page is ever
"in progress" - every other page is finished business and stays put.
Compare this to a random key (BadGUID): there, EVERY existing page is a
candidate for the next insert, so density stays uniformly lower and
fragmentation shows up across the whole table, not just at one tail page.
That's the whole reason sequential keys (IDENTITY) fragment so much less
than random keys (GUID) - see the page-split diagram above.

PageRead = 581 microseconds, identical for every sampled page, tells you
these pages were all pulled from disk together within this one DBCC PAGE
loop (a value of 0 would mean "already sitting in the buffer pool" - see
the VISUAL PRIMER above). Run the proc a second time right after and
expect PageRead to drop to 0 for most pages, since they're now warm in
memory.
================================================================================
*/

-- ============================================================================
-- STATISTICS IO / TIME -- real read cost of a full clustered index scan
-- ============================================================================
-- COUNT(*) against a table with only a clustered index has no choice but to
-- scan every leaf page, so "logical reads" here IS the leaf page count.
-- Run this in SSMS with "Messages" selected (Ctrl+Shift+T shows the STATISTICS
-- TIME/IO grid in some SSMS versions) and paste the output back for the
-- side-by-side comparison against BadGUID's version of this same query.
SET STATISTICS IO ON;
SET STATISTICS TIME ON;

SELECT COUNT(*) AS TotalRows FROM dbo.GoodINT;

SET STATISTICS IO OFF;
SET STATISTICS TIME OFF;

/*
================================================================================
RESULT

Table 'GoodINT'. Scan count 1, logical reads 163, physical reads 0,
read-ahead reads 0, lob reads 0.
SQL Server Execution Times: CPU time = 0 ms, elapsed time = 16 ms.

Analysis (for students):
163 logical reads to scan all 100,000 rows - almost exactly the 161 leaf
pages plus the couple of non-leaf pages reported by the fragmentation
query earlier. Compare against BadGUID's 501 logical reads for the SAME
100,000 rows (see badGUID.sql): roughly 3x more pages SQL Server had to
touch, purely because BadGUID's pages are only ~62% full instead of
~99.7% full. Every one of those extra ~340 reads is real work - more
pages pulled into the buffer pool, more of the buffer pool spent caching
the same amount of data, less room left for everything else on the server.

Note physical reads = 0 - the table was already sitting in the buffer
pool from the earlier DBCC PAGE work, so this comparison is purely about
LOGICAL page-touch cost, not disk I/O. Don't read too much into the
elapsed time (16 ms here vs BadGUID's 8 ms) - for queries this small and
cheap, timing is dominated by noise (plan caching, momentary server load),
not real work. Logical reads is the number that's actually comparable and
repeatable between runs, and the one that scales: on a real multi-GB
table, this same ~3x gap in reads-per-scan is the difference between a
scan finishing in seconds vs feeling like forever.
================================================================================
*/

