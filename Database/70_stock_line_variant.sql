/* ============================================================
   اسم المنتج الخاص (المتغير) في سطر المستند المخزني: كان يُستنتج من التشغيلة المحددة فقط، فيظهر الاسم العام عند التحميل «تلقائي».
   يُحفظ الآن في السطر نفسه، ويُملأ للمستندات القديمة حين يكون المتغير واحدًا واضحًا من حركاتها. قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF COL_LENGTH('StockDocumentLines', 'CustomRecipeId') IS NULL
    ALTER TABLE StockDocumentLines ADD CustomRecipeId INT NULL CONSTRAINT FK_StockDocumentLines_CustomRecipe REFERENCES CustomRecipes(Id);
GO
-- المستندات القديمة: المتغير الوحيد الذي مرّت به حركات الصنف في المستند (وليس بينها حركة بلا متغير)
UPDATE l SET CustomRecipeId = x.rid
FROM StockDocumentLines l
CROSS APPLY (SELECT MIN(b.CustomRecipeId) AS rid, COUNT(DISTINCT b.CustomRecipeId) AS c,
                    SUM(CASE WHEN b.CustomRecipeId IS NULL THEN 1 ELSE 0 END) AS n
             FROM StockTransactions t JOIN ItemBatches b ON b.Id = t.BatchId
             WHERE t.ReferenceTable = 'StockDocuments' AND t.ReferenceId = l.StockDocumentId AND t.ItemId = l.ItemId) x
WHERE l.CustomRecipeId IS NULL AND x.c = 1 AND x.n = 0;
GO
