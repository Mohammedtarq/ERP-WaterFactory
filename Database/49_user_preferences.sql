/* ============================================================
   تفضيلات المستخدم في الواجهة الجديدة
   - ThemeKey: مظهره (الرحمة، أزرق الماء، فيروزي، رصاصي، زجاجي، زيتوني) — فارغ = «الرحمة»
   - Favorites: أقسامه المفضلة («مفضلتي» في الرئيسية) مفاتيح مفصولة بفواصل
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF COL_LENGTH('Users', 'ThemeKey') IS NULL
    ALTER TABLE Users ADD ThemeKey NVARCHAR(30) NULL;
IF COL_LENGTH('Users', 'Favorites') IS NULL
    ALTER TABLE Users ADD Favorites NVARCHAR(2000) NULL;
GO
