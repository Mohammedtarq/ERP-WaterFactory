/* ============================================================
   بيع المواد التالفة (قابل لإعادة التنفيذ)
   - فاتورة نقدية لجهة (مثل بائع خردة) من مخزن التالف بسعر للوحدة
   - الكميات تخرج بمستند إخراج مخزني من مخزن التالف، والنقد يدخل الصندوق، والقيد على "إيراد بيع مواد تالفة"
   ============================================================ */
SET QUOTED_IDENTIFIER ON;
GO

IF NOT EXISTS (SELECT 1 FROM sys.sequences WHERE name = 'seq_DamagedSale')
    CREATE SEQUENCE seq_DamagedSale AS INT START WITH 1 INCREMENT BY 1;
GO

IF OBJECT_ID('DamagedSales', 'U') IS NULL
CREATE TABLE DamagedSales (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    SaleNumber          NVARCHAR(30)    NOT NULL UNIQUE,
    SaleDate            DATE            NOT NULL,
    BuyerName           NVARCHAR(200)   NOT NULL,
    StockDocumentId     INT             NOT NULL FOREIGN KEY REFERENCES StockDocuments(Id),
    TotalAmount         DECIMAL(18,2)   NOT NULL CHECK (TotalAmount > 0),
    Notes               NVARCHAR(400)   NULL,
    JournalEntryId      INT             NULL FOREIGN KEY REFERENCES JournalEntries(Id),
    CreatedByUserId     INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    CreatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

IF OBJECT_ID('DamagedSaleLines', 'U') IS NULL
CREATE TABLE DamagedSaleLines (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    DamagedSaleId       INT             NOT NULL FOREIGN KEY REFERENCES DamagedSales(Id),
    ItemId              INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),
    Quantity            DECIMAL(18,3)   NOT NULL CHECK (Quantity > 0),       -- بالوحدة الأساسية
    UnitPrice           DECIMAL(18,4)   NOT NULL CHECK (UnitPrice >= 0),
    Amount              DECIMAL(18,2)   NOT NULL
);
GO
