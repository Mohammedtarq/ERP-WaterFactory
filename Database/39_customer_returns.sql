/* ============================================================
   مرتجع الزبون (المرحلة 0-ب لتطبيق المندوبين، ومتاح في النظام المكتبي)
   - زبون يعيد بضاعة اشتراها: السليم يعود للمخزن/السيارة، والتالف لمخزن التالف
   - قيمته (بسعر آخر بيع له) إما تُخصم من دينه (سند «مرتجع» يدخل كشفه وتوزيع الدفعات)،
     أو تُرد نقدًا من محفظة المندوب
   - القيد: مدين إيرادات المبيعات / دائن العملاء (أو عهدة المندوبين عند الرد النقدي)
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */

-- طريقة سند جديدة: «مرتجع» (اشعار دائن للعميل، بلا حركة صندوق)
DECLARE @vck SYSNAME = (SELECT TOP 1 cc.name FROM sys.check_constraints cc
                        WHERE cc.parent_object_id = OBJECT_ID('Vouchers') AND cc.definition LIKE '%Cheque%' AND cc.definition NOT LIKE '%Return%');
IF @vck IS NOT NULL EXEC (N'ALTER TABLE Vouchers DROP CONSTRAINT [' + @vck + N']');
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Vouchers_PaymentMethod')
    ALTER TABLE Vouchers ADD CONSTRAINT CK_Vouchers_PaymentMethod CHECK (PaymentMethod IN (N'Cash', N'Bank', N'Cheque', N'Return'));
GO

-- نوع حركة مخزنية: مرتجع زبون
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_StockTransactions_Type' AND definition LIKE '%CustomerReturn%')
BEGIN
    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_StockTransactions_Type')
        ALTER TABLE StockTransactions DROP CONSTRAINT CK_StockTransactions_Type;
    ALTER TABLE StockTransactions ADD CONSTRAINT CK_StockTransactions_Type CHECK (TransactionType IN (
        N'Receipt', N'SalesIssue', N'ProductionConsume', N'ProductionOutput',
        N'Packing', N'Transfer', N'Damaged', N'FreeIssue', N'ReturnToWarehouse',
        N'RepLoad', N'RepSale', N'RepFreeSale', N'RepDamaged', N'RepReturn',
        N'SyncConflictAdjustment', N'Issue', N'WipIssue', N'WipReturn', N'WipAdjust',
        N'SalesVoid', N'StocktakeVariance', N'CustomerReturn'));
END;
GO

IF OBJECT_ID('CustomerReturns', 'U') IS NULL
CREATE TABLE CustomerReturns (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    ReturnNumber        NVARCHAR(30)    NOT NULL CONSTRAINT UQ_CustomerReturns_Number UNIQUE,
    CustomerId          INT             NOT NULL FOREIGN KEY REFERENCES Customers(Id),
    -- المخزن الذي يعود إليه السليم: سيارة المندوب أو مخزن المنتج التام
    WarehouseId         INT             NOT NULL FOREIGN KEY REFERENCES Warehouses(Id),
    RepEmployeeId       INT             NULL FOREIGN KEY REFERENCES Employees(Id),
    ReturnDate          DATE            NOT NULL,
    Settlement          NVARCHAR(10)    NOT NULL CHECK (Settlement IN (N'Debt', N'Cash')),
    TotalAmount         DECIMAL(18,2)   NOT NULL,
    Reason              NVARCHAR(300)   NOT NULL,
    JournalEntryId      INT             NULL FOREIGN KEY REFERENCES JournalEntries(Id),
    VoucherId           INT             NULL FOREIGN KEY REFERENCES Vouchers(Id),
    RepRequestId        INT             NULL FOREIGN KEY REFERENCES RepRequests(Id),
    CreatedByUserId     INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    CreatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
IF OBJECT_ID('CustomerReturnLines', 'U') IS NULL
CREATE TABLE CustomerReturnLines (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    CustomerReturnId    INT             NOT NULL FOREIGN KEY REFERENCES CustomerReturns(Id) ON DELETE CASCADE,
    ItemId              INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),
    PackagingLevelId    INT             NOT NULL FOREIGN KEY REFERENCES ItemPackagingLevels(Id),
    QuantityInLevel     DECIMAL(18,3)   NOT NULL CHECK (QuantityInLevel > 0),
    -- من الكمية: ما عاد تالفًا (يذهب لمخزن التالف)
    DamagedInLevel      DECIMAL(18,3)   NOT NULL DEFAULT 0,
    QuantityBaseUnits   DECIMAL(18,3)   NOT NULL,
    UnitPrice           DECIMAL(18,2)   NOT NULL,
    LineTotal           DECIMAL(18,2)   NOT NULL,
    BatchId             INT             NULL FOREIGN KEY REFERENCES ItemBatches(Id),
    CONSTRAINT CK_CustomerReturnLines_Damaged CHECK (DamagedInLevel >= 0 AND DamagedInLevel <= QuantityInLevel)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CustomerReturns_Customer')
    CREATE INDEX IX_CustomerReturns_Customer ON CustomerReturns (CustomerId, ReturnDate);
GO

-- كشف العميل: سند «مرتجع» يظهر باسمه بدل «سند قبض»
CREATE OR ALTER VIEW vw_CustomerStatement AS
SELECT  i.CustomerId, i.InvoiceDate AS TxDate,
        CASE WHEN i.IsOpeningBalance = 1 THEN N'OpeningBalance' ELSE N'SalesInvoice' END AS TxType,
        i.InvoiceNumber AS DocNumber, i.Id AS DocId,
        i.TotalAmount AS Debit, CAST(0 AS DECIMAL(18,2)) AS Credit,
        CASE WHEN i.IsOpeningBalance = 1 THEN ISNULL(i.Notes, N'رصيد افتتاحي') ELSE N'فاتورة مبيعات' END AS Description
FROM    SalesInvoices i
WHERE   i.Status = N'Posted' AND i.IsFreeSale = 0
UNION ALL
SELECT  i.CustomerId, i.InvoiceDate, N'PaidAtSale', i.InvoiceNumber, i.Id,
        0, i.AmountPaidNow, N'مدفوع عند البيع'
FROM    SalesInvoices i
WHERE   i.Status = N'Posted' AND i.IsFreeSale = 0 AND i.AmountPaidNow > 0
UNION ALL
SELECT  v.PartyId, v.VoucherDate,
        CASE WHEN v.PaymentMethod = N'Return' THEN N'CustomerReturn'
             WHEN v.VoucherType = N'Receipt' THEN N'ReceiptVoucher' ELSE N'PaymentVoucher' END,
        v.VoucherNumber, v.Id,
        CASE WHEN v.VoucherType = N'Payment' THEN v.Amount ELSE 0 END,
        CASE WHEN v.VoucherType = N'Receipt' THEN v.Amount ELSE 0 END,
        CASE WHEN v.PaymentMethod = N'Return' THEN ISNULL(v.Notes, N'مرتجع بضاعة')
             WHEN v.VoucherType = N'Receipt' THEN N'سند قبض' ELSE N'سند صرف' END
FROM    Vouchers v
WHERE   v.PartyType = N'Customer' AND v.PartyId IS NOT NULL AND v.IsVoided = 0;
GO
