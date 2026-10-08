/* ============================================================
   مزامنة جدول الأصناف مع كود التطبيق (Item.cs)
   حقلان أُضيفا في شاشة الأصناف: الباركود وحد التنبيه الأدنى.
   آمن للتنفيذ أكثر من مرة (لا يضيف العمود إن كان موجودًا).
   ============================================================ */
SET QUOTED_IDENTIFIER ON;
GO
IF COL_LENGTH('Items', 'BarCode') IS NULL
    ALTER TABLE Items ADD BarCode NVARCHAR(50) NULL;
GO
IF COL_LENGTH('Items', 'MinStockAlertLevel') IS NULL
    ALTER TABLE Items ADD MinStockAlertLevel DECIMAL(18,3) NULL;   -- بالقطعة
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Items_BarCode')
    CREATE INDEX IX_Items_BarCode ON Items (BarCode) WHERE BarCode IS NOT NULL;
GO
