/* ============================================================
   المزامنة بنظامين معًا: الخادم المحلي (واي فاي المعمل) + الخادم السحابي (الإنترنت)
   - LocalEnabled: الخادم المحلي على حاسبة السيرفر (يثبّته المثبّت خدمةً في Windows)
   - LocalUrl: عنوانه للهواتف، تكتبه خدمة المزامنة تلقائيًا ويتحدّث إن تغيّر عنوان الحاسبة
   - Local*: حالة الخادم المحلي؛ أعمدة Last* الأصلية صارت للخادم السحابي
   - ترقية تجربة الواي فاي: عنوان داخل الشبكة في ServerUrl يُنقل إلى الخادم المحلي
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF COL_LENGTH('CloudSyncSettings', 'LocalEnabled') IS NULL
    ALTER TABLE CloudSyncSettings ADD LocalEnabled BIT NOT NULL CONSTRAINT DF_CloudSync_LocalEnabled DEFAULT 0;
IF COL_LENGTH('CloudSyncSettings', 'LocalUrl') IS NULL
    ALTER TABLE CloudSyncSettings ADD LocalUrl NVARCHAR(300) NULL;
IF COL_LENGTH('CloudSyncSettings', 'LocalLastSuccessAt') IS NULL
    ALTER TABLE CloudSyncSettings ADD LocalLastSuccessAt DATETIME2 NULL;
IF COL_LENGTH('CloudSyncSettings', 'LocalLastError') IS NULL
    ALTER TABLE CloudSyncSettings ADD LocalLastError NVARCHAR(500) NULL;
IF COL_LENGTH('CloudSyncSettings', 'LocalLastErrorAt') IS NULL
    ALTER TABLE CloudSyncSettings ADD LocalLastErrorAt DATETIME2 NULL;
GO
UPDATE CloudSyncSettings
SET LocalUrl = ServerUrl, LocalEnabled = 1, ServerUrl = NULL,
    LocalLastSuccessAt = LastSuccessAt, LocalLastError = LastError, LocalLastErrorAt = LastErrorAt,
    LastSuccessAt = NULL, LastError = NULL, LastErrorAt = NULL
WHERE ServerUrl LIKE N'http://192.168.%' OR ServerUrl LIKE N'http://10.%' OR ServerUrl LIKE N'http://172.%';
GO
