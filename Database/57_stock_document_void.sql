/* ============================================================
   إلغاء المستند المخزني (docs/agreed-pricing-and-names.md قسم «ز»)
   - لا حذف: يبقى المستند ظاهرًا بحالة «ملغى» مع السبب ومن ألغاه ومتى.
   - أثره يُعكس بحركات بنفس النوع والكلفة وكمية معاكسة (IsReversal = 1)، فالتقارير الموقّعة تصفو تلقائيًا.
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF COL_LENGTH('StockDocuments', 'IsVoided') IS NULL
    ALTER TABLE StockDocuments ADD IsVoided BIT NOT NULL CONSTRAINT DF_StockDocuments_IsVoided DEFAULT 0;
GO
IF COL_LENGTH('StockDocuments', 'VoidReason') IS NULL
    ALTER TABLE StockDocuments ADD VoidReason NVARCHAR(300) NULL;
GO
IF COL_LENGTH('StockDocuments', 'VoidedByUserId') IS NULL
    ALTER TABLE StockDocuments ADD VoidedByUserId INT NULL CONSTRAINT FK_StockDocuments_VoidedBy REFERENCES Users(Id);
GO
IF COL_LENGTH('StockDocuments', 'VoidedAt') IS NULL
    ALTER TABLE StockDocuments ADD VoidedAt DATETIME2 NULL;
GO
IF COL_LENGTH('StockTransactions', 'IsReversal') IS NULL
    ALTER TABLE StockTransactions ADD IsReversal BIT NOT NULL CONSTRAINT DF_StockTransactions_IsReversal DEFAULT 0;
GO
