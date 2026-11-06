/* ============================================================
   مستندات المندوبين: إسناد حمولة من مخزن المنتج التام إلى الكاش فان، والإرجاع من المندوب
   (السليم يعود للمخزن، والتالف الميداني يُسجَّل بسببه ولا يعود رصيدًا سليمًا) — قابل لإعادة التنفيذ
   ============================================================ */
SET QUOTED_IDENTIFIER ON;
GO

-- سبب تلف جديد: تلف ميداني (بنفس آلية رموز الأسباب الموجودة) — في الحركات والمستندات
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_StockTransactions_DamageReason')
BEGIN
    DECLARE @old SYSNAME = (SELECT TOP 1 cc.name FROM sys.check_constraints cc
                            WHERE cc.parent_object_id = OBJECT_ID('StockTransactions') AND cc.definition LIKE '%Transit%');
    IF @old IS NOT NULL EXEC (N'ALTER TABLE StockTransactions DROP CONSTRAINT [' + @old + N']');
    ALTER TABLE StockTransactions ADD CONSTRAINT CK_StockTransactions_DamageReason
        CHECK (DamageReason IN (N'Transit', N'Warehouse', N'Production', N'Field'));
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_StockDocuments_DamageReason')
BEGIN
    DECLARE @old SYSNAME = (SELECT TOP 1 cc.name FROM sys.check_constraints cc
                            WHERE cc.parent_object_id = OBJECT_ID('StockDocuments') AND cc.definition LIKE '%Transit%');
    IF @old IS NOT NULL EXEC (N'ALTER TABLE StockDocuments DROP CONSTRAINT [' + @old + N']');
    ALTER TABLE StockDocuments ADD CONSTRAINT CK_StockDocuments_DamageReason
        CHECK (DamageReason IN (N'Transit', N'Warehouse', N'Production', N'Field'));
END;
GO

-- نوعا مستند جديدان: RepLoad (إسناد حمولة) و RepReturn (إرجاع من مندوب)
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_StockDocuments_Type')
BEGIN
    DECLARE @old SYSNAME = (SELECT TOP 1 cc.name FROM sys.check_constraints cc
                            WHERE cc.parent_object_id = OBJECT_ID('StockDocuments') AND cc.definition LIKE '%FreeIssue%');
    IF @old IS NOT NULL EXEC (N'ALTER TABLE StockDocuments DROP CONSTRAINT [' + @old + N']');
    ALTER TABLE StockDocuments ADD CONSTRAINT CK_StockDocuments_Type
        CHECK (DocumentType IN (N'Receipt', N'Issue', N'Transfer', N'Damaged', N'FreeIssue', N'RepLoad', N'RepReturn'));
END;
GO

-- المندوب صاحب المستند (للبحث والطباعة)
IF COL_LENGTH('StockDocuments', 'RepEmployeeId') IS NULL
    ALTER TABLE StockDocuments ADD RepEmployeeId INT NULL CONSTRAINT FK_StockDocuments_Rep FOREIGN KEY REFERENCES Employees(Id);
GO

-- سطر الإرجاع: سليم أم تالف ميدانيًا
IF COL_LENGTH('StockDocumentLines', 'IsDamaged') IS NULL
    ALTER TABLE StockDocumentLines ADD IsDamaged BIT NOT NULL CONSTRAINT DF_StockDocumentLines_IsDamaged DEFAULT 0;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_StockDocuments_Rep')
    CREATE INDEX IX_StockDocuments_Rep ON StockDocuments (RepEmployeeId, DocumentDate) WHERE RepEmployeeId IS NOT NULL;
GO
