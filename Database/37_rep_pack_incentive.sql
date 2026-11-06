/* ============================================================
   حافز المندوب بالعبوة (شرنك، كارتون) بدل القطعة
   - سعر الحافز لكل (صنف × وحدة تعبئة): الإدارة تحدد مبلغ الشرنك ومبلغ الكارتون
   - الحافز الشهري = (المحمّل − الراجع − المجاني) بكل وحدة × سعرها، ويُصرف مع الراتب
   - المجاني يحفظ وحدته (لطرحه من العبوة الصحيحة)
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */

IF COL_LENGTH('RepItemIncentiveRates', 'PackagingLevelId') IS NULL
    ALTER TABLE RepItemIncentiveRates ADD PackagingLevelId INT NULL
        CONSTRAINT FK_RepItemIncentiveRates_Level REFERENCES ItemPackagingLevels(Id);
GO

-- قيد "صنف واحد = سعر واحد" (بلا اسم في 06) يُستبدل بقيد على (الصنف، الوحدة)
DECLARE @uq SYSNAME = (SELECT TOP 1 kc.name FROM sys.key_constraints kc
                       JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
                       JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                       WHERE kc.parent_object_id = OBJECT_ID('RepItemIncentiveRates') AND kc.type = 'UQ' AND c.name = 'ItemId'
                         AND (SELECT COUNT(*) FROM sys.index_columns x WHERE x.object_id = kc.parent_object_id AND x.index_id = kc.unique_index_id) = 1);
IF @uq IS NOT NULL EXEC (N'ALTER TABLE RepItemIncentiveRates DROP CONSTRAINT [' + @uq + N']');
GO

-- السعر القديم كان للقطعة: يصبح لكل عبوة (شرنك، كارتون) = سعر القطعة × عدد قطعها، فلا تتغير قيمته
-- (في معاملة: أي خطأ يلغي التحويل كله فلا يُحذف سعر قبل نقله)
SET XACT_ABORT ON;
BEGIN TRANSACTION;
INSERT INTO RepItemIncentiveRates (ItemId, PackagingLevelId, IncentiveRatePerUnit)
SELECT r.ItemId, l.Id, r.IncentiveRatePerUnit * l.EquivalentBaseUnits
FROM RepItemIncentiveRates r JOIN ItemPackagingLevels l ON l.ItemId = r.ItemId AND l.EquivalentBaseUnits > 1
WHERE r.PackagingLevelId IS NULL;
-- صنف بلا عبوة: يبقى سعره على القطعة
UPDATE r SET r.PackagingLevelId = l.Id
FROM RepItemIncentiveRates r
CROSS APPLY (SELECT TOP 1 Id FROM ItemPackagingLevels WHERE ItemId = r.ItemId ORDER BY EquivalentBaseUnits, Id) l
WHERE r.PackagingLevelId IS NULL
  AND NOT EXISTS (SELECT 1 FROM ItemPackagingLevels x WHERE x.ItemId = r.ItemId AND x.EquivalentBaseUnits > 1);
DELETE FROM RepItemIncentiveRates WHERE PackagingLevelId IS NULL;
COMMIT TRANSACTION;
GO

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('RepItemIncentiveRates') AND name = 'PackagingLevelId' AND is_nullable = 1)
    ALTER TABLE RepItemIncentiveRates ALTER COLUMN PackagingLevelId INT NOT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_RepItemIncentiveRates_Level')
    CREATE UNIQUE INDEX UX_RepItemIncentiveRates_Level ON RepItemIncentiveRates (ItemId, PackagingLevelId);
GO

IF COL_LENGTH('RepFreeGoods', 'PackagingLevelId') IS NULL
    ALTER TABLE RepFreeGoods ADD PackagingLevelId INT NULL CONSTRAINT FK_RepFreeGoods_Level REFERENCES ItemPackagingLevels(Id),
                                 QuantityInLevel DECIMAL(18,3) NULL;
GO
