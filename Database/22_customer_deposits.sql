/* ============================================================
   تأمينات العملاء (قابل لإعادة التنفيذ)
   - مبالغ يودعها العميل كأمانة (مثل تأمين طباعة ستيكر خاص باسمه)، منفصلة تمامًا عن دينه
   - سند استلام يدخل الصندوق، وسند إرجاع يخرج منه، ورصيد افتتاحي (عند النقل من نظام سابق) بلا حركة صندوق
   - كل سند له قيد محاسبي على حساب "تأمينات العملاء" (التزام)
   ============================================================ */
SET QUOTED_IDENTIFIER ON;
GO

IF NOT EXISTS (SELECT 1 FROM sys.sequences WHERE name = 'seq_CustomerDeposit')
    CREATE SEQUENCE seq_CustomerDeposit AS INT START WITH 1 INCREMENT BY 1;
GO

IF OBJECT_ID('CustomerDeposits', 'U') IS NULL
CREATE TABLE CustomerDeposits (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    DepositNumber       NVARCHAR(30)    NOT NULL UNIQUE,
    CustomerId          INT             NOT NULL FOREIGN KEY REFERENCES Customers(Id),
    DepositDate         DATE            NOT NULL,
    Kind                NVARCHAR(20)    NOT NULL CHECK (Kind IN (N'Receipt', N'Refund', N'Opening')),
    Amount              DECIMAL(18,2)   NOT NULL CHECK (Amount > 0),      -- بالدينار دائمًا
    Currency            NVARCHAR(10)    NOT NULL DEFAULT N'IQD',          -- عملة الاستلام الأصلية
    CurrencyAmount      DECIMAL(18,2)   NULL,                             -- المبلغ بعملته إن لم تكن دينارًا
    Purpose             NVARCHAR(200)   NULL,                             -- مثال: تأمين ستيكر خاص
    CustomRecipeId      INT             NULL FOREIGN KEY REFERENCES CustomRecipes(Id),
    Notes               NVARCHAR(400)   NULL,
    JournalEntryId      INT             NULL FOREIGN KEY REFERENCES JournalEntries(Id),
    IsVoided            BIT             NOT NULL DEFAULT 0,
    VoidReason          NVARCHAR(200)   NULL,
    CreatedByUserId     INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    CreatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CustomerDeposits_Customer')
    CREATE INDEX IX_CustomerDeposits_Customer ON CustomerDeposits (CustomerId, DepositDate) INCLUDE (Kind, Amount, IsVoided);
GO

-- حركتا صندوق جديدتان: استلام تأمين / إرجاع تأمين (القيد الأصلي بلا اسم ← قيد مسمّى)
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CashBoxTransactions_Type')
BEGIN
    DECLARE @old SYSNAME = (SELECT TOP 1 cc.name FROM sys.check_constraints cc
                            WHERE cc.parent_object_id = OBJECT_ID('CashBoxTransactions') AND cc.definition LIKE '%RepHandover%');
    IF @old IS NOT NULL EXEC (N'ALTER TABLE CashBoxTransactions DROP CONSTRAINT [' + @old + N']');
    ALTER TABLE CashBoxTransactions ADD CONSTRAINT CK_CashBoxTransactions_Type CHECK (TxType IN (
        N'Opening', N'Deposit', N'Withdrawal', N'TransferIn', N'TransferOut',
        N'SalesReceipt', N'VoucherReceipt', N'VoucherPayment', N'RepHandover',
        N'CustomerDepositIn', N'CustomerDepositOut'));
END;
GO
