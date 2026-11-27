/* ============================================================
   قوالب التعبئة والوصفات المخصصة (قابل لإعادة التنفيذ)
   - قالب التعبئة (مثل 330×40 كارتون / 330×20 شرنك): أدوار مكونات بنسبها (كارتون 1 لكل 40، امبولة 1:1، لاصق 2:1)
   - تطبيق القالب على صنف يملأ قائمة مواده (مع حفظ دور كل مكوّن) — الصنف يُعرَّف مرة واحدة بوصفة ثابتة
   - بديل العميل يستبدل مكوّن دور معيّن بصنف مخزني مستقل (عبر الوصفات المخصصة)
   - الاستبدال على مستوى أمر واحد يُسجَّل بسببه دون تعديل تعريف الصنف
   ============================================================ */
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('PackagingTemplates', 'U') IS NULL
CREATE TABLE PackagingTemplates (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    Name            NVARCHAR(100)   NOT NULL UNIQUE,
    Description     NVARCHAR(300)   NULL,
    IsActive        BIT             NOT NULL DEFAULT 1
);
GO

IF OBJECT_ID('PackagingTemplateLines', 'U') IS NULL
CREATE TABLE PackagingTemplateLines (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    TemplateId          INT             NOT NULL FOREIGN KEY REFERENCES PackagingTemplates(Id) ON DELETE CASCADE,
    ComponentRole       NVARCHAR(50)    NOT NULL,              -- كارتون، امبولة، غطاء، لاصق...
    DefaultItemId       INT             NULL FOREIGN KEY REFERENCES Items(Id),
    ComponentQuantity   DECIMAL(18,4)   NOT NULL CHECK (ComponentQuantity > 0),   -- عدد المكوّن...
    PerUnits            DECIMAL(18,4)   NOT NULL CHECK (PerUnits > 0),            -- ...لكل هذا العدد من المنتج
    CONSTRAINT UQ_PackagingTemplateLines_Role UNIQUE (TemplateId, ComponentRole)
);
GO

IF COL_LENGTH('BillOfMaterials', 'PackagingTemplateId') IS NULL
    ALTER TABLE BillOfMaterials ADD PackagingTemplateId INT NULL CONSTRAINT FK_BOM_Template FOREIGN KEY REFERENCES PackagingTemplates(Id);
GO

IF COL_LENGTH('BOMLines', 'ComponentRole') IS NULL
    ALTER TABLE BOMLines ADD ComponentRole NVARCHAR(50) NULL;
GO

-- نسب مثل 1 لكل 24 تحتاج دقة أعلى من 4 منازل
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('BOMLines') AND name = 'QuantityPerUnit' AND scale < 6)
    ALTER TABLE BOMLines ALTER COLUMN QuantityPerUnit DECIMAL(18,6) NOT NULL;
GO

IF OBJECT_ID('ProductionOrderComponentOverrides', 'U') IS NULL
CREATE TABLE ProductionOrderComponentOverrides (
    Id                      INT IDENTITY(1,1) PRIMARY KEY,
    ProductionOrderLineId   INT             NOT NULL FOREIGN KEY REFERENCES ProductionOrderLines(Id),
    OriginalItemId          INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),
    ReplacementItemId       INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),
    Quantity                DECIMAL(18,4)   NOT NULL,
    Reason                  NVARCHAR(300)   NOT NULL,
    ChangedByUserId         INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    ChangedAt               DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ComponentOverrides_Line')
    CREATE INDEX IX_ComponentOverrides_Line ON ProductionOrderComponentOverrides (ProductionOrderLineId);
GO
