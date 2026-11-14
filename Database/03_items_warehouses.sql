/* ============================================================
   الأصناف، التعبيئة متعددة المستويات، المخازن، المواقع،
   التشغيلات (Batch)، وسجل حركة المخزون (Ledger)
   ============================================================ */

-- ============ الأصناف ============
CREATE TABLE Items (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    ItemCode        NVARCHAR(30)    NOT NULL UNIQUE,
    ItemName        NVARCHAR(200)   NOT NULL,
    BaseUnitName    NVARCHAR(30)    NOT NULL DEFAULT N'قطعة',
    SourcingMethod  NVARCHAR(20)    NOT NULL DEFAULT N'Manufactured'
                        CHECK (SourcingMethod IN (N'Manufactured', N'Purchased', N'Both')),
    SalePrice       DECIMAL(18,2)   NOT NULL DEFAULT 0,      -- سعر البيع العادي بالقطعة
    IsActive        BIT             NOT NULL DEFAULT 1
);
GO

-- ============ هيكلية التعبيئة (قطعة ← شرنك ← كارتون ...) ============
CREATE TABLE ItemPackagingLevels (
    Id                      INT IDENTITY(1,1) PRIMARY KEY,
    ItemId                  INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),
    LevelName               NVARCHAR(50)    NOT NULL,        -- قطعة / شرنك / كارتون / بالة
    ParentLevelId           INT             NULL FOREIGN KEY REFERENCES ItemPackagingLevels(Id),
    ContainsQuantity        DECIMAL(18,3)   NOT NULL DEFAULT 1,   -- كم من المستوى الأصغر يحويه هذا المستوى
    EquivalentBaseUnits     DECIMAL(18,3)   NOT NULL,         -- = بالقطعة (محسوبة ومخزّنة لتسريع الاستعلام)
    IsSellableUnit          BIT             NOT NULL DEFAULT 1
);
GO

-- ============ المخازن ============
CREATE TABLE Warehouses (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    BranchId        INT             NOT NULL FOREIGN KEY REFERENCES Branches(Id),
    Name            NVARCHAR(150)   NOT NULL,
    WarehouseType   NVARCHAR(30)    NOT NULL
                        CHECK (WarehouseType IN (
                            N'Main', N'Sub', N'Returns', N'Damaged', N'UnderInspection',
                            N'Transit', N'RepVan', N'RawMaterial', N'FinishedGoods')),
    IsSellableStock BIT             NOT NULL DEFAULT 1,       -- محسوبة حسب النوع وقت الإدخال
    OwnerEmployeeId INT             NULL FOREIGN KEY REFERENCES Employees(Id),  -- للكاش فان: صاحب المخزن المتنقل
    IsActive        BIT             NOT NULL DEFAULT 1
);
GO

-- ============ هيكلية المواقع الداخلية (منطقة ← رف ← موقع دقيق) ============
CREATE TABLE WarehouseLocations (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    WarehouseId     INT             NOT NULL FOREIGN KEY REFERENCES Warehouses(Id),
    ParentLocationId INT            NULL FOREIGN KEY REFERENCES WarehouseLocations(Id),
    LocationName    NVARCHAR(100)   NOT NULL,
    LevelType       NVARCHAR(20)    NOT NULL CHECK (LevelType IN (N'Zone', N'Shelf', N'Bin'))
);
GO

-- ============ التشغيلات (Batch/Serial + تاريخ الصلاحية) ============
CREATE TABLE ItemBatches (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    ItemId              INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),
    BatchNumber         NVARCHAR(50)    NOT NULL,
    ManufactureDate     DATE            NULL,
    ExpiryDate          DATE            NULL,
    ProductionOrderId   INT             NULL,   -- يُربط لاحقًا بجدول ProductionOrders (ملف 08)
    CONSTRAINT UQ_ItemBatch UNIQUE (ItemId, BatchNumber)
);
GO

/* ============================================================
   سجل حركة المخزون (Ledger) — مصدر الحقيقة الوحيد للأرصدة
   الرصيد الحالي لأي صنف/مخزن/تشغيلة = SUM(QuantityBaseUnits)
   من هذا الجدول (موجب = وارد، سالب = صادر)
   ============================================================ */
CREATE TABLE StockTransactions (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    ItemId              INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),
    WarehouseId         INT             NOT NULL FOREIGN KEY REFERENCES Warehouses(Id),
    LocationId          INT             NULL FOREIGN KEY REFERENCES WarehouseLocations(Id),
    BatchId             INT             NULL FOREIGN KEY REFERENCES ItemBatches(Id),
    QuantityBaseUnits   DECIMAL(18,3)   NOT NULL,             -- بالقطعة دائمًا (موجب/سالب)
    TransactionType     NVARCHAR(30)    NOT NULL
                            CHECK (TransactionType IN (
                                N'Receipt', N'SalesIssue', N'ProductionConsume', N'ProductionOutput',
                                N'Packing', N'Transfer', N'Damaged', N'FreeIssue', N'ReturnToWarehouse',
                                N'RepLoad', N'RepSale', N'RepFreeSale', N'RepDamaged', N'RepReturn',
                                N'SyncConflictAdjustment')),
    DamageReason        NVARCHAR(20)    NULL CHECK (DamageReason IN (N'Transit', N'Warehouse', N'Production')),
    FreeIssueRecipient  NVARCHAR(200)   NULL,                 -- الجهة المستفيدة (مبيعات/منتجات مجانية)
    ReferenceTable      NVARCHAR(60)    NULL,                 -- اسم المستند المصدر
    ReferenceId         INT             NULL,
    TransactionDate     DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    CreatedByUserId     INT             NOT NULL FOREIGN KEY REFERENCES Users(Id)
);
GO

CREATE INDEX IX_StockTransactions_ItemWarehouseBatch
    ON StockTransactions (ItemId, WarehouseId, BatchId);
GO

-- ============ تعارضات المزامنة (رصيد سالب ناتج عن عمل مندوب أوفلاين) ============
CREATE TABLE SyncConflicts (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    StockTransactionId  INT             NOT NULL FOREIGN KEY REFERENCES StockTransactions(Id),
    EmployeeId          INT             NOT NULL FOREIGN KEY REFERENCES Employees(Id),  -- المندوب
    ItemId              INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),
    BatchId             INT             NULL FOREIGN KEY REFERENCES ItemBatches(Id),
    RequestedQuantity   DECIMAL(18,3)   NOT NULL,
    ResultingBalance    DECIMAL(18,3)   NOT NULL,             -- سالب وقت اكتشاف التعارض
    Status              NVARCHAR(20)    NOT NULL DEFAULT N'Pending'
                            CHECK (Status IN (N'Pending', N'Resolved')),
    ResolutionNotes     NVARCHAR(300)   NULL,
    ResolvedByUserId    INT             NULL FOREIGN KEY REFERENCES Users(Id),
    CreatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
