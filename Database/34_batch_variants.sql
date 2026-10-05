/* ============================================================
   متغيرات المنتج التام في أمر يومي واحد
   - أمر اليوم يقبل المنتج نفسه أكثر من مرة (شرنك وكارتون، أساسي ومطعم)
   - التشغيلة تحمل وصفتها (العمود أُضيف في 09 لأن إجراء البيع يقرؤه): تعبئة القديم من سطور الأوامر
   - طلب التحميل يحدد المتغير لكل سطر (الفارغ = الأساسي)
   - قوالب أمر اليوم المعتاد
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */

-- سطر لكل (منتج × متغير × وحدة) بدل سطر واحد لكل منتج
IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_ProductionOrderLines_Item')
    ALTER TABLE ProductionOrderLines DROP CONSTRAINT UQ_ProductionOrderLines_Item;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_ProductionOrderLines_LineNo')
    CREATE UNIQUE INDEX UX_ProductionOrderLines_LineNo ON ProductionOrderLines (ProductionOrderId, [LineNo]);
GO

-- وحدة التعبئة وعدد العبوات كما سُجّلت (لتكرار آخر إنتاج وتقرير الإنتاج بالوحدة الفعلية)
IF COL_LENGTH('ProductionOrderLines', 'PackagingLevelId') IS NULL
    ALTER TABLE ProductionOrderLines ADD PackagingLevelId INT NULL CONSTRAINT FK_ProductionOrderLines_Level REFERENCES ItemPackagingLevels(Id),
                                         Packs DECIMAL(18,3) NULL;
GO

-- هوية التشغيلات المنتجة سابقًا من وصفة سطرها
UPDATE b SET b.CustomRecipeId = l.CustomRecipeId
FROM ItemBatches b JOIN ProductionOrderLines l ON l.OutputBatchId = b.Id
WHERE b.CustomRecipeId IS NULL AND l.CustomRecipeId IS NOT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ItemBatches_CustomRecipe')
    CREATE INDEX IX_ItemBatches_CustomRecipe ON ItemBatches (CustomRecipeId) WHERE CustomRecipeId IS NOT NULL;
GO

IF COL_LENGTH('RepLoadOrderLines', 'CustomRecipeId') IS NULL
    ALTER TABLE RepLoadOrderLines ADD CustomRecipeId INT NULL CONSTRAINT FK_RepLoadOrderLines_CustomRecipe REFERENCES CustomRecipes(Id);
GO

IF OBJECT_ID('DailyProductionTemplates', 'U') IS NULL
CREATE TABLE DailyProductionTemplates (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    Name                NVARCHAR(100)   NOT NULL CONSTRAINT UQ_DailyProductionTemplates_Name UNIQUE,
    CreatedByUserId     INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    UpdatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
IF OBJECT_ID('DailyProductionTemplateLines', 'U') IS NULL
CREATE TABLE DailyProductionTemplateLines (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    TemplateId          INT             NOT NULL FOREIGN KEY REFERENCES DailyProductionTemplates(Id) ON DELETE CASCADE,
    [LineNo]            INT             NOT NULL,
    FinishedItemId      INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),
    PackagingLevelId    INT             NOT NULL FOREIGN KEY REFERENCES ItemPackagingLevels(Id),
    CustomRecipeId      INT             NULL FOREIGN KEY REFERENCES CustomRecipes(Id),
    Packs               DECIMAL(18,3)   NOT NULL CHECK (Packs > 0)
);
GO
