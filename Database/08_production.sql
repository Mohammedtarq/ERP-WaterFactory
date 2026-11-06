/* ============================================================
   الإنتاج والمختبر — قائمة المواد BOM، الوصفات المخصصة،
   أمر الإنتاج، فحص الجودة، أمر التعبيئة
   ============================================================ */

-- ============ قائمة المواد الأساسية (BOM) لكل منتج نهائي ============
CREATE TABLE BillOfMaterials (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    FinishedItemId  INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),
    Name            NVARCHAR(150)   NOT NULL DEFAULT N'الوصفة الأساسية',
    IsActive        BIT             NOT NULL DEFAULT 1
);
GO

CREATE TABLE BOMLines (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    BOMId               INT             NOT NULL FOREIGN KEY REFERENCES BillOfMaterials(Id),
    RawMaterialItemId   INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),
    QuantityPerUnit     DECIMAL(18,4)   NOT NULL
);
GO

-- ============ الوصفات المخصصة لاسم تجاري (مطاعم/كافيهات) ============
CREATE TABLE CustomRecipes (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    FinishedItemId  INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),
    CustomerId      INT             NOT NULL FOREIGN KEY REFERENCES Customers(Id),
    Name            NVARCHAR(150)   NOT NULL,          -- مثال: وصفة مطعم الحسون
    IsActive        BIT             NOT NULL DEFAULT 1
);
GO

-- سطور تستبدل مكوّنات من الوصفة الأساسية (غطاء، لاصق أمامي/خلفي ...)
CREATE TABLE CustomRecipeLines (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    CustomRecipeId      INT             NOT NULL FOREIGN KEY REFERENCES CustomRecipes(Id),
    ComponentItemId     INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),  -- صنف مادة أولية خاص بهذا العميل
    ComponentLabel      NVARCHAR(100)   NOT NULL,       -- مثال: غطاء القنينة / لاصق أمامي / لاصق خلفي
    QuantityPerUnit     DECIMAL(18,4)   NOT NULL
);
GO

-- ============ أمر الإنتاج ============
CREATE TABLE ProductionOrders (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    MONumber            NVARCHAR(30)    NOT NULL UNIQUE,
    FinishedItemId      INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),
    BOMId               INT             NOT NULL FOREIGN KEY REFERENCES BillOfMaterials(Id),
    CustomRecipeId      INT             NULL FOREIGN KEY REFERENCES CustomRecipes(Id),   -- NULL = الوصفة الأساسية فقط
    QuantityToProduce   DECIMAL(18,3)   NOT NULL,
    RawMaterialsWarehouseId INT         NOT NULL FOREIGN KEY REFERENCES Warehouses(Id),  -- WarehouseType = RawMaterial
    OutputBatchId       INT             NULL FOREIGN KEY REFERENCES ItemBatches(Id),     -- دفعة الإنتاج الناتجة
    Status              NVARCHAR(20)    NOT NULL DEFAULT N'Draft'
                            CHECK (Status IN (N'Draft', N'InProgress', N'Completed', N'Cancelled')),
    CreatedByUserId     INT             NOT NULL FOREIGN KEY REFERENCES Users(Id)
);
GO

-- تفصيل الاستهلاك الفعلي لكل مادة أولية (BOM + الوصفة المخصصة مدموجَين)
CREATE TABLE ProductionOrderConsumptions (
    Id                      INT IDENTITY(1,1) PRIMARY KEY,
    ProductionOrderId       INT             NOT NULL FOREIGN KEY REFERENCES ProductionOrders(Id),
    RawMaterialItemId       INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),
    QuantityRequired        DECIMAL(18,4)   NOT NULL,
    QuantityConsumed        DECIMAL(18,4)   NOT NULL DEFAULT 0
);
GO

-- ============ تعريف اختبارات الجودة ============
CREATE TABLE QualityTests (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    TestName            NVARCHAR(150)   NOT NULL,
    ApplicableItemId    INT             NULL FOREIGN KEY REFERENCES Items(Id),  -- NULL = ينطبق على كل المنتجات
    StandardMin         DECIMAL(18,4)   NULL,
    StandardMax         DECIMAL(18,4)   NULL,
    StandardText        NVARCHAR(100)   NULL       -- لاختبارات نعم/لا أو وصفية
);
GO

-- ============ فحص دفعة إنتاج (رأس + نتائج) ============
CREATE TABLE QCBatchResults (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    ProductionOrderId   INT             NOT NULL FOREIGN KEY REFERENCES ProductionOrders(Id),
    BatchId             INT             NOT NULL FOREIGN KEY REFERENCES ItemBatches(Id),
    TestedByUserId      INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    TestDate            DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    OverallResult        NVARCHAR(20)   NOT NULL CHECK (OverallResult IN (N'Passed', N'Rejected'))
    -- قاعدة افتراضية مطبّقة في التطبيق: فشل اختبار واحد فقط يكفي لرفض الدفعة كاملة
);
GO

CREATE TABLE QCTestResultLines (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    QCBatchResultId     INT             NOT NULL FOREIGN KEY REFERENCES QCBatchResults(Id),
    QualityTestId       INT             NOT NULL FOREIGN KEY REFERENCES QualityTests(Id),
    MeasuredValue       NVARCHAR(50)    NOT NULL,
    Result              NVARCHAR(10)    NOT NULL CHECK (Result IN (N'Pass', N'Fail'))
);
GO

-- ============ أمر التعبيئة (يحوّل الناتج السائب إلى وحدات بيع بعد اعتماد المختبر) ============
CREATE TABLE PackingOrders (
    Id                              INT IDENTITY(1,1) PRIMARY KEY,
    ProductionOrderId               INT             NOT NULL FOREIGN KEY REFERENCES ProductionOrders(Id),
    PackagingLevelId                INT             NOT NULL FOREIGN KEY REFERENCES ItemPackagingLevels(Id),
    UnitsPackaged                   DECIMAL(18,3)   NOT NULL,
    ResultingFinishedGoodsWarehouseId INT           NOT NULL FOREIGN KEY REFERENCES Warehouses(Id), -- WarehouseType = FinishedGoods
    PackingDate                     DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    CreatedByUserId                 INT             NOT NULL FOREIGN KEY REFERENCES Users(Id)
);
GO

-- ============ إكمال الربط المؤجل: ItemBatches.ProductionOrderId ============
-- تُرك بلا قيد FK في ملف 03 لأن ProductionOrders لم يكن موجودًا بعد؛ يُستكمل هنا الآن.
ALTER TABLE ItemBatches
    ADD CONSTRAINT FK_ItemBatches_ProductionOrders
    FOREIGN KEY (ProductionOrderId) REFERENCES ProductionOrders(Id);
GO
