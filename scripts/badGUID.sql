-- GUID Demo Code
DROP TABLE IF EXISTS dbo.BadGUID;
CREATE TABLE dbo.BadGUID
(
  SomeGuid UNIQUEIDENTIFIER DEFAULT NEWID()
 , PRIMARY KEY CLUSTERED (SomeGuid)
);

SET NOCOUNT ON;
DECLARE @Counter INT = 1;

WHILE @Counter <= 100000
BEGIN
	INSERT INTO dbo BadGUID DEFAULT VALUES
	SELECT @Counter += 1
END
GO

SELECT index_id
, index_level
, avg_fragmentation_in_percent 
, avg_fragment_size_in_pages
, avg_page_space_used_in_percent
, SizeMB =  page_count/128.0
FROM sys.dm db_index_physical stats (DB_ID(),OBJECT_ID('dbo.BadGUID'),NULL,NULL,DETAILED)

