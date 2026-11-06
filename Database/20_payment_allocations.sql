/* ============================================================
   كشف حساب العميل وتوزيع الدفعات على الفواتير الأقدم أولًا (قابل لإعادة التنفيذ)
   - كل فاتورة تحفظ المدفوع التراكمي والمتبقي
   - سجل توزيع لكل دفعة (سند قبض) على الفواتير: تلقائي بالأقدم أولًا، أو يدوي
   - دين العميل الفرعي مستقل عن وكيله (التوزيع والكشف على مستوى العميل نفسه)
   ============================================================ */
SET QUOTED_IDENTIFIER ON;
GO

IF COL_LENGTH('SalesInvoices', 'AmountSettled') IS NULL
    ALTER TABLE SalesInvoices ADD AmountSettled DECIMAL(18,2) NOT NULL CONSTRAINT DF_SalesInvoices_AmountSettled DEFAULT 0;
GO

IF COL_LENGTH('SalesInvoices', 'AmountRemaining') IS NULL
    ALTER TABLE SalesInvoices ADD AmountRemaining AS (TotalAmount - AmountSettled);
GO

IF OBJECT_ID('PaymentAllocations', 'U') IS NULL
CREATE TABLE PaymentAllocations (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    VoucherId           INT             NOT NULL FOREIGN KEY REFERENCES Vouchers(Id),
    SalesInvoiceId      INT             NOT NULL FOREIGN KEY REFERENCES SalesInvoices(Id),
    Amount              DECIMAL(18,2)   NOT NULL CHECK (Amount > 0),
    IsManual            BIT             NOT NULL DEFAULT 0,
    AllocatedByUserId   INT             NULL FOREIGN KEY REFERENCES Users(Id),
    CreatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PaymentAllocations_Voucher')
    CREATE INDEX IX_PaymentAllocations_Voucher ON PaymentAllocations (VoucherId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PaymentAllocations_Invoice')
    CREATE INDEX IX_PaymentAllocations_Invoice ON PaymentAllocations (SalesInvoiceId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SalesInvoices_Customer')
    CREATE INDEX IX_SalesInvoices_Customer ON SalesInvoices (CustomerId, InvoiceDate) INCLUDE (TotalAmount, AmountPaidNow, AmountSettled, Status, IsFreeSale);
GO

-- الفواتير المرحّلة قبل هذا الملف: المدفوع عند البيع هو أول ما يُحتسب مدفوعًا (التوزيع الكامل يُحدَّث عند فتح الكشف)
UPDATE SalesInvoices SET AmountSettled = CASE WHEN AmountPaidNow > TotalAmount THEN TotalAmount ELSE AmountPaidNow END
WHERE Status = N'Posted' AND AmountSettled = 0 AND AmountPaidNow > 0;
GO
