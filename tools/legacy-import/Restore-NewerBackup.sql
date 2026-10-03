/* ======================================================================
   استعادة نسخة احتياطية أحدث لنظام الرحمة باسم جديد (لا يمس القاعدة الحالية)
   عدّل السطرين التاليين فقط ثم اضغط Execute
   ====================================================================== */
DECLARE @BackupFile NVARCHAR(500) = N'C:\RahmaBackup\ALRAHMA.bak';   -- مسار ملف النسخة الأحدث
DECLARE @NewName    SYSNAME       = N'ALRAHMA_NEW';                   -- اسم القاعدة الجديدة

SET NOCOUNT ON;
IF DB_ID(@NewName) IS NOT NULL
BEGIN
    RAISERROR(N'توجد قاعدة بهذا الاسم مسبقًا — اختر اسمًا آخر في @NewName', 16, 1);
    RETURN;
END;

DECLARE @DataDir NVARCHAR(500) = CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS NVARCHAR(500));
DECLARE @LogDir  NVARCHAR(500) = CAST(SERVERPROPERTY('InstanceDefaultLogPath')  AS NVARCHAR(500));

CREATE TABLE #Files (
    LogicalName NVARCHAR(128), PhysicalName NVARCHAR(260), [Type] CHAR(1), FileGroupName NVARCHAR(128),
    Size NUMERIC(20,0), MaxSize NUMERIC(20,0), FileId BIGINT, CreateLSN NUMERIC(25,0), DropLSN NUMERIC(25,0),
    UniqueId UNIQUEIDENTIFIER, ReadOnlyLSN NUMERIC(25,0), ReadWriteLSN NUMERIC(25,0), BackupSizeInBytes BIGINT,
    SourceBlockSize INT, FileGroupId INT, LogGroupGUID UNIQUEIDENTIFIER, DifferentialBaseLSN NUMERIC(25,0),
    DifferentialBaseGUID UNIQUEIDENTIFIER, IsReadOnly BIT, IsPresent BIT, TDEThumbprint VARBINARY(32), SnapshotUrl NVARCHAR(360));
DECLARE @ReadError NVARCHAR(2000) = NULL;
BEGIN TRY
    INSERT INTO #Files EXEC (N'RESTORE FILELISTONLY FROM DISK = N''' + @BackupFile + N'''');
END TRY
BEGIN CATCH
    SET @ReadError = ERROR_MESSAGE();
END CATCH;
IF NOT EXISTS (SELECT 1 FROM #Files)
BEGIN
    -- رسالة SQL Server الأصلية تحدد السبب: الملف غير موجود (error 2) أو لا صلاحية لقراءته (error 5) أو ليس نسخة صالحة
    DECLARE @Msg NVARCHAR(2400) = N'تعذّر قراءة ملف النسخة: ' + @BackupFile + NCHAR(13) + NCHAR(10) +
                                 N'السبب من SQL Server: ' + ISNULL(@ReadError, N'غير معروف');
    RAISERROR(@Msg, 16, 1);
    DROP TABLE #Files;
    RETURN;
END;

-- كل ملف يُنقل باسم جديد حتى لا يتعارض مع ملفات القاعدة الحالية
DECLARE @Move NVARCHAR(MAX) = N'';
SELECT @Move += N', MOVE N''' + LogicalName + N''' TO N''' +
                CASE WHEN [Type] = 'L' THEN @LogDir ELSE @DataDir END +
                @NewName + N'_' + CAST(FileId AS NVARCHAR(10)) + CASE WHEN [Type] = 'L' THEN N'.ldf' ELSE N'.mdf' END + N''''
FROM #Files;

DECLARE @Sql NVARCHAR(MAX) = N'RESTORE DATABASE [' + @NewName + N'] FROM DISK = N''' + @BackupFile + N''' WITH STATS = 10' + @Move + N';';
PRINT @Sql;
BEGIN TRY
    EXEC (@Sql);
END TRY
BEGIN CATCH
    DECLARE @Err NVARCHAR(2000) = N'فشلت الاستعادة: ' + ERROR_MESSAGE();
    RAISERROR(@Err, 16, 1);
    DROP TABLE #Files;
    RETURN;
END CATCH;
PRINT N'تمت الاستعادة بنجاح باسم ' + @NewName;

-- آخر تاريخ في المبيعات والإنتاج للتأكد أن النسخة هي الأحدث
EXEC (N'SELECT N''آخر فاتورة'' AS [البيان], MAX([date]) AS [التاريخ] FROM [' + @NewName + N'].dbo.Fwater
       UNION ALL SELECT N''آخر إنتاج'', MAX([datetime]) FROM [' + @NewName + N'].dbo.EntajDays
       UNION ALL SELECT N''آخر دفعة عميل'', MAX([date]) FROM [' + @NewName + N'].dbo.CustomerPay');
DROP TABLE #Files;
