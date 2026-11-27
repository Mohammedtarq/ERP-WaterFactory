/* ============================================================
   المطابقة الدورية للموجودات وأرباح الشركاء (قابل لإعادة التنفيذ)
   - سعر الكلفة للصنف (يُحدَّث تلقائيًا من آخر استلام شراء، ويُعدَّل يدويًا)
   - الشركاء ونسبهم، وحركاتهم: حصة أرباح من مطابقة، سحب من الصندوق، رصيد افتتاحي
   - المطابقة: لقطة محفوظة لكل الموجودات والالتزامات، والفائض عن المطابقة السابقة يوزَّع على الشركاء
   ============================================================ */
SET QUOTED_IDENTIFIER ON;
GO

IF COL_LENGTH('Items', 'CostPrice') IS NULL
    ALTER TABLE Items ADD CostPrice DECIMAL(18,4) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.sequences WHERE name = 'seq_Reconciliation')
    CREATE SEQUENCE seq_Reconciliation AS INT START WITH 1 INCREMENT BY 1;
GO
IF NOT EXISTS (SELECT 1 FROM sys.sequences WHERE name = 'seq_PartnerTx')
    CREATE SEQUENCE seq_PartnerTx AS INT START WITH 1 INCREMENT BY 1;
GO

IF OBJECT_ID('Partners', 'U') IS NULL
CREATE TABLE Partners (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    Name            NVARCHAR(150)   NOT NULL UNIQUE,
    SharePercent    DECIMAL(7,4)    NOT NULL CHECK (SharePercent >= 0 AND SharePercent <= 100),
    IsManager       BIT             NOT NULL DEFAULT 0,
    IsActive        BIT             NOT NULL DEFAULT 1,
    Notes           NVARCHAR(300)   NULL
);
GO

IF OBJECT_ID('AssetReconciliations', 'U') IS NULL
CREATE TABLE AssetReconciliations (
    Id                      INT IDENTITY(1,1) PRIMARY KEY,
    ReconNumber             NVARCHAR(30)    NOT NULL UNIQUE,
    ReconDate               DATE            NOT NULL,
    FinishedGoodsValuation  NVARCHAR(20)    NOT NULL CHECK (FinishedGoodsValuation IN (N'Cost', N'SalePrice')),
    RawMaterialsValue       DECIMAL(18,2)   NOT NULL,
    WorkInProcessValue      DECIMAL(18,2)   NOT NULL,
    FinishedGoodsValue      DECIMAL(18,2)   NOT NULL,
    CustomerDebts           DECIMAL(18,2)   NOT NULL,
    CashInBoxes             DECIMAL(18,2)   NOT NULL,
    CashWithReps            DECIMAL(18,2)   NOT NULL,
    EmployeeAdvances        DECIMAL(18,2)   NOT NULL,
    SupplierAdvances        DECIMAL(18,2)   NOT NULL,
    SupplierDebts           DECIMAL(18,2)   NOT NULL,
    CustomerDeposits        DECIMAL(18,2)   NOT NULL,
    NetAssets               DECIMAL(18,2)   NOT NULL,
    PreviousReconciliationId INT            NULL FOREIGN KEY REFERENCES AssetReconciliations(Id),
    PreviousNetAssets       DECIMAL(18,2)   NULL,
    PartnerWithdrawalsSincePrevious DECIMAL(18,2) NOT NULL DEFAULT 0,
    OwnerDepositsSincePrevious DECIMAL(18,2) NOT NULL DEFAULT 0,          -- إيداعات رأس مال في الصناديق (ليست ربحًا)
    Surplus                 DECIMAL(18,2)   NOT NULL,                      -- صفر لمطابقة الأساس الأولى
    IsBaseline              BIT             NOT NULL DEFAULT 0,
    Notes                   NVARCHAR(400)   NULL,
    JournalEntryId          INT             NULL FOREIGN KEY REFERENCES JournalEntries(Id),
    CreatedByUserId         INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    CreatedAt               DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

IF OBJECT_ID('AssetReconciliationLines', 'U') IS NULL
CREATE TABLE AssetReconciliationLines (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    ReconciliationId    INT             NOT NULL FOREIGN KEY REFERENCES AssetReconciliations(Id),
    Section             NVARCHAR(30)    NOT NULL,
    Description         NVARCHAR(250)   NOT NULL,
    Quantity            DECIMAL(18,3)   NULL,
    UnitValue           DECIMAL(18,4)   NULL,
    Value               DECIMAL(18,2)   NOT NULL
);
GO

IF OBJECT_ID('PartnerTransactions', 'U') IS NULL
CREATE TABLE PartnerTransactions (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    TxNumber            NVARCHAR(30)    NOT NULL UNIQUE,
    PartnerId           INT             NOT NULL FOREIGN KEY REFERENCES Partners(Id),
    TxDate              DATE            NOT NULL,
    Kind                NVARCHAR(20)    NOT NULL CHECK (Kind IN (N'ProfitShare', N'Withdrawal', N'Opening')),
    Amount              DECIMAL(18,2)   NOT NULL,                          -- موجب = له، سالب = عليه
    SharePercent        DECIMAL(7,4)    NULL,
    ReconciliationId    INT             NULL FOREIGN KEY REFERENCES AssetReconciliations(Id),
    Notes               NVARCHAR(300)   NULL,
    JournalEntryId      INT             NULL FOREIGN KEY REFERENCES JournalEntries(Id),
    CreatedByUserId     INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    CreatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PartnerTransactions_Partner')
    CREATE INDEX IX_PartnerTransactions_Partner ON PartnerTransactions (PartnerId, TxDate);
GO

-- حركة صندوق جديدة: سحب أرباح شريك
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CashBoxTransactions_Type' AND definition LIKE '%PartnerWithdrawal%')
BEGIN
    DECLARE @old SYSNAME = (SELECT TOP 1 cc.name FROM sys.check_constraints cc
                            WHERE cc.parent_object_id = OBJECT_ID('CashBoxTransactions') AND cc.definition LIKE '%RepHandover%');
    IF @old IS NOT NULL EXEC (N'ALTER TABLE CashBoxTransactions DROP CONSTRAINT [' + @old + N']');
    ALTER TABLE CashBoxTransactions ADD CONSTRAINT CK_CashBoxTransactions_Type CHECK (TxType IN (
        N'Opening', N'Deposit', N'Withdrawal', N'TransferIn', N'TransferOut',
        N'SalesReceipt', N'VoucherReceipt', N'VoucherPayment', N'RepHandover',
        N'CustomerDepositIn', N'CustomerDepositOut', N'EmployeeAdvance', N'PartnerWithdrawal'));
END;
GO
