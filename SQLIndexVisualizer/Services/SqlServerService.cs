using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SQLIndexVisualizer.Models;

namespace SQLIndexVisualizer.Services;

public class SqlServerService
{
    private string _masterConnectionString = string.Empty;
    private string _dbConnectionString = string.Empty;

    public void SetServer(string server)
    {
        _masterConnectionString =
            $"Server={server};Database=master;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=15;";
    }

    public void SetDatabase(string server, string database)
    {
        _dbConnectionString =
            $"Server={server};Database={database};Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=15;";
    }

    public async Task<List<string>> GetDatabasesAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT name
            FROM sys.databases
            WHERE state = 0
              AND database_id > 4
            ORDER BY name
            """;

        var result = new List<string>();
        await using var conn = new SqlConnection(_masterConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));
        return result;
    }

    public async Task TestConnectionAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_masterConnectionString);
        await conn.OpenAsync(ct);
    }

    public async Task<List<TableGroup>> GetTablesAndIndexesAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                s.name           AS SchemaName,
                t.name           AS TableName,
                i.name           AS IndexName,
                i.index_id       AS IndexId,
                i.object_id      AS ObjectId,
                i.type_desc      AS IndexType,
                i.fill_factor    AS [FillFactor]
            FROM sys.indexes     i
            JOIN sys.objects     t ON i.object_id  = t.object_id
            JOIN sys.schemas     s ON t.schema_id  = s.schema_id
            WHERE t.type       = 'U'
              AND i.type       IN (1, 2)
              AND i.is_disabled = 0
              AND i.name IS NOT NULL
            ORDER BY s.name, t.name, i.index_id
            """;

        var groups = new Dictionary<string, TableGroup>(StringComparer.OrdinalIgnoreCase);
        await using var conn = new SqlConnection(_dbConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var schema = reader.GetString(0);
            var table  = reader.GetString(1);
            var key    = $"{schema}.{table}";

            if (!groups.TryGetValue(key, out var group))
            {
                group = new TableGroup { Schema = schema, TableName = table };
                groups[key] = group;
            }

            group.Indexes.Add(new IndexItem
            {
                Schema    = schema,
                TableName = table,
                IndexName = reader.GetString(2),
                IndexId   = reader.GetInt32(3),
                ObjectId  = reader.GetInt32(4),
                IndexType = reader.GetString(5),
                FillFactor = reader.GetByte(6)
            });
        }

        return new List<TableGroup>(groups.Values);
    }

    public async Task<List<PageData>> AnalyzeIndexAsync(
        int objectId, int indexId, CancellationToken ct = default)
    {
        var sql = $"""
            DECLARE @pObjectID INT = {objectId};
            DECLARE @pIndexID  INT = {indexId};

            {IndexPageInfoSql}
            """;

        await using var conn = new SqlConnection(_dbConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 600 };

        var pages = new List<PageData>();
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.Default, ct);

        // IndexPageInfo may emit PRINT/intermediate output; scan for the final SELECT result set
        // identified by its first column name.
        do
        {
            if (reader.FieldCount == 0) continue;
            if (reader.GetName(0) != "PageSort") continue;

            while (await reader.ReadAsync(ct))
            {
                pages.Add(new PageData
                {
                    PageSort    = Convert.ToInt32(reader["PageSort"]),
                    PageDensity = reader["PageDensity"] is DBNull ? 0 : Convert.ToDouble(reader["PageDensity"]),
                    PageRead    = reader["PageRead"]    is DBNull ? 0 : Convert.ToInt32(reader["PageRead"])
                });
            }
        }
        while (await reader.NextResultAsync(ct));

        return pages;
    }

    public async Task<double> GetIndexFragmentationAsync(int objectId, int indexId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT TOP 1 avg_fragmentation_in_percent
            FROM sys.dm_db_index_physical_stats(DB_ID(), @objectId, @indexId, NULL, 'LIMITED')
            WHERE index_id = @indexId
            """;
        await using var conn = new SqlConnection(_dbConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        cmd.Parameters.AddWithValue("@objectId", objectId);
        cmd.Parameters.AddWithValue("@indexId", indexId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DBNull or null ? -1.0 : Convert.ToDouble(result);
    }

    public async Task ReorganizeIndexAsync(string schema, string table, string indexName,
        CancellationToken ct = default)
    {
        var sql = $"ALTER INDEX [{indexName}] ON [{schema}].[{table}] REORGANIZE";
        await ExecuteDdlAsync(sql, ct);
    }

    public async Task RebuildIndexAsync(string schema, string table, string indexName,
        bool online, int fillFactor = 0, CancellationToken ct = default)
    {
        var opts = new System.Collections.Generic.List<string>();
        if (online)      opts.Add("ONLINE = ON");
        if (fillFactor > 0) opts.Add($"FILLFACTOR = {fillFactor}");
        var withClause = opts.Count > 0 ? $"WITH ({string.Join(", ", opts)})" : string.Empty;
        var sql = $"ALTER INDEX [{indexName}] ON [{schema}].[{table}] REBUILD {withClause}";
        await ExecuteDdlAsync(sql, ct);
    }

    public async Task ExecuteWithMessagesAsync(string sql, Action<string> onMessage,
        bool useMasterConnection = false, CancellationToken ct = default)
    {
        var connStr = useMasterConnection ? _masterConnectionString : _dbConnectionString;
        await using var conn = new SqlConnection(connStr);
        conn.InfoMessage += (_, e) => onMessage(e.Message);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 3600 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task ExecuteDdlAsync(string sql, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_dbConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 3600 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Body of IndexPageInfo.sql without the top two DECLARE lines (those are prepended
    // at call time with the actual objectId/indexId int values).
    private const string IndexPageInfoSql = """
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

        -- Build dynamic SQL
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
        """;
}
