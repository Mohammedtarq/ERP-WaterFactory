/* ============================================================
   الضوابط (المرحلة م0):
   - سجل الحركات العام (من أضاف/عدّل/ألغى ماذا ومتى)
   - قفل الفترات المحاسبية: لا إضافة ولا تعديل بتاريخ داخل فترة مقفلة إلا بعد فتحها من المدير
   - سقف الصرف لكل دور، ونوع صندوق "بنك / إلكتروني"
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */

IF OBJECT_ID('AuditLogs', 'U') IS NULL
CREATE TABLE AuditLogs (
    Id          BIGINT IDENTITY(1,1) PRIMARY KEY,
    AtUtc       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    UserId      INT             NULL,
    Action      NVARCHAR(20)    NOT NULL,          -- Insert / Update / Delete / Void / Post / Close / Reopen / Login ...
    TableName   NVARCHAR(80)    NOT NULL,
    RecordId    NVARCHAR(40)    NULL,
    Summary     NVARCHAR(400)   NULL,
    Changes     NVARCHAR(MAX)   NULL               -- JSON: {"Field": ["قبل", "بعد"]}
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AuditLogs_At')
    CREATE INDEX IX_AuditLogs_At ON AuditLogs (AtUtc DESC) INCLUDE (UserId, Action, TableName);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AuditLogs_Record')
    CREATE INDEX IX_AuditLogs_Record ON AuditLogs (TableName, RecordId);
GO

-- كل إغلاق أو فتح يُسجَّل صفًا جديدًا؛ القفل الساري = آخر صف
IF OBJECT_ID('PeriodLocks', 'U') IS NULL
CREATE TABLE PeriodLocks (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    LockedThrough   DATE            NULL,              -- NULL = لا قفل
    Action          NVARCHAR(10)    NOT NULL CHECK (Action IN (N'Close', N'Reopen')),
    Reason          NVARCHAR(300)   NULL,
    UserId          INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    AtUtc           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

CREATE OR ALTER FUNCTION fn_PeriodLockedThrough()
RETURNS DATE
AS
BEGIN
    RETURN (SELECT TOP 1 LockedThrough FROM PeriodLocks ORDER BY Id DESC);
END;
GO

/* ------------------------------------------------------------
   مشغّلات القفل: أي إضافة أو تعديل أو حذف لسجل تاريخه داخل الفترة المقفلة يُرفض،
   سواء جاء من شاشة أو إجراء مخزَّن أو أداة نقل. الرسالة نفسها في كل مكان.
   ------------------------------------------------------------ */
CREATE OR ALTER PROCEDURE sp_Period_ThrowLocked @LockedThrough DATE
AS
BEGIN
    DECLARE @msg NVARCHAR(400) = N'الفترة حتى ' + CONVERT(NVARCHAR(10), @LockedThrough, 23)
        + N' مقفلة. لا يمكن إضافة أو تعديل أو إلغاء حركات بتاريخ داخلها إلا بعد فتحها من المدير.';
    THROW 51100, @msg, 1;
END;
GO

CREATE OR ALTER TRIGGER trg_JournalEntries_PeriodLock ON JournalEntries AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @lock DATE = dbo.fn_PeriodLockedThrough();
    IF @lock IS NULL RETURN;
    IF EXISTS (SELECT 1 FROM inserted WHERE EntryDate <= @lock) OR EXISTS (SELECT 1 FROM deleted WHERE EntryDate <= @lock)
        EXEC sp_Period_ThrowLocked @lock;
END;
GO

CREATE OR ALTER TRIGGER trg_SalesInvoices_PeriodLock ON SalesInvoices AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @lock DATE = dbo.fn_PeriodLockedThrough();
    IF @lock IS NULL RETURN;
    -- تحديث الأعمدة المحسوبة للتوزيع (AmountSettled) على فاتورة قديمة مسموح: سند قبض جديد يسدد دينًا قديمًا
    IF EXISTS (SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.Id = i.Id
               WHERE i.InvoiceDate <= @lock
                 AND (d.Id IS NULL OR d.Status <> i.Status OR d.TotalAmount <> i.TotalAmount OR d.InvoiceDate <> i.InvoiceDate
                      OR d.CustomerId <> i.CustomerId OR d.AmountPaidNow <> i.AmountPaidNow))
       OR EXISTS (SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.Id = d.Id WHERE i.Id IS NULL AND d.InvoiceDate <= @lock)
       OR EXISTS (SELECT 1 FROM deleted d JOIN inserted i ON i.Id = d.Id WHERE d.InvoiceDate <= @lock AND i.InvoiceDate > @lock)
        EXEC sp_Period_ThrowLocked @lock;
END;
GO

CREATE OR ALTER TRIGGER trg_Vouchers_PeriodLock ON Vouchers AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @lock DATE = dbo.fn_PeriodLockedThrough();
    IF @lock IS NULL RETURN;
    IF EXISTS (SELECT 1 FROM inserted WHERE VoucherDate <= @lock) OR EXISTS (SELECT 1 FROM deleted WHERE VoucherDate <= @lock)
        EXEC sp_Period_ThrowLocked @lock;
END;
GO

CREATE OR ALTER TRIGGER trg_CashBoxTransactions_PeriodLock ON CashBoxTransactions AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @lock DATE = dbo.fn_PeriodLockedThrough();
    IF @lock IS NULL RETURN;
    IF EXISTS (SELECT 1 FROM inserted WHERE TxDate <= @lock) OR EXISTS (SELECT 1 FROM deleted WHERE TxDate <= @lock)
        EXEC sp_Period_ThrowLocked @lock;
END;
GO

CREATE OR ALTER TRIGGER trg_StockDocuments_PeriodLock ON StockDocuments AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @lock DATE = dbo.fn_PeriodLockedThrough();
    IF @lock IS NULL RETURN;
    IF EXISTS (SELECT 1 FROM inserted WHERE DocumentDate <= @lock) OR EXISTS (SELECT 1 FROM deleted WHERE DocumentDate <= @lock)
        EXEC sp_Period_ThrowLocked @lock;
END;
GO

CREATE OR ALTER TRIGGER trg_GoodsReceipts_PeriodLock ON GoodsReceipts AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @lock DATE = dbo.fn_PeriodLockedThrough();
    IF @lock IS NULL RETURN;
    IF EXISTS (SELECT 1 FROM inserted WHERE ReceiptDate <= @lock) OR EXISTS (SELECT 1 FROM deleted WHERE ReceiptDate <= @lock)
        EXEC sp_Period_ThrowLocked @lock;
END;
GO

-- ---------- سقف الصرف لكل دور (NULL = بلا سقف) ----------
IF COL_LENGTH('Roles', 'MaxPaymentAmount') IS NULL
    ALTER TABLE Roles ADD MaxPaymentAmount DECIMAL(18,2) NULL;
GO

-- ---------- نوع صندوق "بنك / إلكتروني" ----------
DECLARE @oldBoxCk sysname = (SELECT TOP 1 name FROM sys.check_constraints
                             WHERE parent_object_id = OBJECT_ID('CashBoxes') AND definition LIKE N'%BoxType%' AND definition NOT LIKE N'%Bank%');
IF @oldBoxCk IS NOT NULL EXEC (N'ALTER TABLE CashBoxes DROP CONSTRAINT [' + @oldBoxCk + N']');
IF OBJECT_ID('CK_CashBoxes_BoxType', 'C') IS NULL
    ALTER TABLE CashBoxes ADD CONSTRAINT CK_CashBoxes_BoxType CHECK (BoxType IN (N'Main', N'User', N'Bank'));
GO

-- ---------- نوعا حركة مخزنية: عكس فاتورة ملغاة، وفرق الجرد ----------
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_StockTransactions_Type' AND definition LIKE '%SalesVoid%')
BEGIN
    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_StockTransactions_Type')
        ALTER TABLE StockTransactions DROP CONSTRAINT CK_StockTransactions_Type;
    ALTER TABLE StockTransactions ADD CONSTRAINT CK_StockTransactions_Type CHECK (TransactionType IN (
        N'Receipt', N'SalesIssue', N'ProductionConsume', N'ProductionOutput',
        N'Packing', N'Transfer', N'Damaged', N'FreeIssue', N'ReturnToWarehouse',
        N'RepLoad', N'RepSale', N'RepFreeSale', N'RepDamaged', N'RepReturn',
        N'SyncConflictAdjustment', N'Issue', N'WipIssue', N'WipReturn', N'WipAdjust',
        N'SalesVoid', N'StocktakeVariance'));
END;
GO
