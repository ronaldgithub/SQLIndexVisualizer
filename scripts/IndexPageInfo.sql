DECLARE @pObjectID INT = OBJECT_ID('dbo.Posts');
DECLARE @pIndexID  INT = 1;

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
WITH cteIndexParts AS
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

PRINT @SQL;
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
