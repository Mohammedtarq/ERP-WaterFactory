/* ============================================================
   فهارس الأداء للجداول كثيرة الاستخدام (قابل لإعادة التنفيذ بأمان)
   - SQL Server لا يُنشئ فهارس للمفاتيح الأجنبية تلقائيًا: كل ربط (Join) بين مستند وسطوره يحتاج فهرسًا.
   - الفهارس "المغطّية" (INCLUDE) تجيب استعلامات الأرصدة واللوحات من الفهرس وحده دون قراءة الجدول.
   ============================================================ */
SET QUOTED_IDENTIFIER ON;
GO

-- ---------- حركة المخزون (أكثر جدول نموًا) ----------
-- الأرصدة: SUM(QuantityBaseUnits) لكل صنف/مخزن/تشغيلة — نسخة مغطّية تحل محل الفهرس الأصلي
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_StockTransactions_Balance')
BEGIN
    CREATE INDEX IX_StockTransactions_Balance ON StockTransactions (ItemId, WarehouseId, BatchId) INCLUDE (QuantityBaseUnits);
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_StockTransactions_ItemWarehouseBatch')
        DROP INDEX IX_StockTransactions_ItemWarehouseBatch ON StockTransactions;
END;
GO
-- لوحة المخازن ورسم الوارد/الصادر اليومي لكل المخازن
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_StockTransactions_Date')
    CREATE INDEX IX_StockTransactions_Date ON StockTransactions (TransactionDate) INCLUDE (WarehouseId, ItemId, QuantityBaseUnits, TransactionType);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_StockTransactions_Batch')
    CREATE INDEX IX_StockTransactions_Batch ON StockTransactions (BatchId) WHERE BatchId IS NOT NULL;
GO

-- ---------- المبيعات ----------
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SalesInvoices_StatusDate')
    CREATE INDEX IX_SalesInvoices_StatusDate ON SalesInvoices (Status, InvoiceDate)
        INCLUDE (TotalAmount, AmountPaidNow, IsFreeSale, CustomerId, SalesRepEmployeeId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SalesInvoices_Rep')
    CREATE INDEX IX_SalesInvoices_Rep ON SalesInvoices (SalesRepEmployeeId, InvoiceDate) INCLUDE (TotalAmount, Status)
        WHERE SalesRepEmployeeId IS NOT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SalesInvoiceLines_Item')
    CREATE INDEX IX_SalesInvoiceLines_Item ON SalesInvoiceLines (ItemId) INCLUDE (SalesInvoiceId, QuantityBaseUnits);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Customers_ParentAgent')
    CREATE INDEX IX_Customers_ParentAgent ON Customers (ParentAgentId) WHERE ParentAgentId IS NOT NULL;
GO

-- ---------- الصناديق ----------
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CashBoxTransactions_Date')
    CREATE INDEX IX_CashBoxTransactions_Date ON CashBoxTransactions (TxDate) INCLUDE (CashBoxId, Amount, TxType, IsVoided);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CashBoxTransactions_Reference')
    CREATE INDEX IX_CashBoxTransactions_Reference ON CashBoxTransactions (ReferenceTable, ReferenceId) WHERE ReferenceId IS NOT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CashBoxTransactions_TransferGroup')
    CREATE INDEX IX_CashBoxTransactions_TransferGroup ON CashBoxTransactions (TransferGroup) WHERE TransferGroup IS NOT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CashBoxes_Owner')
    CREATE INDEX IX_CashBoxes_Owner ON CashBoxes (OwnerUserId) INCLUDE (IsActive, IsDefault, BoxType) WHERE OwnerUserId IS NOT NULL;
GO

-- ---------- المالية ----------
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Vouchers_Party')
    CREATE INDEX IX_Vouchers_Party ON Vouchers (PartyType, PartyId) INCLUDE (VoucherType, Amount, VoucherDate);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Vouchers_Date')
    CREATE INDEX IX_Vouchers_Date ON Vouchers (VoucherDate);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_JournalEntries_Date')
    CREATE INDEX IX_JournalEntries_Date ON JournalEntries (EntryDate);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_JournalEntries_Source')
    CREATE INDEX IX_JournalEntries_Source ON JournalEntries (SourceTable, SourceId) WHERE SourceId IS NOT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_JournalEntryLines_Entry')
    CREATE INDEX IX_JournalEntryLines_Entry ON JournalEntryLines (JournalEntryId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_JournalEntryLines_Account')
    CREATE INDEX IX_JournalEntryLines_Account ON JournalEntryLines (AccountId) INCLUDE (Debit, Credit);
GO

-- ---------- مستندات المخازن ----------
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_StockDocumentLines_Document')
    CREATE INDEX IX_StockDocumentLines_Document ON StockDocumentLines (StockDocumentId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_StockDocuments_Counter')
    CREATE INDEX IX_StockDocuments_Counter ON StockDocuments (CounterWarehouseId, DocumentDate) WHERE CounterWarehouseId IS NOT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ItemPackagingLevels_Item')
    CREATE INDEX IX_ItemPackagingLevels_Item ON ItemPackagingLevels (ItemId);
GO

-- ---------- المشتريات ----------
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PurchaseOrderLines_Order')
    CREATE INDEX IX_PurchaseOrderLines_Order ON PurchaseOrderLines (PurchaseOrderId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PurchaseOrders_Status')
    CREATE INDEX IX_PurchaseOrders_Status ON PurchaseOrders (Status, OrderDate);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_GoodsReceipts_Date')
    CREATE INDEX IX_GoodsReceipts_Date ON GoodsReceipts (ReceiptDate) INCLUDE (SupplierId, PurchaseOrderId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_GoodsReceiptLines_Receipt')
    CREATE INDEX IX_GoodsReceiptLines_Receipt ON GoodsReceiptLines (GoodsReceiptId);
GO

-- ---------- الإنتاج والموارد البشرية ----------
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ProductionOrderConsumptions_Order')
    CREATE INDEX IX_ProductionOrderConsumptions_Order ON ProductionOrderConsumptions (ProductionOrderId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PackingOrders_Order')
    CREATE INDEX IX_PackingOrders_Order ON PackingOrders (ProductionOrderId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PackingOrders_Date')
    CREATE INDEX IX_PackingOrders_Date ON PackingOrders (PackingDate) INCLUDE (UnitsPackaged, PackagingLevelId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_QCBatchResults_Order')
    CREATE INDEX IX_QCBatchResults_Order ON QCBatchResults (ProductionOrderId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_QCTestResultLines_Result')
    CREATE INDEX IX_QCTestResultLines_Result ON QCTestResultLines (QCBatchResultId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AttendanceRecords_Date')
    CREATE INDEX IX_AttendanceRecords_Date ON AttendanceRecords (AttendanceDate) INCLUDE (EmployeeId, Status);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PayrollLines_Run')
    CREATE INDEX IX_PayrollLines_Run ON PayrollLines (PayrollRunId);
GO
