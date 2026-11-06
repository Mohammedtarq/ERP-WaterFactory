/* ============================================================
   حدود تنبيه الأسماء الخاصة (المرحلة الأولى — docs/agreed-pricing-and-names.md قسم «ح»)
   - لكل (منتج، متغير) حدّ يدوي بالعبوة (كرتون/شرنك)؛ المتغير = طلب خاص (اسم زبون/مناسبة) أو الأساسي (NULL).
   - الاسم بلا حدّ لا يُتابَع ولا ينبّه. يُقارن الحد برصيد المنتج التام لذلك المتغير، وينبّه الجرس عند نزوله عنه.
   - المواد الأولية خارج هذا الجدول (حدودها في WarehouseItemAlerts بوحداتها).
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF OBJECT_ID('VariantAlerts', 'U') IS NULL
CREATE TABLE VariantAlerts (
    Id                  INT             NOT NULL IDENTITY PRIMARY KEY,
    ItemId              INT             NOT NULL REFERENCES Items(Id),
    CustomRecipeId      INT             NULL REFERENCES CustomRecipes(Id),
    MinPacks            DECIMAL(18,3)   NOT NULL CHECK (MinPacks >= 0),     -- بعبوة المنتج الكبرى
    UpdatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedByUserId     INT             NULL REFERENCES Users(Id)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_VariantAlerts_Base')
    CREATE UNIQUE INDEX UX_VariantAlerts_Base ON VariantAlerts(ItemId) WHERE CustomRecipeId IS NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_VariantAlerts_Recipe')
    CREATE UNIQUE INDEX UX_VariantAlerts_Recipe ON VariantAlerts(ItemId, CustomRecipeId) WHERE CustomRecipeId IS NOT NULL;
GO
