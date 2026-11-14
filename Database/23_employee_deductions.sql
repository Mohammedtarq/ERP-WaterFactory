/* ============================================================
   سلف ومسحوبات وعقوبات الموظفين (قابل لإعادة التنفيذ)
   - السلفة: مبلغ يُصرف من الصندوق ويُستقطع بأقساط شهرية حتى السداد
   - المسحوب: مبلغ يُصرف حسب الطلب ويُستقطع كاملًا من راتب الشهر
   - العقوبة: خصم من الراتب بسبب مكتوب، بلا صرف نقدي
   - كل استقطاع فعلي يُسجَّل قسطًا مربوطًا بدورة الرواتب التي خُصم فيها
   ============================================================ */
SET QUOTED_IDENTIFIER ON;
GO

IF NOT EXISTS (SELECT 1 FROM sys.sequences WHERE name = 'seq_EmployeeDeduction')
    CREATE SEQUENCE seq_EmployeeDeduction AS INT START WITH 1 INCREMENT BY 1;
GO

IF OBJECT_ID('EmployeeDeductions', 'U') IS NULL
CREATE TABLE EmployeeDeductions (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    DeductionNumber     NVARCHAR(30)    NOT NULL UNIQUE,
    EmployeeId          INT             NOT NULL FOREIGN KEY REFERENCES Employees(Id),
    Kind                NVARCHAR(20)    NOT NULL CHECK (Kind IN (N'Loan', N'Withdrawal', N'Penalty')),
    EntryDate           DATE            NOT NULL,
    Amount              DECIMAL(18,2)   NOT NULL CHECK (Amount > 0),          -- بالدينار
    MonthlyInstallment  DECIMAL(18,2)   NULL,                                 -- للسلفة فقط
    StartMonth          INT             NOT NULL CHECK (StartMonth BETWEEN 1 AND 12),
    StartYear           INT             NOT NULL,
    IsOpening           BIT             NOT NULL DEFAULT 0,                   -- رصيد منقول من نظام سابق (بلا صرف نقدي)
    Reason              NVARCHAR(300)   NULL,
    JournalEntryId      INT             NULL FOREIGN KEY REFERENCES JournalEntries(Id),
    IsVoided            BIT             NOT NULL DEFAULT 0,
    VoidReason          NVARCHAR(200)   NULL,
    CreatedByUserId     INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    CreatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

IF OBJECT_ID('EmployeeDeductionInstallments', 'U') IS NULL
CREATE TABLE EmployeeDeductionInstallments (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    EmployeeDeductionId INT             NOT NULL FOREIGN KEY REFERENCES EmployeeDeductions(Id),
    PayrollRunId        INT             NOT NULL FOREIGN KEY REFERENCES PayrollRuns(Id),
    Amount              DECIMAL(18,2)   NOT NULL CHECK (Amount > 0)           -- بالدينار
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_EmployeeDeductions_Employee')
    CREATE INDEX IX_EmployeeDeductions_Employee ON EmployeeDeductions (EmployeeId, StartYear, StartMonth) INCLUDE (Kind, Amount, IsVoided);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_EmployeeDeductionInstallments_Run')
    CREATE INDEX IX_EmployeeDeductionInstallments_Run ON EmployeeDeductionInstallments (PayrollRunId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_EmployeeDeductionInstallments_Deduction')
    CREATE INDEX IX_EmployeeDeductionInstallments_Deduction ON EmployeeDeductionInstallments (EmployeeDeductionId);
GO

-- أعمدة الاستقطاعات في سطر الراتب (بعملة الراتب)
IF COL_LENGTH('PayrollLines', 'LoanDeduction') IS NULL
    ALTER TABLE PayrollLines ADD LoanDeduction DECIMAL(18,2) NOT NULL CONSTRAINT DF_PayrollLines_Loan DEFAULT 0;
GO
IF COL_LENGTH('PayrollLines', 'WithdrawalDeduction') IS NULL
    ALTER TABLE PayrollLines ADD WithdrawalDeduction DECIMAL(18,2) NOT NULL CONSTRAINT DF_PayrollLines_Withdrawal DEFAULT 0;
GO
IF COL_LENGTH('PayrollLines', 'PenaltyDeduction') IS NULL
    ALTER TABLE PayrollLines ADD PenaltyDeduction DECIMAL(18,2) NOT NULL CONSTRAINT DF_PayrollLines_Penalty DEFAULT 0;
GO

-- حركة صندوق جديدة: صرف سلفة/مسحوب لموظف
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CashBoxTransactions_Type' AND definition LIKE '%EmployeeAdvance%')
BEGIN
    DECLARE @old SYSNAME = (SELECT TOP 1 cc.name FROM sys.check_constraints cc
                            WHERE cc.parent_object_id = OBJECT_ID('CashBoxTransactions') AND cc.definition LIKE '%RepHandover%');
    IF @old IS NOT NULL EXEC (N'ALTER TABLE CashBoxTransactions DROP CONSTRAINT [' + @old + N']');
    ALTER TABLE CashBoxTransactions ADD CONSTRAINT CK_CashBoxTransactions_Type CHECK (TxType IN (
        N'Opening', N'Deposit', N'Withdrawal', N'TransferIn', N'TransferOut',
        N'SalesReceipt', N'VoucherReceipt', N'VoucherPayment', N'RepHandover',
        N'CustomerDepositIn', N'CustomerDepositOut', N'EmployeeAdvance'));
END;
GO
