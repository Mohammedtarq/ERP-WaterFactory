/* ============================================================
   الموردون — أمر الشراء (شروط الدفع + الدفعة المقدمة) والاستلام
   ============================================================ */

CREATE TABLE Suppliers (
    Id                      INT IDENTITY(1,1) PRIMARY KEY,
    Name                    NVARCHAR(200)   NOT NULL,
    Phone                   NVARCHAR(30)    NULL,
    Address                 NVARCHAR(300)   NULL,
    DefaultPaymentTerms     NVARCHAR(20)    NULL
                                CHECK (DefaultPaymentTerms IN (N'Cash', N'Credit', N'AdvancePlusCredit')),
    IsActive                BIT             NOT NULL DEFAULT 1
);
GO

-- ============ أمر الشراء ============
CREATE TABLE PurchaseOrders (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    PONumber            NVARCHAR(30)    NOT NULL UNIQUE,
    SupplierId          INT             NOT NULL FOREIGN KEY REFERENCES Suppliers(Id),
    WarehouseId         INT             NOT NULL FOREIGN KEY REFERENCES Warehouses(Id),  -- المخزن المستهدف
    OrderDate           DATE            NOT NULL,
    ExpectedDeliveryDate DATE           NULL,
    Status              NVARCHAR(20)    NOT NULL DEFAULT N'Draft'
                            CHECK (Status IN (N'Draft', N'Sent', N'PartiallyReceived', N'Completed', N'Cancelled')),
    PaymentTerms         NVARCHAR(20)   NOT NULL DEFAULT N'Credit'
                            CHECK (PaymentTerms IN (N'Cash', N'Credit', N'AdvancePlusCredit')),
    AdvanceAmount        DECIMAL(18,2)  NOT NULL DEFAULT 0,
    AdvanceVoucherId     INT            NULL FOREIGN KEY REFERENCES Vouchers(Id),
    CreatedByUserId      INT            NOT NULL FOREIGN KEY REFERENCES Users(Id)
);
GO

CREATE TABLE PurchaseOrderLines (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    PurchaseOrderId     INT             NOT NULL FOREIGN KEY REFERENCES PurchaseOrders(Id),
    ItemId              INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),
    QuantityOrdered     DECIMAL(18,3)   NOT NULL,
    ExpectedUnitCost    DECIMAL(18,2)   NOT NULL,
    QuantityReceived    DECIMAL(18,3)   NOT NULL DEFAULT 0     -- يتراكم مع كل استلام جزئي
);
GO

-- ============ استلام البضاعة ============
CREATE TABLE GoodsReceipts (
    Id                      INT IDENTITY(1,1) PRIMARY KEY,
    ReceiptNumber           NVARCHAR(30)    NOT NULL UNIQUE,
    PurchaseOrderId         INT             NULL FOREIGN KEY REFERENCES PurchaseOrders(Id),
    SupplierId              INT             NOT NULL FOREIGN KEY REFERENCES Suppliers(Id),
    WarehouseId             INT             NOT NULL FOREIGN KEY REFERENCES Warehouses(Id),
    ReceiptDate             DATE            NOT NULL,
    SupplierInvoiceNumber   NVARCHAR(50)    NULL,
    JournalEntryId          INT             NULL FOREIGN KEY REFERENCES JournalEntries(Id),
    Status                  NVARCHAR(20)    NOT NULL DEFAULT N'Draft' CHECK (Status IN (N'Draft', N'Posted')),
    CreatedByUserId         INT             NOT NULL FOREIGN KEY REFERENCES Users(Id)
);
GO

CREATE TABLE GoodsReceiptLines (
    Id                      INT IDENTITY(1,1) PRIMARY KEY,
    GoodsReceiptId          INT             NOT NULL FOREIGN KEY REFERENCES GoodsReceipts(Id),
    ItemId                  INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),
    PurchaseOrderLineId     INT             NULL FOREIGN KEY REFERENCES PurchaseOrderLines(Id),
    QuantityReceived        DECIMAL(18,3)   NOT NULL,
    UnitCost                DECIMAL(18,2)   NOT NULL,
    BatchId                 INT             NOT NULL FOREIGN KEY REFERENCES ItemBatches(Id)
);
GO
