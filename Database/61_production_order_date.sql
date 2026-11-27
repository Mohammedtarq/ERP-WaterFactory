/* ============================================================
   تاريخ إنشاء أمر الإنتاج: شاشة أوامر الإنتاج تعرض إنتاج اليوم افتراضيًا، ولأي يوم آخر «من / إلى» ثم «عرض».
   الأوامر السابقة تأخذ تاريخ أول حركة مخزنية لها، وإلا تاريخ تشغيلة ناتجها، وإلا تاريخ الترقية.
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF COL_LENGTH('ProductionOrders', 'CreatedAt') IS NULL
BEGIN
    ALTER TABLE ProductionOrders ADD CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_ProductionOrders_CreatedAt DEFAULT SYSUTCDATETIME();
    EXEC(N'
    UPDATE o SET CreatedAt = COALESCE(
        (SELECT MIN(t.TransactionDate) FROM StockTransactions t WHERE t.ReferenceTable = N''ProductionOrders'' AND t.ReferenceId = o.Id),
        (SELECT b.ManufactureDate FROM ItemBatches b WHERE b.Id = o.OutputBatchId),
        o.CreatedAt)
    FROM ProductionOrders o;');
END
GO
