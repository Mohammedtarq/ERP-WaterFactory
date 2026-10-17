/* ============================================================
   المرحلة م3: المندوبون
   - الحمولة الافتراضية لكل مندوب (تُقترح كل صباح)
   - طلب التحميل: يطلبه مدير المبيعات ويجهّزه أمين المخزن (عند التجهيز فقط يتحرك المخزون بمستند إسناد مرقّم)
   - التسوية اليومية مع أمين الصندوق: المرتجع، المجاني الذي أعطاه المندوب، المصاريف الميدانية،
     النقد المتوقع (رصيد المحفظة) والمستلم فعلًا والفرق (يبقى في ذمة المندوب)
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */

IF OBJECT_ID('RepDefaultLoads', 'U') IS NULL
CREATE TABLE RepDefaultLoads (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    RepEmployeeId       INT             NOT NULL FOREIGN KEY REFERENCES Employees(Id),
    ItemId              INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),
    PackagingLevelId    INT             NOT NULL FOREIGN KEY REFERENCES ItemPackagingLevels(Id),
    QuantityInLevel     DECIMAL(18,3)   NOT NULL CHECK (QuantityInLevel > 0),
    CONSTRAINT UQ_RepDefaultLoads UNIQUE (RepEmployeeId, ItemId, PackagingLevelId)
);
GO

IF OBJECT_ID('RepLoadOrders', 'U') IS NULL
CREATE TABLE RepLoadOrders (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    OrderNumber         NVARCHAR(30)    NOT NULL UNIQUE,
    RepEmployeeId       INT             NOT NULL FOREIGN KEY REFERENCES Employees(Id),
    VanWarehouseId      INT             NOT NULL FOREIGN KEY REFERENCES Warehouses(Id),
    FromWarehouseId     INT             NOT NULL FOREIGN KEY REFERENCES Warehouses(Id),
    LoadDate            DATE            NOT NULL,
    Status              NVARCHAR(20)    NOT NULL DEFAULT N'Pending' CHECK (Status IN (N'Pending', N'Prepared', N'Cancelled')),
    Notes               NVARCHAR(400)   NULL,
    RequestedByUserId   INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    RequestedAt         DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    PreparedByUserId    INT             NULL FOREIGN KEY REFERENCES Users(Id),
    PreparedAt          DATETIME2       NULL,
    StockDocumentId     INT             NULL FOREIGN KEY REFERENCES StockDocuments(Id),
    CancelReason        NVARCHAR(300)   NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RepLoadOrders_Status')
    CREATE INDEX IX_RepLoadOrders_Status ON RepLoadOrders (Status, LoadDate);
GO

IF OBJECT_ID('RepLoadOrderLines', 'U') IS NULL
CREATE TABLE RepLoadOrderLines (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    RepLoadOrderId      INT             NOT NULL FOREIGN KEY REFERENCES RepLoadOrders(Id),
    ItemId              INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),
    PackagingLevelId    INT             NOT NULL FOREIGN KEY REFERENCES ItemPackagingLevels(Id),
    QuantityInLevel     DECIMAL(18,3)   NOT NULL CHECK (QuantityInLevel > 0),     -- المطلوب
    PreparedQuantity    DECIMAL(18,3)   NULL                                       -- المجهَّز فعلًا (قد يقل عن المطلوب)
);
GO

IF OBJECT_ID('RepSettlements', 'U') IS NULL
CREATE TABLE RepSettlements (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    SettlementNumber    NVARCHAR(30)    NOT NULL UNIQUE,
    RepEmployeeId       INT             NOT NULL FOREIGN KEY REFERENCES Employees(Id),
    VanWarehouseId      INT             NOT NULL FOREIGN KEY REFERENCES Warehouses(Id),
    SettlementDate      DATE            NOT NULL,
    ReturnDocumentId    INT             NULL FOREIGN KEY REFERENCES StockDocuments(Id),
    AutoInvoiceId       INT             NULL FOREIGN KEY REFERENCES SalesInvoices(Id),
    ReturnedPieces      DECIMAL(18,3)   NOT NULL DEFAULT 0,
    FreePieces          DECIMAL(18,3)   NOT NULL DEFAULT 0,
    FreeCost            DECIMAL(18,2)   NOT NULL DEFAULT 0,       -- مجاني المندوب بالكلفة
    FieldExpenses       DECIMAL(18,2)   NOT NULL DEFAULT 0,
    ExpectedCash        DECIMAL(18,2)   NOT NULL DEFAULT 0,       -- رصيد المحفظة بعد المصاريف
    ReceivedCash        DECIMAL(18,2)   NOT NULL DEFAULT 0,
    Difference          DECIMAL(18,2)   NOT NULL DEFAULT 0,       -- موجب = عجز يبقى في ذمة المندوب
    Notes               NVARCHAR(400)   NULL,
    CreatedByUserId     INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    CreatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RepSettlements_Rep')
    CREATE INDEX IX_RepSettlements_Rep ON RepSettlements (RepEmployeeId, SettlementDate);
GO

-- مجاني المندوب: لمن أعطاه ولماذا، بالكلفة (يظهر في الحسابات الختامية سطرًا مستقلًا)
IF OBJECT_ID('RepFreeGoods', 'U') IS NULL
CREATE TABLE RepFreeGoods (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    RepSettlementId     INT             NOT NULL FOREIGN KEY REFERENCES RepSettlements(Id),
    ItemId              INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),
    QuantityBaseUnits   DECIMAL(18,3)   NOT NULL CHECK (QuantityBaseUnits > 0),
    UnitCost            DECIMAL(18,4)   NULL,
    CustomerId          INT             NULL FOREIGN KEY REFERENCES Customers(Id),
    Reason              NVARCHAR(200)   NOT NULL
);
GO

-- المصروف الميداني يُربط بالسيارة (وقود، تصليح...) لتقرير كلفة كل سيارة
IF COL_LENGTH('RepWalletTransactions', 'VehicleId') IS NULL
    ALTER TABLE RepWalletTransactions ADD VehicleId INT NULL FOREIGN KEY REFERENCES Vehicles(Id);
GO

CREATE OR ALTER TRIGGER trg_RepSettlements_PeriodLock ON RepSettlements AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @lock DATE = dbo.fn_PeriodLockedThrough();
    IF @lock IS NULL RETURN;
    IF EXISTS (SELECT 1 FROM inserted WHERE SettlementDate <= @lock) OR EXISTS (SELECT 1 FROM deleted WHERE SettlementDate <= @lock)
        EXEC sp_Period_ThrowLocked @lock;
END;
GO
