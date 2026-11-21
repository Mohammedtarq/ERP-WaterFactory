/* ============================================================
   شاشة التلفاز في المعمل: نوع حساب العرض (مدير أو تلفاز) ورسالة ثابتة يكتبها المدير على الشاشة.
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF COL_LENGTH('ManagerAccounts', 'Kind') IS NULL
    ALTER TABLE ManagerAccounts ADD Kind VARCHAR(10) NOT NULL CONSTRAINT DF_ManagerAccounts_Kind DEFAULT 'Manager';
GO
IF COL_LENGTH('CloudSyncSettings', 'TvMessage') IS NULL
    ALTER TABLE CloudSyncSettings ADD TvMessage NVARCHAR(300) NULL;
GO
