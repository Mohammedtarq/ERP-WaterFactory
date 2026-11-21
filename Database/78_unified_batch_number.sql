/* ============================================================
   رقم دفعة موحّد لأمر الإنتاج متعدد الأصناف والمتغيرات (312 لكل أسطر الأمر)
   - كان UQ_ItemBatch (ItemId, BatchNumber) يمنع رقمًا واحدًا لمتغيرين من الصنف نفسه (الرحمة ومطعم الحسون)
   - الآن الفريد: (الصنف، الرقم، المتغير). لكل سطر دفعته (ItemBatches) برقم الأمر نفسه، ويبقى التتبع بالصنف والمتغير
   - تفرّد رقم دفعات الإنتاج بين الأوامر تضبطه الخدمة (ProductionService.ValidateBatchNumberAsync)
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */

IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_ItemBatch' AND parent_object_id = OBJECT_ID('ItemBatches'))
    ALTER TABLE ItemBatches DROP CONSTRAINT UQ_ItemBatch;
GO
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ItemBatches_ItemId_BatchNumber' AND object_id = OBJECT_ID('ItemBatches'))
    DROP INDEX IX_ItemBatches_ItemId_BatchNumber ON ItemBatches;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_ItemBatches_Item_Number_Recipe' AND object_id = OBJECT_ID('ItemBatches'))
    CREATE UNIQUE INDEX UX_ItemBatches_Item_Number_Recipe ON ItemBatches (ItemId, BatchNumber, CustomRecipeId);
GO

-- رقم دفعة الإنتاج كان فريدًا وحده على مستوى النظام؛ صار فريدًا مع الصنف والمتغير (الأمر الواحد يشاركه أصنافه ومتغيراته)
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_ItemBatches_ProductionNumber' AND object_id = OBJECT_ID('ItemBatches'))
    DROP INDEX UX_ItemBatches_ProductionNumber ON ItemBatches;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_ItemBatches_ProductionNumber_Variant' AND object_id = OBJECT_ID('ItemBatches'))
    CREATE UNIQUE INDEX UX_ItemBatches_ProductionNumber_Variant ON ItemBatches (BatchNumber, ItemId, CustomRecipeId) WHERE ProductionOrderId IS NOT NULL;
GO
