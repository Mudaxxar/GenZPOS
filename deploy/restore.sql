-- GenZ-POS: restore a .bak file over the shop database. Run by restore.cmd:
--   sqlcmd -S <server> -E -d master -b -v BAK="<file>" DB="<database>" -i restore.sql
-- Works on the same PC (keeps the current data file locations) and on a new PC
-- (puts the files in SQL Server's default data/log folders).
SET NOCOUNT ON;

DECLARE @bak nvarchar(4000) = N'$(BAK)';
DECLARE @db sysname = N'$(DB)';
DECLARE @sql nvarchar(max);

-- Logical file names inside the backup (column list of SQL Server 2016+).
DECLARE @files TABLE (
    LogicalName nvarchar(128), PhysicalName nvarchar(260), [Type] char(1), FileGroupName nvarchar(128),
    Size numeric(20,0), MaxSize numeric(20,0), FileId bigint, CreateLSN numeric(25,0), DropLSN numeric(25,0),
    UniqueId uniqueidentifier, ReadOnlyLSN numeric(25,0), ReadWriteLSN numeric(25,0), BackupSizeInBytes bigint,
    SourceBlockSize int, FileGroupId int, LogGroupGUID uniqueidentifier, DifferentialBaseLSN numeric(25,0),
    DifferentialBaseGUID uniqueidentifier, IsReadOnly bit, IsPresent bit, TDEThumbprint varbinary(32),
    SnapshotUrl nvarchar(360));

SET @sql = N'RESTORE FILELISTONLY FROM DISK = N''' + REPLACE(@bak, N'''', N'''''') + N'''';
INSERT INTO @files EXEC (@sql);

-- Where the files go: the current database's files, or SQL Server's default folders.
DECLARE @dataFile nvarchar(512), @logFile nvarchar(512);
IF DB_ID(@db) IS NOT NULL
BEGIN
    SELECT TOP (1) @dataFile = physical_name FROM sys.master_files WHERE database_id = DB_ID(@db) AND type = 0 ORDER BY file_id;
    SELECT TOP (1) @logFile  = physical_name FROM sys.master_files WHERE database_id = DB_ID(@db) AND type = 1 ORDER BY file_id;
END
IF @dataFile IS NULL SET @dataFile = CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(512)) + @db + N'.mdf';
IF @logFile  IS NULL SET @logFile  = CAST(SERVERPROPERTY('InstanceDefaultLogPath')  AS nvarchar(512)) + @db + N'_log.ldf';

DECLARE @move nvarchar(max) = N'';
SELECT @move = @move + N', MOVE N''' + REPLACE(LogicalName, N'''', N'''''') + N''' TO N''' +
       REPLACE(CASE WHEN [Type] = 'L' THEN @logFile
                    WHEN FileId = 1 THEN @dataFile
                    ELSE @dataFile + N'_' + CAST(FileId AS nvarchar(20)) + N'.ndf' END, N'''', N'''''') + N''''
FROM @files;

PRINT 'Restoring ' + @db + ' from ' + @bak + ' ...';

IF DB_ID(@db) IS NOT NULL
BEGIN
    -- Throw everyone out (GenZ-POS should already be closed on all PCs).
    SET @sql = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE';
    EXEC (@sql);
END

BEGIN TRY
    SET @sql = N'RESTORE DATABASE ' + QUOTENAME(@db) + N' FROM DISK = N''' + REPLACE(@bak, N'''', N'''''') + N''''
             + N' WITH REPLACE, RECOVERY' + @move;
    EXEC (@sql);
END TRY
BEGIN CATCH
    IF DB_ID(@db) IS NOT NULL
    BEGIN
        SET @sql = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET MULTI_USER';
        EXEC (@sql);
    END;
    THROW;
END CATCH

SET @sql = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET MULTI_USER';
EXEC (@sql);

PRINT 'Restore finished.';
