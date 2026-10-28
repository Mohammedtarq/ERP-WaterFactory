/* ============================================================
   أمر إنتاج متعدد الأصناف: سطور الأمر (صنف، وصفة، كمية، دفعة خاصة بالسطر)
   - المواد المشتركة تُجمَّع للعرض وفحص التوفر، والمكونات الخاصة بكل صنف تبقى منفصلة
   - الاستهلاك والتعبئة والفحص لكل سطر عبر دفعته — قابل لإعادة التنفيذ
   - الأوامر القديمة تُحوَّل لسطر واحد من بيانات رأس الأمر
   ============================================================ */
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('ProductionOrderLines', 'U') IS NULL
CREATE TABLE ProductionOrderLines (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    ProductionOrderId   INT             NOT NULL FOREIGN KEY REFERENCES ProductionOrders(Id),
    [LineNo]              INT             NOT NULL,
    FinishedItemId      INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),
    BOMId               INT             NOT NULL FOREIGN KEY REFERENCES BillOfMaterials(Id),
    CustomRecipeId      INT             NULL FOREIGN KEY REFERENCES CustomRecipes(Id),
    QuantityToProduce   DECIMAL(18,3)   NOT NULL CHECK (QuantityToProduce > 0),
    OutputBatchId       INT             NULL FOREIGN KEY REFERENCES ItemBatches(Id),
    CONSTRAINT UQ_ProductionOrderLines_Item UNIQUE (ProductionOrderId, FinishedItemId)
);
GO

IF COL_LENGTH('ProductionOrderConsumptions', 'ProductionOrderLineId') IS NULL
    ALTER TABLE ProductionOrderConsumptions ADD ProductionOrderLineId INT NULL
        CONSTRAINT FK_Consumptions_Line FOREIGN KEY REFERENCES ProductionOrderLines(Id);
GO

IF COL_LENGTH('PackingOrders', 'ProductionOrderLineId') IS NULL
    ALTER TABLE PackingOrders ADD ProductionOrderLineId INT NULL
        CONSTRAINT FK_PackingOrders_Line FOREIGN KEY REFERENCES ProductionOrderLines(Id);
GO

-- تحويل الأوامر القديمة: سطر واحد من رأس الأمر، وربط استهلاكها وتعبئتها به
INSERT INTO ProductionOrderLines (ProductionOrderId, [LineNo], FinishedItemId, BOMId, CustomRecipeId, QuantityToProduce, OutputBatchId)
SELECT o.Id, 1, o.FinishedItemId, o.BOMId, o.CustomRecipeId, o.QuantityToProduce, o.OutputBatchId
FROM ProductionOrders o
WHERE NOT EXISTS (SELECT 1 FROM ProductionOrderLines l WHERE l.ProductionOrderId = o.Id);
GO

UPDATE c SET ProductionOrderLineId = l.Id
FROM ProductionOrderConsumptions c
JOIN ProductionOrderLines l ON l.ProductionOrderId = c.ProductionOrderId AND l.[LineNo] = 1
WHERE c.ProductionOrderLineId IS NULL
  AND (SELECT COUNT(*) FROM ProductionOrderLines x WHERE x.ProductionOrderId = c.ProductionOrderId) = 1;
GO

UPDATE p SET ProductionOrderLineId = l.Id
FROM PackingOrders p
JOIN ProductionOrderLines l ON l.ProductionOrderId = p.ProductionOrderId AND l.[LineNo] = 1
WHERE p.ProductionOrderLineId IS NULL
  AND (SELECT COUNT(*) FROM ProductionOrderLines x WHERE x.ProductionOrderId = p.ProductionOrderId) = 1;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ProductionOrderLines_Batch')
    CREATE INDEX IX_ProductionOrderLines_Batch ON ProductionOrderLines (OutputBatchId) WHERE OutputBatchId IS NOT NULL;
GO
