/* ============================================================
   مستندات المخازن + الصناديق المالية (قابل لإعادة التنفيذ بأمان)
   ============================================================ */
SET QUOTED_IDENTIFIER ON;
GO

-- ============ نوع حركة جديد: إخراج مخزني (صرف لجهة/غرض) ============
-- قيد CHECK الأصلي بلا اسم؛ يُستبدل بقيد مسمّى يضم Issue
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_StockTransactions_Type')
BEGIN
    DECLARE @old SYSNAME = (SELECT TOP 1 cc.name FROM sys.check_constraints cc
                            WHERE cc.parent_object_id = OBJECT_ID('StockTransactions') AND cc.definition LIKE '%SalesIssue%');
    IF @old IS NOT NULL EXEC (N'ALTER TABLE StockTransactions DROP CONSTRAINT [' + @old + N']');
    ALTER TABLE StockTransactions ADD CONSTRAINT CK_StockTransactions_Type CHECK (TransactionType IN (
        N'Receipt', N'SalesIssue', N'ProductionConsume', N'ProductionOutput',
        N'Packing', N'Transfer', N'Damaged', N'FreeIssue', N'ReturnToWarehouse',
        N'RepLoad', N'RepSale', N'RepFreeSale', N'RepDamaged', N'RepReturn',
        N'SyncConflictAdjustment', N'Issue'));
END;
GO

