/* ============================================================
   المرحلة م5: استيراد بصمة ZKTeco (ملف attlog.dat)
   - رقم الموظف في جهاز البصمة يُربط ببطاقته
   - الشفت قد يعبر منتصف الليل (الخروج قبل الدخول = شفت ليلي، والبصمات بعد 12 تُحسب ليوم بداية الشفت)
   - شفت «دخول فقط» (المبيعات): بصمة الحضور تكفي والأجر كامل
   - سجل لكل عملية استيراد
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */

IF COL_LENGTH('Employees', 'FingerprintCode') IS NULL
    ALTER TABLE Employees ADD FingerprintCode NVARCHAR(20) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Employees_FingerprintCode')
    CREATE UNIQUE INDEX UX_Employees_FingerprintCode ON Employees (FingerprintCode) WHERE FingerprintCode IS NOT NULL;
GO

IF COL_LENGTH('Shifts', 'IsEntryOnly') IS NULL
    ALTER TABLE Shifts ADD IsEntryOnly BIT NOT NULL CONSTRAINT DF_Shifts_IsEntryOnly DEFAULT 0;
GO

IF OBJECT_ID('FingerprintImports', 'U') IS NULL
CREATE TABLE FingerprintImports (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    FileName            NVARCHAR(260)   NOT NULL,
    PeriodFrom          DATE            NOT NULL,
    PeriodTo            DATE            NOT NULL,
    Punches             INT             NOT NULL,
    DaysApplied         INT             NOT NULL,
    UnmappedCodes       NVARCHAR(400)   NULL,
    ImportedByUserId    INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    ImportedAt          DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
