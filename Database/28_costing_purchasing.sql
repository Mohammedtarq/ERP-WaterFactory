/* ============================================================
   المرحلة م1: محرك الكلفة (المتوسط المرجّح) وفاتورة الشراء بوحدات الشراء
   - كل حركة مخزنية تحمل كلفة القطعة لحظة حدوثها (UnitCost)
   - الوارد المسعَّر (استلام شراء، ناتج إنتاج) يعيد حساب متوسط كلفة الصنف:
       المتوسط الجديد = (الرصيد × المتوسط الحالي + قيمة الوارد) ÷ (الرصيد + الوارد)
   - أي حركة بلا كلفة (صرف، مناقلة، بيع، تلف...) تأخذ المتوسط الساري
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */

IF COL_LENGTH('StockTransactions', 'UnitCost') IS NULL
    ALTER TABLE StockTransactions ADD UnitCost DECIMAL(18,4) NULL;
IF COL_LENGTH('Items', 'UnitWeightGrams') IS NULL
    ALTER TABLE Items ADD UnitWeightGrams DECIMAL(18,3) NULL;       -- وزن القطعة: يحوّل الشراء بالكغم/الطن إلى قطع
IF COL_LENGTH('Items', 'LeadTimeDays') IS NULL
    ALTER TABLE Items ADD LeadTimeDays INT NULL;                    -- مدة التجهيز من المورد (لنقطة إعادة الطلب)
IF COL_LENGTH('GoodsReceiptLines', 'PurchaseUnit') IS NULL
    ALTER TABLE GoodsReceiptLines ADD PurchaseUnit NVARCHAR(50) NULL,
                                      PurchaseQuantity DECIMAL(18,3) NULL,
                                      PurchaseUnitPrice DECIMAL(18,4) NULL;
IF COL_LENGTH('GoodsReceiptLines', 'UnitCost') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('GoodsReceiptLines') AND name = 'UnitCost' AND scale < 4)
    ALTER TABLE GoodsReceiptLines ALTER COLUMN UnitCost DECIMAL(18,4) NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_StockTransactions_Item')
    CREATE INDEX IX_StockTransactions_Item ON StockTransactions (ItemId) INCLUDE (QuantityBaseUnits, UnitCost, TransactionType);
GO

CREATE OR ALTER TRIGGER trg_StockTransactions_Cost ON StockTransactions AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;

    -- 1) الوارد المسعَّر يعيد حساب المتوسط المرجّح للصنف (على مستوى الشركة كلها، كل المخازن)
    ;WITH ins AS (
        SELECT ItemId,
               SUM(QuantityBaseUnits) AS AllQty,
               SUM(CASE WHEN QuantityBaseUnits > 0 AND UnitCost IS NOT NULL AND TransactionType IN (N'Receipt', N'ProductionOutput')
                        THEN QuantityBaseUnits END) AS InQty,
               SUM(CASE WHEN QuantityBaseUnits > 0 AND UnitCost IS NOT NULL AND TransactionType IN (N'Receipt', N'ProductionOutput')
                        THEN QuantityBaseUnits * UnitCost END) AS InValue
        FROM inserted GROUP BY ItemId),
    calc AS (
        SELECT ins.ItemId, ins.InQty, ins.InValue,
               (SELECT ISNULL(SUM(s.QuantityBaseUnits), 0) FROM StockTransactions s WHERE s.ItemId = ins.ItemId) - ins.AllQty AS Before
        FROM ins WHERE ins.InQty > 0)
    UPDATE i
    SET CostPrice = ROUND(CASE WHEN c.Before > 0 AND i.CostPrice IS NOT NULL
                               THEN (c.Before * i.CostPrice + c.InValue) / (c.Before + c.InQty)
                               ELSE c.InValue / c.InQty END, 4)
    FROM Items i JOIN calc c ON c.ItemId = i.Id;

    -- 2) كل حركة بلا كلفة تأخذ المتوسط الساري لحظتها
    UPDATE st SET UnitCost = i.CostPrice
    FROM StockTransactions st
    JOIN inserted x ON x.Id = st.Id
    JOIN Items i ON i.Id = st.ItemId
    WHERE st.UnitCost IS NULL AND i.CostPrice IS NOT NULL;
END;
GO

-- الحركات القديمة: تأخذ كلفة الصنف الحالية مرة واحدة (نقطة بداية)
UPDATE st SET UnitCost = i.CostPrice
FROM StockTransactions st JOIN Items i ON i.Id = st.ItemId
WHERE st.UnitCost IS NULL AND i.CostPrice IS NOT NULL;
GO

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PurchaseOrderLines') AND name = 'ExpectedUnitCost' AND scale < 4)
    ALTER TABLE PurchaseOrderLines ALTER COLUMN ExpectedUnitCost DECIMAL(18,4) NOT NULL;
GO
