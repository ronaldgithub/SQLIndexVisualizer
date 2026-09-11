-- Proc version of IndexPageInfo.sql.
-- Install in jeffmoden (or any target database) and call while connected
-- to that database, since DBCC PAGE below resolves DB_NAME() at call time.
--
-- Usage:
--   EXEC dbo.usp_IndexPageInfo @TableName = 'dbo.GoodInt';
--   EXEC dbo.usp_IndexPageInfo @TableName = 'dbo.BadGUID', @IndexID = 1;
--
-- Requires DBCC PAGE permission (sysadmin, or ALTER TRACE / VIEW SERVER STATE
-- depending on version) - same requirement as running the script inline.

CREATE OR ALTER PROCEDURE dbo.usp_IndexPageInfo
     @TableName NVARCHAR(261)
    ,@IndexID   INT = 1
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @pObjectID INT = OBJECT_ID(@TableName);
    DECLARE @pIndexID  INT = @IndexID;

    IF @pObjectID IS NULL
    BEGIN
        RAISERROR('Table "%s" not found in the current database.', 16, 1, @TableName);
        RETURN;
    END;

    DROP TABLE IF EXISTS #IndexPageSpace;
    DROP TABLE IF EXISTS #PageInfo;

    CREATE TABLE #IndexPageSpace(
         PageSort    INT        NOT NULL
        ,FileID      SMALLINT   NOT NULL
        ,PageID      INT        NOT NULL
        ,PageDensity FLOAT
        ,PageRead    INT
    );

    CREATE TABLE #PageInfo(
         ParentObject   VARCHAR(255)
        ,Object         VARCHAR(255)
        ,Field          VARCHAR(255)
        ,Value          VARCHAR(255)
    );

    DECLARE
         @Counter       INT
        ,@IndexColsCSV  NVARCHAR(4000) = NULL
        ,@LeafPageCount INT
        ,@MaxPageSort   INT
        ,@Obj2PartName  NVARCHAR(261)
        ,@PageDensity   FLOAT
        ,@PageFreeBytes SMALLINT
        ,@PageRowCount  SMALLINT
        ,@PageUsedBytes SMALLINT
        ,@SampleSize    INT
        ,@SQL           NVARCHAR(MAX)
        ,@PageRead      INT;

    -- Build 2-part name
    SET @Obj2PartName = QUOTENAME(OBJECT_SCHEMA_NAME(@pObjectID)) + N'.' + QUOTENAME(OBJECT_NAME(@pObjectID));

    -- Get leaf page count and sample size
    SELECT
         @LeafPageCount = in_row_used_page_count
        ,@SampleSize    = POWER(10, CEILING(LOG10(in_row_used_page_count)) - 5)
    FROM sys.dm_db_partition_stats
    WHERE object_id = @pObjectID
      AND index_id  = @pIndexID;

    SET @SampleSize = CASE WHEN @SampleSize > 0 THEN @SampleSize ELSE 1 END;

    -- Build ORDER BY list from index key columns
    ;WITH cteIndexParts AS
    (
        SELECT
             idxcol.key_ordinal
            ,KeyColName = INDEX_COL(@Obj2PartName, @pIndexID, idxcol.key_ordinal)
            ,Direction  = CASE WHEN idxcol.is_descending_key = 0 THEN N'ASC' ELSE N'DESC' END
        FROM sys.index_columns idxcol
        WHERE idxcol.object_id = @pObjectID
          AND idxcol.index_id  = @pIndexID
          AND idxcol.key_ordinal > 0
    )
    SELECT @IndexColsCSV = STRING_AGG(QUOTENAME(KeyColName) + N' ' + Direction, N', ')
           WITHIN GROUP (ORDER BY key_ordinal)
    FROM cteIndexParts;

    -- Build dynamic SQL (clean version)
    SET @SQL = N'
    ;WITH cteBaseSortOrder AS
    (
        SELECT
            PhysLoc   = SUBSTRING(%%physloc%%,1,6),
            SortOrder = ROW_NUMBER() OVER (ORDER BY ' + @IndexColsCSV + N')
        FROM ' + @Obj2PartName + N' WITH (INDEX(' + CAST(@pIndexID AS nvarchar(10)) + N'))
    ),
    cteLogicalPageOrder AS
    (
        SELECT
            PhysLoc,
            PageSort = ROW_NUMBER() OVER (ORDER BY MIN(SortOrder)) - 1
        FROM cteBaseSortOrder
        GROUP BY PhysLoc
    )
    INSERT INTO #IndexPageSpace WITH (TABLOCK)
            (PageSort, FileID, PageID)
    SELECT
        PageSort,
        FileID = CONVERT(smallint, SUBSTRING(PhysLoc,6,1) + SUBSTRING(PhysLoc,5,1)),
        PageID = CONVERT(int,      SUBSTRING(PhysLoc,4,1) + SUBSTRING(PhysLoc,3,1)
                                   + SUBSTRING(PhysLoc,2,1) + SUBSTRING(PhysLoc,1,1))
    FROM cteLogicalPageOrder
    WHERE PageSort % ' + CAST(@SampleSize AS nvarchar(10)) + N' = 0;
    ';

    EXEC (@SQL);

    -- Add PK for faster lookups
    ALTER TABLE #IndexPageSpace ADD PRIMARY KEY CLUSTERED (PageSort);

    -- Loop through sampled pages
    SELECT
         @MaxPageSort = MAX(PageSort)
        ,@Counter     = 0
    FROM #IndexPageSpace;

    WHILE @Counter <= @MaxPageSort
    BEGIN
        TRUNCATE TABLE #PageInfo;

        SELECT @SQL = N'DBCC PAGE (' + QUOTENAME(DB_NAME()) + N','
                       + CAST(FileID AS nvarchar(10)) + N','
                       + CAST(PageID AS nvarchar(10)) + N',0)
                       WITH NO_INFOMSGS, TABLERESULTS;'
        FROM #IndexPageSpace
        WHERE PageSort = @Counter;

        INSERT INTO #PageInfo (ParentObject,Object,Field,Value)
        EXEC (@SQL);

        SELECT
             @PageRowCount  = MAX(CASE WHEN Field = N'm_slotCnt' THEN Value ELSE 0 END)
            ,@PageFreeBytes = MAX(CASE WHEN Field = N'm_freeCnt' THEN Value ELSE 0 END)
            ,@PageRead      = MAX(CASE WHEN Field = N'bReadMicroSec' THEN Value ELSE 0 END)
            ,@PageUsedBytes = 8096 - @PageFreeBytes
            ,@PageDensity   = (@PageUsedBytes * 100.0) / 8096.0
        FROM #PageInfo;

        UPDATE #IndexPageSpace
           SET PageDensity = @PageDensity,
               PageRead    = @PageRead
         WHERE PageSort    = @Counter;

        SET @Counter += @SampleSize;
    END;

    -- Final output
    SELECT PageSort, PageDensity, PageRead
    FROM #IndexPageSpace
    ORDER BY PageSort;
END;
GO

/*
================================================================================
SAMPLE RUN -- dbo.GoodINT, jeffmoden database
================================================================================
EXEC dbo.usp_IndexPageInfo @TableName = 'dbo.GoodINT';

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
than random keys (GUID) - see goodINT.sql for the ASCII picture of a page
split and why it only happens "in the middle" for random keys.

PageRead = 581 microseconds, identical for every sampled page, tells you
these pages were all pulled from disk together within this one DBCC PAGE
loop (see goodINT.sql's primer: a value of 0 would mean "already sitting
in the buffer pool"). Run the proc a second time right after and expect
PageRead to drop to 0 for most pages, since they're now warm in memory.
================================================================================
*/
