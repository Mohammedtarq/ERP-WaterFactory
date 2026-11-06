/* ============================================================
   المندوبين — محفظة المندوب (عهدة نقدية)، المناطق، الزبائن المخصصون
   ملاحظة: الكاش فان نفسه هو سجل في جدول Warehouses (WarehouseType = RepVan)
   وحركاته مسجّلة في StockTransactions (ملف 03) — لا حاجة لجداول مخزون منفصلة هنا.
   ============================================================ */

-- ============ محفظة المندوب (سجل حركة نقدية = عهدة) ============
CREATE TABLE RepWalletTransactions (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    EmployeeId      INT             NOT NULL FOREIGN KEY REFERENCES Employees(Id),  -- IsSalesRep = 1
    TransactionDate DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    Description     NVARCHAR(300)   NOT NULL,
    AmountIn        DECIMAL(18,2)   NOT NULL DEFAULT 0,   -- مبيعات نقدية + تحصيل ديون
    AmountOut       DECIMAL(18,2)   NOT NULL DEFAULT 0,   -- مصروفات ميدانية + تسليم للخزينة
    ReferenceTable  NVARCHAR(60)    NULL,                 -- SalesInvoices / Vouchers / ...
    ReferenceId     INT             NULL,
    JournalEntryId  INT             NULL FOREIGN KEY REFERENCES JournalEntries(Id)
    -- رصيد المحفظة الحالي = SUM(AmountIn) - SUM(AmountOut) لكل موظف
);
GO

-- ============ المناطق المخصصة للمندوب (مرنة، قابلة للتعديل) ============
CREATE TABLE RepTerritories (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    EmployeeId      INT             NOT NULL FOREIGN KEY REFERENCES Employees(Id),
    TerritoryName   NVARCHAR(150)   NOT NULL,
    CONSTRAINT UQ_RepTerritory UNIQUE (EmployeeId, TerritoryName)
);
GO

-- ============ الزبائن المخصصون للمندوب (متعدد لمتعدد، مرن) ============
CREATE TABLE RepCustomerAssignments (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    EmployeeId      INT             NOT NULL FOREIGN KEY REFERENCES Employees(Id),
    CustomerId      INT             NOT NULL FOREIGN KEY REFERENCES Customers(Id),
    AssignedAt      DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_RepCustomer UNIQUE (EmployeeId, CustomerId)
    -- الوكلاء بلا مندوب حاليًا: ببساطة لا سطر لهم هنا؛ يمكن إضافته لاحقًا دون أي تعديل بنيوي
);
GO
