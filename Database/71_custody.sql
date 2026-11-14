/* ============================================================
   العهد (مسؤول المشتريات وحالات المندوبين الطارئة)
   - صرف عهدة نقدية من صندوق لموظف: مدين «عهد الموظفين» / دائن الصندوق
   - مصروف من العهدة: مدين باب المصروف / دائن «عهد الموظفين» (يظهر في مصاريف الباب)
   - إعادة المتبقي: مدين الصندوق / دائن «عهد الموظفين»
   - التجديد والتعديل والإلغاء (بقيد عكسي وبقاء السطر ظاهرًا) وتسوية العهدة
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */

IF OBJECT_ID('Custodies', 'U') IS NULL
CREATE TABLE Custodies (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    CustodyNumber   NVARCHAR(30)    NOT NULL CONSTRAINT UQ_Custodies_Number UNIQUE,
    EmployeeId      INT             NOT NULL FOREIGN KEY REFERENCES Employees(Id),
    Purpose         NVARCHAR(200)   NOT NULL,
    Status          NVARCHAR(10)    NOT NULL CONSTRAINT CK_Custodies_Status CHECK (Status IN (N'Active', N'Settled', N'Voided')),
    CashBoxId       INT             NULL FOREIGN KEY REFERENCES CashBoxes(Id),
    IssuedAt        DATE            NOT NULL,
    TargetAmount    DECIMAL(18,2)   NULL,
    ClosedAt        DATETIME2       NULL,
    ClosedByUserId  INT             NULL FOREIGN KEY REFERENCES Users(Id),
    VoidReason      NVARCHAR(300)   NULL,
    VoidedByUserId  INT             NULL FOREIGN KEY REFERENCES Users(Id),
    VoidedAt        DATETIME2       NULL,
    CreatedByUserId INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    CreatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

IF OBJECT_ID('CustodyTransactions', 'U') IS NULL
CREATE TABLE CustodyTransactions (
    Id               INT IDENTITY(1,1) PRIMARY KEY,
    CustodyId        INT             NOT NULL FOREIGN KEY REFERENCES Custodies(Id),
    TxDate           DATE            NOT NULL,
    Kind             NVARCHAR(10)    NOT NULL CONSTRAINT CK_CustodyTx_Kind CHECK (Kind IN (N'Issue', N'Expense', N'Return')),
    Amount           DECIMAL(18,2)   NOT NULL CONSTRAINT CK_CustodyTx_Amount CHECK (Amount > 0),
    OriginalAmount   DECIMAL(18,2)   NULL,
    CategoryId       INT             NULL FOREIGN KEY REFERENCES FinanceCategories(Id),
    Description      NVARCHAR(300)   NULL,
    ReceiptNumber    NVARCHAR(60)    NULL,
    HasReceiptPhoto  BIT             NOT NULL DEFAULT 0,
    JournalEntryId   INT             NULL FOREIGN KEY REFERENCES JournalEntries(Id),
    FinanceEntryId   INT             NULL FOREIGN KEY REFERENCES FinanceEntries(Id),
    CashBoxTxId      INT             NULL FOREIGN KEY REFERENCES CashBoxTransactions(Id),
    ClientId         UNIQUEIDENTIFIER NULL,
    IsVoided         BIT             NOT NULL DEFAULT 0,
    VoidReason       NVARCHAR(300)   NULL,
    VoidedByUserId   INT             NULL FOREIGN KEY REFERENCES Users(Id),
    VoidedAt         DATETIME2       NULL,
    ModifiedByUserId INT             NULL FOREIGN KEY REFERENCES Users(Id),
    ModifiedAt       DATETIME2       NULL,
    CreatedByUserId  INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    CreatedAt        DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CustodyTx_ClientId')
    CREATE UNIQUE INDEX UX_CustodyTx_ClientId ON CustodyTransactions (ClientId) WHERE ClientId IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CustodyTx_Custody')
    CREATE INDEX IX_CustodyTx_Custody ON CustodyTransactions (CustodyId, TxDate);
GO

-- أنواع حركة صندوق جديدة: صرف عهدة وإعادة عهدة
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CashBoxTransactions_Type' AND definition LIKE '%CustodyIssue%')
BEGIN
    IF OBJECT_ID('CK_CashBoxTransactions_Type', 'C') IS NOT NULL ALTER TABLE CashBoxTransactions DROP CONSTRAINT CK_CashBoxTransactions_Type;
    ALTER TABLE CashBoxTransactions ADD CONSTRAINT CK_CashBoxTransactions_Type CHECK (TxType IN (
        N'Opening', N'Deposit', N'Withdrawal', N'TransferIn', N'TransferOut',
        N'SalesReceipt', N'VoucherReceipt', N'VoucherPayment', N'RepHandover',
        N'CustomerDepositIn', N'CustomerDepositOut', N'EmployeeAdvance', N'PartnerWithdrawal',
        N'Expense', N'OtherIncome', N'TempWages', N'PayrollPayout', N'CustodyIssue', N'CustodyReturn'));
END;
GO

-- تنبيهات العهد: عدد أيام العهدة بلا تسوية، ونسبة الرصيد المنخفض
IF COL_LENGTH('RepAppSettings', 'CustodyAlertDays') IS NULL ALTER TABLE RepAppSettings ADD CustodyAlertDays INT NOT NULL CONSTRAINT DF_RepAppSettings_CustodyDays DEFAULT 7;
GO
IF COL_LENGTH('RepAppSettings', 'CustodyLowPercent') IS NULL ALTER TABLE RepAppSettings ADD CustodyLowPercent INT NOT NULL CONSTRAINT DF_RepAppSettings_CustodyLow DEFAULT 20;
GO
IF COL_LENGTH('RepAppSettings', 'CustodyReceiptAbove') IS NULL ALTER TABLE RepAppSettings ADD CustodyReceiptAbove DECIMAL(18,2) NOT NULL CONSTRAINT DF_RepAppSettings_CustodyReceipt DEFAULT 25000;
GO

-- حركة هاتف جديدة: مصروف من العهدة (CustodyExpense) — يوسَّع قيد نوع الحركة في RepRequests
DECLARE @ck SYSNAME, @sql NVARCHAR(400);
DECLARE c CURSOR LOCAL FAST_FORWARD FOR
    SELECT cc.name FROM sys.check_constraints cc
    JOIN sys.columns col ON col.object_id = cc.parent_object_id AND col.column_id = cc.parent_column_id
    WHERE cc.parent_object_id = OBJECT_ID('RepRequests') AND col.name = 'Kind' AND cc.definition NOT LIKE '%CustodyExpense%';
OPEN c;
FETCH NEXT FROM c INTO @ck;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'ALTER TABLE RepRequests DROP CONSTRAINT ' + QUOTENAME(@ck);
    EXEC sp_executesql @sql;
    FETCH NEXT FROM c INTO @ck;
END
CLOSE c; DEALLOCATE c;
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_RepRequests_Kind')
    ALTER TABLE RepRequests ADD CONSTRAINT CK_RepRequests_Kind
        CHECK (Kind IN (N'CashSale', N'CreditSale', N'Free', N'Collection', N'NewCustomer', N'Expense', N'Return', N'CustomerLink', N'CustodyExpense'));
GO
