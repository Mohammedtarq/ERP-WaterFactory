/* ============================================================
   نقل البيانات من نظام سابق (نظام الرحمة) — قابل لإعادة التنفيذ
   - سجل عملية النقل (مرة واحدة لكل مشروع) وخريطة المعرّفات القديمة ← الجديدة
   - وصفة الملصق الخاص بلا عميل = ملصق مناسبة عام (رمضان، عيد، زواج...)
   ============================================================ */
SET QUOTED_IDENTIFIER ON;
GO

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('CustomRecipes') AND name = 'CustomerId' AND is_nullable = 0)
    ALTER TABLE CustomRecipes ALTER COLUMN CustomerId INT NULL;
GO

IF OBJECT_ID('LegacyImports', 'U') IS NULL
CREATE TABLE LegacyImports (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    SourceSystem        NVARCHAR(60)    NOT NULL,              -- ALRAHMA
    SourceServer        NVARCHAR(200)   NOT NULL,
    SourceDatabase      NVARCHAR(128)   NOT NULL,
    CutoverDate         DATE            NOT NULL,              -- تاريخ الأرصدة الافتتاحية
    Summary             NVARCHAR(MAX)   NULL,                  -- ملخص ما نُقل (نص للعرض)
    ImportedByUserId    INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    ImportedAt          DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_LegacyImports_Source')
    CREATE UNIQUE INDEX UX_LegacyImports_Source ON LegacyImports (SourceSystem);
GO

IF OBJECT_ID('LegacyImportMap', 'U') IS NULL
CREATE TABLE LegacyImportMap (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    ImportId            INT             NOT NULL FOREIGN KEY REFERENCES LegacyImports(Id),
    EntityType          NVARCHAR(40)    NOT NULL,              -- Customer / Supplier / Item / Employee ...
    LegacyKey           NVARCHAR(300)   NOT NULL,              -- المعرّف أو المفتاح في النظام القديم
    NewId               INT             NOT NULL
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_LegacyImportMap_Entity')
    CREATE INDEX IX_LegacyImportMap_Entity ON LegacyImportMap (EntityType, LegacyKey);
GO
