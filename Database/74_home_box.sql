/* ============================================================
   صندوق المنزل وتسوية الصناديق
   - HomeBoxEntries: صرف من صندوق المنزل خارج العمل (الخمس، مساعدات، شراء أصول، أخرى) — لا يدخل كلفة القنينة ولا صافي الربح
   - HomeLoans / HomeLoanRepayments: اقتراض صندوق عمل من صندوق المنزل وسداده
   - أنواع حركة صندوق: HomeSpend (صرف المنزل)، Adjustment (تسوية بالجرد الفعلي)
   - طلب صرف جديد: CashAdjustment (تسوية فوق السقف تنتظر موافقة)
   - المطابقة: HomeDistributionsSincePrevious (ما صُرف من المنزل منذ المطابقة السابقة يُعاد لحساب الفائض)
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */

IF OBJECT_ID('HomeBoxEntries', 'U') IS NULL
CREATE TABLE HomeBoxEntries (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    EntryNumber     NVARCHAR(30)   NOT NULL CONSTRAINT UQ_HomeBoxEntries_Number UNIQUE,
    EntryDate       DATE           NOT NULL,
    Kind            NVARCHAR(12)   NOT NULL CONSTRAINT CK_HomeBoxEntries_Kind CHECK (Kind IN (N'Khums', N'Aid', N'Asset', N'Other')),
    Amount          DECIMAL(18,2)  NOT NULL CONSTRAINT CK_HomeBoxEntries_Amount CHECK (Amount > 0),
    Title           NVARCHAR(200)  NULL,
    Beneficiary     NVARCHAR(150)  NULL,
    Notes           NVARCHAR(300)  NULL,
    CashBoxId       INT            NOT NULL FOREIGN KEY REFERENCES CashBoxes(Id),
    CashBoxTxId     INT            NULL FOREIGN KEY REFERENCES CashBoxTransactions(Id),
    JournalEntryId  INT            NULL FOREIGN KEY REFERENCES JournalEntries(Id),
    IsVoided        BIT            NOT NULL DEFAULT 0,
    VoidReason      NVARCHAR(300)  NULL,
    CreatedByUserId INT            NOT NULL FOREIGN KEY REFERENCES Users(Id),
    CreatedAt       DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_HomeBoxEntries_Date')
    CREATE INDEX IX_HomeBoxEntries_Date ON HomeBoxEntries (EntryDate, Kind);
GO

IF OBJECT_ID('HomeLoans', 'U') IS NULL
CREATE TABLE HomeLoans (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    LoanNumber      NVARCHAR(30)   NOT NULL CONSTRAINT UQ_HomeLoans_Number UNIQUE,
    ToCashBoxId     INT            NOT NULL FOREIGN KEY REFERENCES CashBoxes(Id),
    HomeCashBoxId   INT            NOT NULL FOREIGN KEY REFERENCES CashBoxes(Id),
    Amount          DECIMAL(18,2)  NOT NULL CONSTRAINT CK_HomeLoans_Amount CHECK (Amount > 0),
    LoanDate        DATE           NOT NULL,
    Reason          NVARCHAR(300)  NOT NULL,
    Status          NVARCHAR(10)   NOT NULL CONSTRAINT CK_HomeLoans_Status CHECK (Status IN (N'Open', N'Settled', N'Voided')),
    TransferGroup   UNIQUEIDENTIFIER NULL,
    SettledAt       DATETIME2      NULL,
    CreatedByUserId INT            NOT NULL FOREIGN KEY REFERENCES Users(Id),
    CreatedAt       DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

IF OBJECT_ID('HomeLoanRepayments', 'U') IS NULL
CREATE TABLE HomeLoanRepayments (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    LoanId          INT            NOT NULL FOREIGN KEY REFERENCES HomeLoans(Id),
    RepayDate       DATE           NOT NULL,
    Amount          DECIMAL(18,2)  NOT NULL CONSTRAINT CK_HomeLoanRepay_Amount CHECK (Amount > 0),
    FromCashBoxId   INT            NOT NULL FOREIGN KEY REFERENCES CashBoxes(Id),
    TransferGroup   UNIQUEIDENTIFIER NULL,
    Notes           NVARCHAR(300)  NULL,
    CreatedByUserId INT            NOT NULL FOREIGN KEY REFERENCES Users(Id),
    CreatedAt       DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_HomeLoanRepayments_Loan')
    CREATE INDEX IX_HomeLoanRepayments_Loan ON HomeLoanRepayments (LoanId);
GO

-- حركات الصندوق: صرف المنزل والتسوية بالجرد
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CashBoxTransactions_Type' AND definition LIKE '%HomeSpend%')
BEGIN
    IF OBJECT_ID('CK_CashBoxTransactions_Type', 'C') IS NOT NULL ALTER TABLE CashBoxTransactions DROP CONSTRAINT CK_CashBoxTransactions_Type;
    ALTER TABLE CashBoxTransactions ADD CONSTRAINT CK_CashBoxTransactions_Type CHECK (TxType IN (
        N'Opening', N'Deposit', N'Withdrawal', N'TransferIn', N'TransferOut',
        N'SalesReceipt', N'VoucherReceipt', N'VoucherPayment', N'RepHandover',
        N'CustomerDepositIn', N'CustomerDepositOut', N'EmployeeAdvance', N'PartnerWithdrawal',
        N'Expense', N'OtherIncome', N'TempWages', N'PayrollPayout', N'CustodyIssue', N'CustodyReturn',
        N'HomeSpend', N'Adjustment'));
END;
GO

-- طلبات الصرف: نوع التسوية
DECLARE @ck SYSNAME, @sql NVARCHAR(400);
DECLARE c CURSOR LOCAL FAST_FORWARD FOR
    SELECT cc.name FROM sys.check_constraints cc
    JOIN sys.columns col ON col.object_id = cc.parent_object_id AND col.column_id = cc.parent_column_id
    WHERE cc.parent_object_id = OBJECT_ID('PaymentRequests') AND col.name = 'Kind' AND cc.definition NOT LIKE '%CashAdjustment%';
OPEN c;
FETCH NEXT FROM c INTO @ck;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'ALTER TABLE PaymentRequests DROP CONSTRAINT ' + QUOTENAME(@ck);
    EXEC sp_executesql @sql;
    FETCH NEXT FROM c INTO @ck;
END
CLOSE c; DEALLOCATE c;
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PaymentRequests_Kind')
    ALTER TABLE PaymentRequests ADD CONSTRAINT CK_PaymentRequests_Kind
        CHECK (Kind IN (N'EmployeeAdvance', N'EmployeeWithdrawal', N'PayrollPayout', N'PartnerWithdrawal', N'CashAdjustment'));
GO

-- المطابقة: ما صُرف من صندوق المنزل منذ السابقة (توزيع أرباح لا خسارة)
IF COL_LENGTH('AssetReconciliations', 'HomeDistributionsSincePrevious') IS NULL
    ALTER TABLE AssetReconciliations ADD HomeDistributionsSincePrevious DECIMAL(18,2) NOT NULL CONSTRAINT DF_AssetRecon_HomeDist DEFAULT 0;
GO
