/* ============================================================
   التسعير بالعبوة والخصم (docs/agreed-pricing-and-names.md أقسام أ، ب، ج)
   - PackPrices: سعر كل عبوة كما يُكتب (كارتون 40×330 = 4,250) — غير متناسب مع القطعة ولا بأس.
     CustomRecipeId NULL = السعر العام للمنتج؛ غير NULL = سعر الطلب الخاص (المتغير) لتلك العبوة.
   - السعر العام موحّد لكل الزبائن: لا أسعار وكلاء في الفاتورة (AgentItemPrices تبقى محفوظة بلا استعمال).
   - سعر القائمة للسطر = سعر العبوة (للمتغير إن وُجد)، فالخصم = (القائمة − سعر البيع) × الكمية.
   - لا بيع بالقطعة: القطعة لا تظهر في قوائم البيع (الفاتورة والتطبيق)؛ تبقى هنا لتسوية باقي السيارة غير الكامل.
   - Customers.DiscountKind: وكيل / صاحب خصم تشجيعي — علامة لتقرير الخصم الشهري فقط.
   - الترقية: يُملأ سعر كل عبوة بقيمته الحالية (سعر القطعة × قطعها)؛ لا يتغير سعر أي فاتورة سابقة.
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF OBJECT_ID('PackPrices', 'U') IS NULL
CREATE TABLE PackPrices (
    Id                  INT             NOT NULL IDENTITY PRIMARY KEY,
    ItemId              INT             NOT NULL REFERENCES Items(Id),
    PackagingLevelId    INT             NOT NULL REFERENCES ItemPackagingLevels(Id),
    CustomRecipeId      INT             NULL REFERENCES CustomRecipes(Id),
    Price               DECIMAL(18,2)   NOT NULL CHECK (Price >= 0),
    UpdatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedByUserId     INT             NULL REFERENCES Users(Id)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_PackPrices_General')
    CREATE UNIQUE INDEX UX_PackPrices_General ON PackPrices(PackagingLevelId) WHERE CustomRecipeId IS NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_PackPrices_Variant')
    CREATE UNIQUE INDEX UX_PackPrices_Variant ON PackPrices(PackagingLevelId, CustomRecipeId) WHERE CustomRecipeId IS NOT NULL;
GO

-- الترقية: سعر كل عبوة بيع لمنتج مصنَّع = سعر القطعة الحالي × قطعها (عام، ولكل طلب خاص له سعر)
INSERT INTO PackPrices (ItemId, PackagingLevelId, CustomRecipeId, Price)
SELECT l.ItemId, l.Id, NULL, ROUND(i.SalePrice * l.EquivalentBaseUnits, 2)
FROM ItemPackagingLevels l JOIN Items i ON i.Id = l.ItemId
WHERE l.IsSellableUnit = 1 AND l.EquivalentBaseUnits > 1 AND i.SourcingMethod <> N'Purchased'
  AND NOT EXISTS (SELECT 1 FROM PackPrices p WHERE p.PackagingLevelId = l.Id AND p.CustomRecipeId IS NULL);
INSERT INTO PackPrices (ItemId, PackagingLevelId, CustomRecipeId, Price)
SELECT l.ItemId, l.Id, r.Id, ROUND(r.SalePrice * l.EquivalentBaseUnits, 2)
FROM CustomRecipes r JOIN ItemPackagingLevels l ON l.ItemId = r.FinishedItemId
WHERE r.SalePrice IS NOT NULL AND l.IsSellableUnit = 1 AND l.EquivalentBaseUnits > 1
  AND NOT EXISTS (SELECT 1 FROM PackPrices p WHERE p.PackagingLevelId = l.Id AND p.CustomRecipeId = r.Id);
GO

IF COL_LENGTH('Customers', 'DiscountKind') IS NULL
    ALTER TABLE Customers ADD DiscountKind NVARCHAR(20) NULL
        CONSTRAINT CK_Customers_DiscountKind CHECK (DiscountKind IN (N'Agent', N'Incentive'));
GO

/* سعر العبوة: الطلب الخاص لعبوته ← العام للعبوة ← (احتياط) سعر القطعة × قطعها. موحّد لكل الزبائن. */
CREATE OR ALTER FUNCTION fn_Sales_PackPrice (@ItemId INT, @PackagingLevelId INT, @RecipeId INT)
RETURNS DECIMAL(18,2)
AS
BEGIN
    DECLARE @price DECIMAL(18,2), @units DECIMAL(18,3);
    IF @RecipeId IS NOT NULL
        SELECT @price = Price FROM PackPrices WHERE PackagingLevelId = @PackagingLevelId AND CustomRecipeId = @RecipeId;
    IF @price IS NULL
        SELECT @price = Price FROM PackPrices WHERE PackagingLevelId = @PackagingLevelId AND CustomRecipeId IS NULL;
    IF @price IS NULL
    BEGIN
        SELECT @units = EquivalentBaseUnits FROM ItemPackagingLevels WHERE Id = @PackagingLevelId;
        SELECT @price = ROUND(COALESCE((SELECT SalePrice FROM CustomRecipes WHERE Id = @RecipeId), (SELECT SalePrice FROM Items WHERE Id = @ItemId)) * @units, 2);
    END;
    RETURN @price;
