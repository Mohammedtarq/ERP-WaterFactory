/* ============================================================
   الوحدة المالية — العقل المالي، السندات، القيود، أسعار الصرف
   ============================================================ */

-- ============ دليل الحسابات (يُنشأ تلقائيًا وقت الإعداد الأولي) ============
CREATE TABLE ChartOfAccounts (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    AccountCode         NVARCHAR(20)    NOT NULL UNIQUE,
    AccountName         NVARCHAR(200)   NOT NULL,
    AccountType         NVARCHAR(20)    NOT NULL
                            CHECK (AccountType IN (N'Asset', N'Liability', N'Equity', N'Revenue', N'Expense')),
    ParentAccountId     INT             NULL FOREIGN KEY REFERENCES ChartOfAccounts(Id),
    IsActive            BIT             NOT NULL DEFAULT 1
);
GO

-- ============ قواعد الربط التلقائي (العقل المالي) ============
-- كل نوع عملية (فاتورة مبيعات، سند قبض نقدي، راتب...) مرتبط تلقائيًا بحسابين افتراضيين
CREATE TABLE AccountMappingRules (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    TransactionType     NVARCHAR(60)    NOT NULL UNIQUE,   -- مثال: CashReceiptVoucher, SalesInvoiceCash, PayrollDisbursement
    DebitAccountId      INT             NOT NULL FOREIGN KEY REFERENCES ChartOfAccounts(Id),
    CreditAccountId     INT             NOT NULL FOREIGN KEY REFERENCES ChartOfAccounts(Id)
);
GO

-- ============ القيود المحاسبية (رأس القيد) ============
CREATE TABLE JournalEntries (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    EntryNumber     NVARCHAR(30)    NOT NULL UNIQUE,
    EntryDate       DATE            NOT NULL,
    EntryType       NVARCHAR(30)    NOT NULL
                        CHECK (EntryType IN (N'Manual', N'AutoVoucher', N'AutoSales', N'AutoPurchase', N'AutoPayroll', N'AutoProduction')),
    Description     NVARCHAR(400)   NULL,
    CreatedByUserId INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    IsPosted        BIT             NOT NULL DEFAULT 0,     -- يتطلب توازن مدين = دائن قبل الترحيل
    SourceTable     NVARCHAR(60)    NULL,                   -- اسم الجدول المصدر (إن وُجد): SalesInvoices, Vouchers ...
    SourceId        INT             NULL,
    CreatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- ============ سطور القيد ============
CREATE TABLE JournalEntryLines (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    JournalEntryId  INT             NOT NULL FOREIGN KEY REFERENCES JournalEntries(Id),
    AccountId       INT             NOT NULL FOREIGN KEY REFERENCES ChartOfAccounts(Id),
    Debit           DECIMAL(18,2)   NOT NULL DEFAULT 0,
    Credit          DECIMAL(18,2)   NOT NULL DEFAULT 0,
    Description     NVARCHAR(300)   NULL
);
GO

-- ============ السندات المبسّطة (قبض / صرف) ============
CREATE TABLE Vouchers (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    VoucherNumber       NVARCHAR(30)    NOT NULL UNIQUE,
    VoucherType         NVARCHAR(10)    NOT NULL CHECK (VoucherType IN (N'Receipt', N'Payment')),
    PartyType           NVARCHAR(20)    NOT NULL CHECK (PartyType IN (N'Customer', N'Supplier', N'Employee', N'Other')),
    PartyId             INT             NULL,               -- CustomerId أو SupplierId أو EmployeeId حسب PartyType
    Amount              DECIMAL(18,2)   NOT NULL,
    Currency            NVARCHAR(3)     NOT NULL DEFAULT N'IQD',
    PaymentMethod       NVARCHAR(20)    NOT NULL CHECK (PaymentMethod IN (N'Cash', N'Bank', N'Cheque')),
    VoucherDate         DATE            NOT NULL,
    LinkedInvoiceTable  NVARCHAR(40)    NULL,                -- SalesInvoices أو PurchaseOrders عند الربط
    LinkedInvoiceId     INT             NULL,
    JournalEntryId      INT             NULL FOREIGN KEY REFERENCES JournalEntries(Id),
    Notes               NVARCHAR(300)   NULL,
    CreatedByUserId     INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    CreatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- ============ أسعار الصرف (تُستخدم فقط للعرض/تقارير الرواتب بالدولار) ============
CREATE TABLE ExchangeRates (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    EffectiveDate   DATE            NOT NULL,
    CurrencyCode    NVARCHAR(3)     NOT NULL DEFAULT N'USD',
    RateToIQD       DECIMAL(18,4)   NOT NULL,
    EnteredByUserId INT             NOT NULL FOREIGN KEY REFERENCES Users(Id)
);
GO