-- ============ مستندات المخزن (إدخال / إخراج / مناقلة / تالف / مسحوب مجاني) ============
-- كل عملية من واجهة المخزن مستند مرقّم قابل للطباعة؛ حركاته في StockTransactions
-- (ReferenceTable = 'StockDocuments')، وسطوره بوحدة التعبئة كما أُدخلت في StockDocumentLines.
IF OBJECT_ID('StockDocuments', 'U') IS NULL
CREATE TABLE StockDocuments (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    DocumentNumber      NVARCHAR(30)    NOT NULL UNIQUE,
    DocumentType        NVARCHAR(20)    NOT NULL
                            CHECK (DocumentType IN (N'Receipt', N'Issue', N'Transfer', N'Damaged', N'FreeIssue')),
    WarehouseId         INT             NOT NULL FOREIGN KEY REFERENCES Warehouses(Id),
    CounterWarehouseId  INT             NULL FOREIGN KEY REFERENCES Warehouses(Id),   -- المخزن المستلم في المناقلة
    DocumentDate        DATE            NOT NULL,
    PartyName           NVARCHAR(200)   NULL,       -- المورد / الجهة المستلمة / المستفيد
    DamageReason        NVARCHAR(20)    NULL CHECK (DamageReason IN (N'Transit', N'Warehouse', N'Production')),
    Notes               NVARCHAR(400)   NULL,
    CreatedByUserId     INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    CreatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

IF OBJECT_ID('StockDocumentLines', 'U') IS NULL
CREATE TABLE StockDocumentLines (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    StockDocumentId     INT             NOT NULL FOREIGN KEY REFERENCES StockDocuments(Id),
    ItemId              INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),
    PackagingLevelId    INT             NOT NULL FOREIGN KEY REFERENCES ItemPackagingLevels(Id),
    QuantityInLevel     DECIMAL(18,3)   NOT NULL,
    QuantityBaseUnits   DECIMAL(18,3)   NOT NULL,
    BatchId             INT             NULL FOREIGN KEY REFERENCES ItemBatches(Id),  -- NULL في الصرف = تلقائي (الأقرب انتهاءً)
    Notes               NVARCHAR(200)   NULL
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_StockDocuments_Warehouse')
    CREATE INDEX IX_StockDocuments_Warehouse ON StockDocuments (WarehouseId, DocumentDate);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_StockTransactions_WarehouseDate')
    CREATE INDEX IX_StockTransactions_WarehouseDate ON StockTransactions (WarehouseId, TransactionDate) INCLUDE (ItemId, QuantityBaseUnits, TransactionType);
GO

IF OBJECT_ID('seq_StockDocuments', 'SO') IS NULL
    CREATE SEQUENCE seq_StockDocuments AS INT START WITH 1 INCREMENT BY 1;
GO

/* ============================================================
   الصناديق المالية
   - صندوق رئيسي (أو أكثر) + صناديق للمستخدمين (لكل مستخدم صندوقه).
   - الرصيد = مجموع حركات الصندوق غير الملغاة (سجل حركة، لا رقم مخزَّن).
   - المبيعات النقدية، سندات القبض/الصرف النقدية، وتسليم نقد المندوب تدخل الصندوق تلقائيًا.
   ============================================================ */
IF OBJECT_ID('CashBoxes', 'U') IS NULL
CREATE TABLE CashBoxes (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    Name            NVARCHAR(100)   NOT NULL UNIQUE,
    BoxType         NVARCHAR(10)    NOT NULL CHECK (BoxType IN (N'Main', N'User')),
    OwnerUserId     INT             NULL FOREIGN KEY REFERENCES Users(Id),
    IsDefault       BIT             NOT NULL DEFAULT 0,   -- يستقبل المبالغ التلقائية إن لم يكن للمستخدم صندوق
    IsActive        BIT             NOT NULL DEFAULT 1,
    Notes           NVARCHAR(300)   NULL,
    CreatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

IF OBJECT_ID('CashBoxTransactions', 'U') IS NULL
CREATE TABLE CashBoxTransactions (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    TxNumber            NVARCHAR(30)    NOT NULL UNIQUE,
    CashBoxId           INT             NOT NULL FOREIGN KEY REFERENCES CashBoxes(Id),
    TxDate              DATE            NOT NULL,
    TxType              NVARCHAR(20)    NOT NULL
                            CHECK (TxType IN (N'Opening', N'Deposit', N'Withdrawal', N'TransferIn', N'TransferOut',
                                              N'SalesReceipt', N'VoucherReceipt', N'VoucherPayment', N'RepHandover')),
    Amount              DECIMAL(18,2)   NOT NULL,           -- موجب = داخل، سالب = خارج
    CounterCashBoxId    INT             NULL FOREIGN KEY REFERENCES CashBoxes(Id),
    TransferGroup       UNIQUEIDENTIFIER NULL,              -- يربط طرفي المناقلة
    PartyName           NVARCHAR(200)   NULL,
    Description         NVARCHAR(400)   NULL,
    ReferenceTable      NVARCHAR(60)    NULL,               -- SalesInvoices / Vouchers / RepWalletTransactions
    ReferenceId         INT             NULL,
    JournalEntryId      INT             NULL FOREIGN KEY REFERENCES JournalEntries(Id),
    IsVoided            BIT             NOT NULL DEFAULT 0,
    VoidReason          NVARCHAR(300)   NULL,
    OriginalAmount      DECIMAL(18,2)   NULL,               -- المبلغ قبل أول تعديل (للتدقيق)
    CreatedByUserId     INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    CreatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    ModifiedByUserId    INT             NULL FOREIGN KEY REFERENCES Users(Id),
    ModifiedAt          DATETIME2       NULL
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CashBoxTransactions_Box')
    CREATE INDEX IX_CashBoxTransactions_Box ON CashBoxTransactions (CashBoxId, TxDate) INCLUDE (Amount, IsVoided);
GO

IF OBJECT_ID('seq_CashBoxTx', 'SO') IS NULL
    CREATE SEQUENCE seq_CashBoxTx AS INT START WITH 1 INCREMENT BY 1;
GO

/* ------------------------------------------------------------
   تسجيل حركة تلقائية في الصندوق المناسب للمستخدم:
   صندوقه الخاص ← الصندوق الافتراضي ← أول صندوق رئيسي. بلا صندوق = لا شيء (لا يوقف العملية).
   يُستدعى من ترحيل المبيعات (09) ومن خدمات C# داخل نفس المعاملة.
   ------------------------------------------------------------ */
CREATE OR ALTER PROCEDURE sp_CashBox_RecordAuto
    @UserId         INT,
    @TxType         NVARCHAR(20),
    @Amount         DECIMAL(18,2),
    @TxDate         DATE,
    @ReferenceTable NVARCHAR(60)  = NULL,
    @ReferenceId    INT           = NULL,
    @PartyName      NVARCHAR(200) = NULL,
    @Description    NVARCHAR(400) = NULL,
    @JournalEntryId INT           = NULL,
    @CashBoxId      INT           = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @Amount = 0 RETURN;

    IF @CashBoxId IS NULL
        SELECT TOP 1 @CashBoxId = Id FROM CashBoxes
        WHERE IsActive = 1 AND (OwnerUserId = @UserId OR IsDefault = 1 OR BoxType = N'Main')
        ORDER BY CASE WHEN OwnerUserId = @UserId THEN 0 WHEN IsDefault = 1 THEN 1 ELSE 2 END, Id;
    IF @CashBoxId IS NULL RETURN;

    DECLARE @n INT = NEXT VALUE FOR seq_CashBoxTx;
    INSERT INTO CashBoxTransactions (TxNumber, CashBoxId, TxDate, TxType, Amount, PartyName, Description,
                                     ReferenceTable, ReferenceId, JournalEntryId, CreatedByUserId)
    VALUES (N'CB-' + CAST(YEAR(@TxDate) AS NVARCHAR(4)) + N'-' + RIGHT(N'000000' + CAST(@n AS NVARCHAR(10)), 6),
            @CashBoxId, @TxDate, @TxType, @Amount, @PartyName, @Description,
            @ReferenceTable, @ReferenceId, @JournalEntryId, @UserId);
END;
GO
