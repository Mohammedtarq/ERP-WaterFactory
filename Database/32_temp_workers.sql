/* ============================================================
   المرحلة م5 (الجزء الذي لا يحتاج ملف البصمة):
   - العمال الوقتيون: أجر يومي، بلا بصمة، بلا سلف؛ كشف أيام يدوي، ويُصرف أجرهم أسبوعيًا أو عند إنهاء الخدمة
   - معفى من البصمة: لا يُخصم غيابه من الراتب
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */

IF COL_LENGTH('Employees', 'IsTemporary') IS NULL
    ALTER TABLE Employees ADD IsTemporary BIT NOT NULL CONSTRAINT DF_Employees_IsTemporary DEFAULT 0;
IF COL_LENGTH('Employees', 'DailyWage') IS NULL
    ALTER TABLE Employees ADD DailyWage DECIMAL(18,2) NULL;
IF COL_LENGTH('Employees', 'AttendanceExempt') IS NULL
    ALTER TABLE Employees ADD AttendanceExempt BIT NOT NULL CONSTRAINT DF_Employees_AttendanceExempt DEFAULT 0;
IF COL_LENGTH('Employees', 'EndOfServiceDate') IS NULL
    ALTER TABLE Employees ADD EndOfServiceDate DATE NULL;
GO

IF OBJECT_ID('TempWorkerPayments', 'U') IS NULL
CREATE TABLE TempWorkerPayments (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    PaymentNumber       NVARCHAR(30)    NOT NULL UNIQUE,
    EmployeeId          INT             NOT NULL FOREIGN KEY REFERENCES Employees(Id),
    PaidDate            DATE            NOT NULL,
    FromDate            DATE            NOT NULL,
    ToDate              DATE            NOT NULL,
    Days                DECIMAL(6,2)    NOT NULL,
    DailyWage           DECIMAL(18,2)   NOT NULL,
    Amount              DECIMAL(18,2)   NOT NULL,
    IsFinal             BIT             NOT NULL DEFAULT 0,      -- تسوية نهاية الخدمة
    JournalEntryId      INT             NULL FOREIGN KEY REFERENCES JournalEntries(Id),
    CreatedByUserId     INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    CreatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

IF OBJECT_ID('TempWorkDays', 'U') IS NULL
CREATE TABLE TempWorkDays (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    EmployeeId          INT             NOT NULL FOREIGN KEY REFERENCES Employees(Id),
    WorkDate            DATE            NOT NULL,
    Days                DECIMAL(4,2)    NOT NULL CHECK (Days > 0 AND Days <= 1.5),   -- يوم، نصف يوم، أو يوم ونصف (إضافي)
    Notes               NVARCHAR(200)   NULL,
    PaymentId           INT             NULL FOREIGN KEY REFERENCES TempWorkerPayments(Id),
    CreatedByUserId     INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    CONSTRAINT UQ_TempWorkDays UNIQUE (EmployeeId, WorkDate)
);
GO

CREATE OR ALTER TRIGGER trg_TempWorkerPayments_PeriodLock ON TempWorkerPayments AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @lock DATE = dbo.fn_PeriodLockedThrough();
    IF @lock IS NULL RETURN;
    IF EXISTS (SELECT 1 FROM inserted WHERE PaidDate <= @lock) OR EXISTS (SELECT 1 FROM deleted WHERE PaidDate <= @lock)
        EXEC sp_Period_ThrowLocked @lock;
END;
GO

-- ---------- نوع حركة صندوق: أجور العمال الوقتيين ----------
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CashBoxTransactions_Type' AND definition LIKE '%TempWages%')
BEGIN
    IF OBJECT_ID('CK_CashBoxTransactions_Type', 'C') IS NOT NULL ALTER TABLE CashBoxTransactions DROP CONSTRAINT CK_CashBoxTransactions_Type;
    ALTER TABLE CashBoxTransactions ADD CONSTRAINT CK_CashBoxTransactions_Type CHECK (TxType IN (
        N'Opening', N'Deposit', N'Withdrawal', N'TransferIn', N'TransferOut',
        N'SalesReceipt', N'VoucherReceipt', N'VoucherPayment', N'RepHandover',
        N'CustomerDepositIn', N'CustomerDepositOut', N'EmployeeAdvance', N'PartnerWithdrawal',
        N'Expense', N'OtherIncome', N'TempWages'));
END;
GO
