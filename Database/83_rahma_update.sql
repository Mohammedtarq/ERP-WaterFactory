/* ============================================================
   «تحديث من الرحمة»: سجل كل تحديث (بيانات أساسية) وتغييراته بالقيمة القديمة والجديدة ليمكن التراجع عن آخر تحديث.
   - LegacyUpdateRuns: تحديث واحد (قاعدة المصدر، من نفّذه، متى، ملخص، نسخة احتياطية سبقته، وإن أُلغي).
   - LegacyUpdateChanges: سطر لكل تغيير (أُنشئ / عُدّل) مع الحقل والقيمة قبل وبعد.
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF OBJECT_ID('LegacyUpdateRuns', 'U') IS NULL
CREATE TABLE LegacyUpdateRuns (
    Id               INT IDENTITY(1,1) PRIMARY KEY,
    SourceDatabase   NVARCHAR(200) NOT NULL,
    AppliedAt        DATETIME2     NOT NULL CONSTRAINT DF_LUR_At DEFAULT SYSUTCDATETIME(),
    AppliedByUserId  INT           NOT NULL,
    Summary          NVARCHAR(1000) NULL,
    PreBackupPath    NVARCHAR(500) NULL,
    UndoneAt         DATETIME2     NULL,
    UndoneByUserId   INT           NULL,
    UndoReason       NVARCHAR(500) NULL,
    CONSTRAINT FK_LUR_User FOREIGN KEY (AppliedByUserId) REFERENCES Users(Id)
);
GO
IF OBJECT_ID('LegacyUpdateChanges', 'U') IS NULL
CREATE TABLE LegacyUpdateChanges (
    Id         INT IDENTITY(1,1) PRIMARY KEY,
    RunId      INT           NOT NULL,
    EntityType NVARCHAR(30)  NOT NULL,
    EntityId   INT           NOT NULL,
    LegacyKey  NVARCHAR(100) NULL,
    Action     NVARCHAR(10)  NOT NULL,
    Field      NVARCHAR(50)  NULL,
    OldValue   NVARCHAR(500) NULL,
    NewValue   NVARCHAR(500) NULL,
    CONSTRAINT FK_LUC_Run FOREIGN KEY (RunId) REFERENCES LegacyUpdateRuns(Id)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_LUC_Run')
    CREATE INDEX IX_LUC_Run ON LegacyUpdateChanges(RunId);
GO
