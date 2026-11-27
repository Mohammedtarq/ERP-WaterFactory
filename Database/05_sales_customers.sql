/* ============================================================
   المبيعات والعملاء — هيكلية الوكلاء، تسعير الوكلاء، الفاتورة
   ============================================================ */

-- ============ العملاء (وكيل / عميل فرعي / عميل مباشر) ============
CREATE TABLE Customers (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    Name            NVARCHAR(200)   NOT NULL,
    CustomerType    NVARCHAR(20)    NOT NULL DEFAULT N'Direct'
                        CHECK (CustomerType IN (N'Agent', N'SubCustomer', N'Direct')),
    ParentAgentId   INT             NULL FOREIGN KEY REFERENCES Customers(Id),  -- فقط عند SubCustomer
    Province        NVARCHAR(100)   NULL,
    Phone           NVARCHAR(30)    NULL,
    Address         NVARCHAR(300)   NULL,
    IsActive        BIT             NOT NULL DEFAULT 1
    -- ملاحظة: مديونية كل عميل مستقلة تمامًا، تُحسب من Vouchers + SalesInvoices الخاصة به فقط
);
GO

-- ============ تسعير الوكلاء (سعر يدوي منفصل لكل صنف) ============
CREATE TABLE AgentItemPrices (
    Id          INT IDENTITY(1,1) PRIMARY KEY,
    CustomerId  INT             NOT NULL FOREIGN KEY REFERENCES Customers(Id),  -- يجب أن يكون CustomerType = Agent
    ItemId      INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),
    AgentPrice  DECIMAL(18,2)   NOT NULL,
    CONSTRAINT UQ_AgentItem UNIQUE (CustomerId, ItemId)
);
GO

-- إعداد عام لسعر مستلزمات التحميل (قابل للتعديل من الإعدادات)
CREATE TABLE LoadingSuppliesSettings (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    RatePerPiece        DECIMAL(18,2)   NOT NULL,
    EffectiveDate       DATE            NOT NULL
);
GO

-- ============ فاتورة المبيعات (رأس المستند) ============
CREATE TABLE SalesInvoices (
    Id                          INT IDENTITY(1,1) PRIMARY KEY,
    InvoiceNumber                NVARCHAR(30)    NOT NULL UNIQUE,
    CustomerId                   INT             NOT NULL FOREIGN KEY REFERENCES Customers(Id),
    WarehouseId                  INT             NOT NULL FOREIGN KEY REFERENCES Warehouses(Id),
    InvoiceDate                  DATE            NOT NULL,
    PaymentMethod                NVARCHAR(20)    NOT NULL
                                    CHECK (PaymentMethod IN (N'Cash', N'Credit', N'Partial', N'Electronic')),
    AmountPaidNow                 DECIMAL(18,2)  NOT NULL DEFAULT 0,
    TaxEnabled                    BIT            NOT NULL DEFAULT 0,
    TaxRate                       DECIMAL(5,2)   NOT NULL DEFAULT 14,
    LoadingSuppliesEnabled        BIT            NOT NULL DEFAULT 0,
    LoadingSuppliesAmount         DECIMAL(18,2)  NOT NULL DEFAULT 0,
    IsAgentPricing                BIT            NOT NULL DEFAULT 0,
    IsFreeSale                    BIT            NOT NULL DEFAULT 0,
    FreeSaleRecipient             NVARCHAR(200)  NULL,           -- إلزامي عند IsFreeSale = 1
    SalesRepEmployeeId            INT            NULL FOREIGN KEY REFERENCES Employees(Id),  -- عند الصدور من كاش فان
    Status                        NVARCHAR(20)   NOT NULL DEFAULT N'Draft' CHECK (Status IN (N'Draft', N'Posted')),
    JournalEntryId                INT            NULL FOREIGN KEY REFERENCES JournalEntries(Id),
    CreatedByUserId               INT            NOT NULL FOREIGN KEY REFERENCES Users(Id)
);
GO

-- ============ سطور الفاتورة ============
CREATE TABLE SalesInvoiceLines (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    SalesInvoiceId      INT             NOT NULL FOREIGN KEY REFERENCES SalesInvoices(Id),
    ItemId              INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),
    BatchId             INT             NULL FOREIGN KEY REFERENCES ItemBatches(Id),  -- اختيار حر، توصية FIFO في التطبيق
    PackagingLevelId    INT             NOT NULL FOREIGN KEY REFERENCES ItemPackagingLevels(Id),
    QuantityInLevel     DECIMAL(18,3)   NOT NULL,               -- الكمية بوحدة البيع المختارة (كارتون/شرنك/قطعة)
    QuantityBaseUnits   DECIMAL(18,3)   NOT NULL,               -- محسوبة تلقائيًا بالقطعة لخصم المخزون
    UnitPrice           DECIMAL(18,2)   NOT NULL,
    LineTotal           DECIMAL(18,2)   NOT NULL
);
GO