END;
GO

CREATE OR ALTER PROCEDURE sp_Sales_AddInvoiceLine
    @InvoiceId          INT,
    @ItemId             INT,
    @PackagingLevelId   INT,
    @QuantityInLevel    DECIMAL(18,3),
    @UnitPrice          DECIMAL(18,2)   = NULL,
    @BatchId            INT             = NULL,
    @UserId             INT,
    @NewLineId          INT             = NULL OUTPUT,
    @CustomRecipeId     INT             = NULL
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    IF dbo.fn_UserCan(@UserId, N'Sales', N'Edit') = 0 AND dbo.fn_UserCan(@UserId, N'Sales', N'Add') = 0
        THROW 51010, N'لا تملك صلاحية تعديل فواتير المبيعات.', 1;

    DECLARE @status NVARCHAR(20), @free BIT, @rawSale BIT;
    SELECT @status = i.Status, @free = i.IsFreeSale,
           @rawSale = CASE WHEN w.WarehouseType = N'RawMaterial' THEN 1 ELSE 0 END
    FROM SalesInvoices i JOIN Warehouses w ON w.Id = i.WarehouseId WHERE i.Id = @InvoiceId;

    IF @status IS NULL THROW 51011, N'الفاتورة غير موجودة.', 1;
    IF @status <> N'Draft' THROW 51012, N'لا يمكن تعديل فاتورة مرحّلة.', 1;
    IF @QuantityInLevel IS NULL OR @QuantityInLevel <= 0
        THROW 51013, N'الكمية يجب أن تكون أكبر من صفر.', 1;
    IF NOT EXISTS (SELECT 1 FROM Items WHERE Id = @ItemId AND IsActive = 1)
        THROW 51014, N'الصنف غير موجود أو غير فعّال.', 1;

    DECLARE @baseUnits DECIMAL(18,3);
    SELECT @baseUnits = EquivalentBaseUnits FROM ItemPackagingLevels
    WHERE Id = @PackagingLevelId AND ItemId = @ItemId AND (IsSellableUnit = 1 OR @rawSale = 1);

    IF @baseUnits IS NULL
        THROW 51015, N'وحدة البيع المختارة لا تخص هذا الصنف أو غير قابلة للبيع.', 1;

    IF @BatchId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM ItemBatches WHERE Id = @BatchId AND ItemId = @ItemId)
        THROW 51016, N'التشغيلة المختارة لا تخص هذا الصنف.', 1;

    IF @CustomRecipeId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM CustomRecipes WHERE Id = @CustomRecipeId AND FinishedItemId = @ItemId)
        THROW 51018, N'المتغير المختار لا يخص هذا الصنف.', 1;

    IF @UnitPrice IS NOT NULL AND @UnitPrice < 0
        THROW 51017, N'السعر لا يمكن أن يكون سالبًا.', 1;

    -- متغير التسعير: المحدد بالاسم، أو متغير التشغيلة المختارة
    DECLARE @priceRecipe INT = @CustomRecipeId;
    IF @priceRecipe IS NULL AND @BatchId IS NOT NULL
        SELECT @priceRecipe = CustomRecipeId FROM ItemBatches WHERE Id = @BatchId;

    -- سعر القائمة = سعر العبوة الموحّد (للمتغير إن وُجد)؛ السعر الأقل المكتوب يظهر خصمًا
    DECLARE @list DECIMAL(18,2) = dbo.fn_Sales_PackPrice(@ItemId, @PackagingLevelId, @priceRecipe);
    IF @free = 1
        SET @UnitPrice = 0;
    ELSE IF @UnitPrice IS NULL
        SET @UnitPrice = @list;

    INSERT INTO SalesInvoiceLines
        (SalesInvoiceId, ItemId, BatchId, PackagingLevelId, QuantityInLevel, QuantityBaseUnits, UnitPrice, LineTotal, ListUnitPrice, CustomRecipeId)
    VALUES
        (@InvoiceId, @ItemId, @BatchId, @PackagingLevelId, @QuantityInLevel,
         @QuantityInLevel * @baseUnits, @UnitPrice, ROUND(@QuantityInLevel * @UnitPrice, 2),
         CASE WHEN @free = 1 THEN 0 ELSE @list END,
         CASE WHEN @BatchId IS NULL THEN @CustomRecipeId END);

    SET @NewLineId = SCOPE_IDENTITY();
    SELECT @NewLineId AS LineId, @UnitPrice AS UnitPrice;
END;
GO
