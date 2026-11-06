/* ============================================================
   المزامنة السحابية (المرحلة 1): إعداد خدمة المزامنة وحالتها
   - عنوان الخادم السحابي ومفتاح المعمل (يُلصق نفسه في إعدادات الخادم)
   - آخر نجاح وآخر خطأ: يعرضهما النظام في شاشة «المزامنة السحابية» ومؤشر الحالة
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF OBJECT_ID('CloudSyncSettings', 'U') IS NULL
CREATE TABLE CloudSyncSettings (
    Id                  INT             NOT NULL PRIMARY KEY CHECK (Id = 1),
    ServerUrl           NVARCHAR(300)   NULL,
    AgentKey            NVARCHAR(128)   NULL,
    IsEnabled           BIT             NOT NULL DEFAULT 0,
    IntervalSeconds     INT             NOT NULL DEFAULT 20 CHECK (IntervalSeconds BETWEEN 5 AND 600),
    LastAttemptAt       DATETIME2       NULL,
    LastSuccessAt       DATETIME2       NULL,
    LastError           NVARCHAR(500)   NULL,
    LastErrorAt         DATETIME2       NULL,
    -- مجموع الحركات التي وصلت من الهواتف عبر الخادم
    ReceivedCount       INT             NOT NULL DEFAULT 0,
    LastSnapshotAt      DATETIME2       NULL,
    -- آخر من شغّل الخدمة (اسم الجهاز): للتنبيه إن عملت خدمتان على جهازين
    AgentMachine        NVARCHAR(100)   NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM CloudSyncSettings) INSERT INTO CloudSyncSettings (Id) VALUES (1);
GO
