/* ============================================================
   اسم المنتج الموحد (قرار المالك): «{العبوة} {عدد القطع}×{الحجم} {المتغير}»
   مثل: كارتون 40×330 مطعم داون تاون — كارتون 40×330 الرحمة — شرنك 20×330 زواج سعيد
   - Items.SizeMl: حجم القطعة بالمليلتر (فارغ = يُقرأ من اسم الصنف إن كان فيه، مثل «ماء 330 مل»)
   - CustomRecipes.ShortName: اسم مختصر للمتغير في الاسم الموحد (فارغ = اسم الزبون، أو اسم الطلب الخاص)
   - CompanyProfile.BaseVariantName: اسم المنتج الأساسي (بلا طلب خاص)، مثل «الرحمة»
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF COL_LENGTH('Items', 'SizeMl') IS NULL
    ALTER TABLE Items ADD SizeMl DECIMAL(9,2) NULL CONSTRAINT CK_Items_SizeMl CHECK (SizeMl > 0);
IF COL_LENGTH('CustomRecipes', 'ShortName') IS NULL
    ALTER TABLE CustomRecipes ADD ShortName NVARCHAR(60) NULL;
IF COL_LENGTH('CompanyProfile', 'BaseVariantName') IS NULL
    ALTER TABLE CompanyProfile ADD BaseVariantName NVARCHAR(60) NULL;
GO
-- معمل الرحمة: المنتج الأساسي يُسمّى «الرحمة» (قرار المالك)، ويُغيَّر من «هوية الطباعة»
UPDATE CompanyProfile SET BaseVariantName = N'الرحمة'
WHERE BaseVariantName IS NULL AND NameAr LIKE N'%الرحمة%';
GO
