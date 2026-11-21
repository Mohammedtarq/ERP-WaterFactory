/* ============================================================
   موافقة القسم المالي على الصرف: طلبات صرف (سلفة، مسحوب، رواتب، سحب شريك) تنتظر الاعتماد قبل خروج النقد،
   وخطوة صرف الرواتب من الصندوق بعد اعتماد المسير. قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF OBJECT_ID('PaymentRequests', 'U') IS NULL
CREATE TABLE PaymentRequests (
    Id                INT            NOT NULL IDENTITY PRIMARY KEY,
    RequestNumber     NVARCHAR(30)   NOT NULL CONSTRAINT UQ_PaymentRequests_Number UNIQUE,
    Kind              NVARCHAR(30)   NOT NULL CONSTRAINT CK_PaymentRequests_Kind CHECK (Kind IN (N'EmployeeAdvance', N'EmployeeWithdrawal', N'PayrollPayout', N'PartnerWithdrawal')),
    Status            NVARCHAR(20)   NOT NULL CONSTRAINT DF_PaymentRequests_Status DEFAULT N'Pending' CONSTRAINT CK_PaymentRequests_Status CHECK (Status IN (N'Pending', N'Approved', N'Rejected')),
    Amount            DECIMAL(18,2)  NOT NULL,
    Summary           NVARCHAR(300)  NOT NULL,
    Payload           NVARCHAR(MAX)  NOT NULL,
    CashBoxId         INT            NULL,
    RequestedByUserId INT            NOT NULL CONSTRAINT FK_PaymentRequests_RequestedBy REFERENCES Users(Id),
    RequestedAt       DATETIME2      NOT NULL CONSTRAINT DF_PaymentRequests_RequestedAt DEFAULT SYSUTCDATETIME(),
    DecidedByUserId   INT            NULL CONSTRAINT FK_PaymentRequests_DecidedBy REFERENCES Users(Id),
    DecidedAt         DATETIME2      NULL,
    RejectReason      NVARCHAR(300)  NULL,
    ResultTable       NVARCHAR(50)   NULL,
    ResultId          INT            NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PaymentRequests_Status')
    CREATE INDEX IX_PaymentRequests_Status ON PaymentRequests (Status, RequestedAt);
GO
IF COL_LENGTH('PayrollRuns', 'PaidAt') IS NULL ALTER TABLE PayrollRuns ADD PaidAt DATETIME2 NULL;
GO
IF COL_LENGTH('PayrollRuns', 'PaidByUserId') IS NULL ALTER TABLE PayrollRuns ADD PaidByUserId INT NULL;
GO
IF COL_LENGTH('PayrollRuns', 'PayoutJournalEntryId') IS NULL ALTER TABLE PayrollRuns ADD PayoutJournalEntryId INT NULL;
GO
-- نوع حركة صندوق: صرف رواتب
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CashBoxTransactions_Type' AND definition LIKE '%PayrollPayout%')
BEGIN
    IF OBJECT_ID('CK_CashBoxTransactions_Type', 'C') IS NOT NULL ALTER TABLE CashBoxTransactions DROP CONSTRAINT CK_CashBoxTransactions_Type;
    ALTER TABLE CashBoxTransactions ADD CONSTRAINT CK_CashBoxTransactions_Type CHECK (TxType IN (
        N'Opening', N'Deposit', N'Withdrawal', N'TransferIn', N'TransferOut',
        N'SalesReceipt', N'VoucherReceipt', N'VoucherPayment', N'RepHandover',
        N'CustomerDepositIn', N'CustomerDepositOut', N'EmployeeAdvance', N'PartnerWithdrawal',
        N'Expense', N'OtherIncome', N'TempWages', N'PayrollPayout'));
END;
GO
