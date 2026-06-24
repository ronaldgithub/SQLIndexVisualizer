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

    public async Task<bool> CheckSpIndexDnaExistsAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(1) FROM master.sys.objects WHERE name = 'sp_IndexDNA' AND type = 'P'";
        await using var conn = new SqlConnection(_masterConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        var count = (int)(await cmd.ExecuteScalarAsync(ct))!;
        return count > 0;
    }

    public async Task InstallSpIndexDnaAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_masterConnectionString);
        await conn.OpenAsync(ct);

        // Create the stored procedure in master
        var createSql = GetSpIndexDnaSql();
        await using var createCmd = new SqlCommand(createSql, conn) { CommandTimeout = 120 };
        await createCmd.ExecuteNonQueryAsync(ct);

        // Mark as system procedure so it can be called from any DB
        await using var markCmd = new SqlCommand("EXEC sp_ms_marksystemobject 'sp_IndexDNA'", conn) { CommandTimeout = 30 };
        await markCmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(IndexInfo info, List<PageData> pages)> AnalyzeIndexAsync(
        int objectId, int indexId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_dbConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("EXEC dbo.sp_IndexDNA @pObjectID, @pIndexID", conn)
        {
            CommandTimeout = 600
        };
        cmd.Parameters.AddWithValue("@pObjectID", objectId);
        cmd.Parameters.AddWithValue("@pIndexID", indexId);

        var info  = new IndexInfo();
        var pages = new List<PageData>();

        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.Default, ct);

        // sp_IndexDNA runs DBCC PAGE in a loop via INSERT...EXEC, which can leak
        // intermediate result sets to the client.  Scan every result set and
        // identify it by its first column name rather than assuming position.
        do
        {
            if (reader.FieldCount == 0) continue;

            var firstCol = reader.GetName(0);

            if (firstCol == "ServerName" && string.IsNullOrEmpty(info.ServerName))
            {
                if (await reader.ReadAsync(ct))
                {
                    info.ServerName = reader["ServerName"]?.ToString()  ?? string.Empty;
                    info.DBName     = reader["DBName"]?.ToString()      ?? string.Empty;
                    info.SchemaName = reader["SchemaName"]?.ToString()  ?? string.Empty;
                    info.ObjectName = reader["ObjectName"]?.ToString()  ?? string.Empty;
                    info.IndexName  = reader["IndexName"]?.ToString()   ?? string.Empty;
                    info.SampleDT   = reader["SampleDT"]?.ToString()    ?? string.Empty;
                }
            }
            else if (firstCol == "PageSort" && pages.Count == 0)
            {
                while (await reader.ReadAsync(ct))
                {
                    pages.Add(new PageData
                    {
                        PageSort    = Convert.ToInt32(reader["PageSort"]),
                        PageDensity = reader["PageDensity"] == DBNull.Value
                                          ? 0
                                          : Convert.ToDouble(reader["PageDensity"])
                    });
                }
            }
            // Any other result set (DBCC PAGE spillover, etc.) is intentionally skipped.
        }
        while (await reader.NextResultAsync(ct));

        return (info, pages);
    }

    public async Task ReorganizeIndexAsync(string schema, string table, string indexName,
        CancellationToken ct = default)
    {
        var sql = $"ALTER INDEX [{indexName}] ON [{schema}].[{table}] REORGANIZE";
        await ExecuteDdlAsync(sql, ct);
    }

    public async Task RebuildIndexAsync(string schema, string table, string indexName,
        bool online, CancellationToken ct = default)
    {
        var onlineOpt = online ? "WITH (ONLINE = ON)" : string.Empty;
        var sql = $"ALTER INDEX [{indexName}] ON [{schema}].[{table}] REBUILD {onlineOpt}";
        await ExecuteDdlAsync(sql, ct);
    }

    private async Task ExecuteDdlAsync(string sql, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_dbConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 3600 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string GetSpIndexDnaSql() => """
        USE [master]
        ;
        CREATE OR ALTER PROCEDURE [dbo].[sp_IndexDNA]
            @pObjectID INT
           ,@pIndexID  INT
        AS
        SET NOCOUNT ON;
        SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

        IF OBJECT_ID('tempdb..#IndexPageSpace') IS NOT NULL DROP TABLE #IndexPageSpace;
        IF OBJECT_ID('tempdb..#PageInfo')       IS NOT NULL DROP TABLE #PageInfo;

        CREATE TABLE #IndexPageSpace
            (
              PageSort      INT         NOT NULL
            , FileID        SMALLINT    NOT NULL
            , PageID        INT         NOT NULL
            , PageDensity   FLOAT
            )
        ;
        CREATE TABLE #PageInfo
            (
              ParentObject  VARCHAR(255)
            , Object        VARCHAR(255)
            , Field         VARCHAR(255)
            , Value         VARCHAR(255)
            )
        ;

        DECLARE  @Counter           INT
                ,@IndexColsCSV      NVARCHAR(4000)  = NULL
                ,@LeafPageCount     INT
                ,@MaxPageSort       INT
                ,@Obj2PartName      NVARCHAR(261)   = QUOTENAME(OBJECT_SCHEMA_NAME(@pObjectID))
                                                    + N'.'
                                                    + QUOTENAME(OBJECT_NAME(@pObjectID))
                ,@PageDensity       FLOAT
                ,@PageFreeBytes     SMALLINT
                ,@PageRowCount      SMALLINT
                ,@PageUsedBytes     SMALLINT
                ,@SampleSize        INT
                ,@SQL               NVARCHAR(MAX)
        ;

        SELECT  @LeafPageCount = in_row_used_page_count
               ,@SampleSize    = POWER(10, CONVERT(INT, CEILING(LOG(@LeafPageCount) / LOG(10)) - 5))
               ,@SampleSize    = CASE WHEN @SampleSize > 0 THEN @SampleSize ELSE 1 END
          FROM  sys.dm_db_partition_stats
         WHERE  object_id = @pObjectID
           AND  index_id  = @pIndexID
        ;

        WITH cteIndexParts AS
        (
          SELECT   idxcol.key_ordinal
                  ,KeyColName = INDEX_COL(@Obj2PartName, @pIndexID, key_ordinal)
                  ,Direction  = CASE WHEN idxcol.is_descending_key = 0 THEN N'ASC' ELSE N'DESC' END
            FROM  sys.indexes             idx
            JOIN  sys.index_columns       idxcol
                      ON idx.object_id    = idxcol.object_id
                     AND idx.index_id     = idxcol.index_id
           WHERE  idx.object_id           = @pObjectID
             AND  idx.index_id            = @pIndexID
             AND  idxcol.key_ordinal      > 0
        )
        SELECT @IndexColsCSV = ISNULL(@IndexColsCSV + N', ', '') + QUOTENAME(KeyColName) + ' ' + Direction
          FROM cteIndexParts
         ORDER BY key_ordinal
        ;

        SELECT  ServerName  = @@SERVERNAME
               ,DBName      = DB_NAME()
               ,SchemaName  = OBJECT_SCHEMA_NAME(@pObjectID)
               ,ObjectName  = OBJECT_NAME(@pObjectID)
               ,IndexName   = (SELECT name FROM sys.indexes WHERE object_id = @pObjectID AND index_id = @pIndexID)
               ,SampleDT    = CONVERT(CHAR(20), GETDATE(), 113)
        ;

        SELECT @SQL = REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(N'
            WITH
        cteBaseSortOrder AS
        (
          SELECT   PhysLoc     = SUBSTRING(%%physloc%%, 1, 6)
                  ,SortOrder   = ROW_NUMBER() OVER (ORDER BY <<@IndexColsCSV>>)
            FROM  <<@Obj2PartName>> WITH (INDEX(<<@pIndexID>>))
        )
        ,cteLogicalPageOrder AS
        (
          SELECT   Physloc
                  ,PageSort = ROW_NUMBER() OVER (ORDER BY MIN(SortOrder)) - 1
            FROM  cteBaseSortOrder
           GROUP BY PhysLoc
        )
          INSERT INTO #IndexPageSpace WITH (TABLOCK)
                      (PageSort, FileID, PageID)
          SELECT   PageSort
                  ,FileID  = CONVERT(SMALLINT, SUBSTRING(Physloc, 6, 1) + SUBSTRING(Physloc, 5, 1))
                  ,PageID  = CONVERT(INT      , SUBSTRING(Physloc, 4, 1) + SUBSTRING(Physloc, 3, 1) + SUBSTRING(Physloc, 2, 1) + SUBSTRING(Physloc, 1, 1))
            FROM  cteLogicalPageOrder
           WHERE  PageSort % <<@SampleSize>> = 0
        ;'
                  , N'"'                    , N'''')
                  , N'<<@IndexColsCSV>>'    , @IndexColsCSV)
                  , N'<<@Obj2PartName>>'    , @Obj2PartName)
                  , N'<<@pIndexID>>'        , CONVERT(NVARCHAR(10), @pIndexID))
                  , N'<<@SampleSize>>'      , CONVERT(NVARCHAR(10), @SampleSize))
        ;
          EXEC (@SQL)
        ;
          ALTER TABLE #IndexPageSpace ADD PRIMARY KEY CLUSTERED (PageSort)
        ;

          SELECT  @MaxPageSort = MAX(PageSort)
                 ,@Counter     = 0
            FROM  #IndexPageSpace
        ;

          WHILE @Counter <= @MaxPageSort
          BEGIN
                  TRUNCATE TABLE #PageInfo
                  ;
                  SELECT @SQL = REPLACE(REPLACE(REPLACE(
                                          N'DBCC PAGE (<<DB_Name>>, <<FileID>>, <<PageID>>, 0) WITH NO_INFOMSGS, TABLERESULTS;'
                                          , N'<<DB_Name>>', DB_NAME())
                                          , N'<<FileID>>', CONVERT(NVARCHAR(10), FileID))
                                          , N'<<PageID>>', CONVERT(NVARCHAR(10), PageID))
                    FROM  #IndexPageSpace
                   WHERE  PageSort = @Counter
                  ;
                  INSERT INTO #PageInfo (ParentObject, Object, Field, Value)
                    EXEC (@SQL)
                  ;
                  SELECT  @PageRowCount   = MAX(CASE WHEN Field = N'm_slotCnt'  THEN VALUE ELSE 0 END)
                         ,@PageFreeBytes  = MAX(CASE WHEN Field = N'm_freeCnt'  THEN VALUE ELSE 0 END)
                         ,@PageUsedBytes  = 8096 - @PageFreeBytes
                         ,@PageDensity    = @PageUsedBytes * 100.0 / 8096.0
                    FROM  #PageInfo
                  ;
                  UPDATE #IndexPageSpace
                     SET PageDensity = @PageDensity
                   WHERE PageSort    = @Counter
                  ;
                  SELECT @Counter = @Counter + @SampleSize
                  ;
          END
        ;
          SELECT PageSort, PageDensity
            FROM #IndexPageSpace
           ORDER BY PageSort
        ;
        """;
}
