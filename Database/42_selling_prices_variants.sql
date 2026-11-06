/* ============================================================
   أسعار البيع للطلبات الخاصة (ملاحظة التجربة 3 — قرار المدير: كل الأصناف حتى الطلبات الخاصة)
   - CustomRecipes.SalePrice: السعر العام للطلب الخاص بالقطعة (NULL = سعر المنتج الأساسي)
   - AgentItemPrices.CustomRecipeId: سعر وكيل لطلب خاص بعينه (NULL = سعره للمنتج الأساسي)
   - التسعير الهرمي للقطعة:
       طلب خاص: سعر الوكيل للطلب الخاص ← السعر العام للطلب الخاص ← (كالأساسي)
       الأساسي: سعر الوكيل للمنتج ← سعر بيع الصنف
   - الفاتورة (والتطبيق والتسوية عبرها) تأخذ متغير السطر: المحدد بالاسم أو متغير التشغيلة المختارة.
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF COL_LENGTH('CustomRecipes', 'SalePrice') IS NULL
    ALTER TABLE CustomRecipes ADD SalePrice DECIMAL(18,2) NULL CONSTRAINT CK_CustomRecipes_SalePrice CHECK (SalePrice >= 0);
GO
IF COL_LENGTH('AgentItemPrices', 'CustomRecipeId') IS NULL
    ALTER TABLE AgentItemPrices ADD CustomRecipeId INT NULL CONSTRAINT FK_AgentItemPrices_CustomRecipe REFERENCES CustomRecipes(Id);
GO
-- سعر واحد لكل (وكيل، صنف، طلب خاص)؛ NULL = الأساسي
IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_AgentItem')
    ALTER TABLE AgentItemPrices DROP CONSTRAINT UQ_AgentItem;
GO
IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_AgentItemRecipe')
    ALTER TABLE AgentItemPrices ADD CONSTRAINT UQ_AgentItemRecipe UNIQUE (CustomerId, ItemId, CustomRecipeId);
GO

/* سعر القطعة لعميل وصنف وطلب خاص (اختياري). @UseAgentPricing = 0 يُجبر الأسعار العامة. */
CREATE OR ALTER FUNCTION fn_Sales_UnitPrice (@CustomerId INT, @ItemId INT, @RecipeId INT, @UseAgentPricing BIT)
RETURNS DECIMAL(18,4)
AS
BEGIN
    DECLARE @price DECIMAL(18,4), @agentId INT;

    SELECT @agentId = CASE CustomerType WHEN N'Agent' THEN Id WHEN N'SubCustomer' THEN ParentAgentId END
    FROM   Customers WHERE Id = @CustomerId;
    IF @UseAgentPricing = 0 SET @agentId = NULL;

    IF @RecipeId IS NOT NULL
    BEGIN
        IF @agentId IS NOT NULL
            SELECT @price = AgentPrice FROM AgentItemPrices WHERE CustomerId = @agentId AND ItemId = @ItemId AND CustomRecipeId = @RecipeId;
        IF @price IS NULL
            SELECT @price = SalePrice FROM CustomRecipes WHERE Id = @RecipeId AND FinishedItemId = @ItemId;
    END;

    IF @price IS NULL AND @agentId IS NOT NULL
        SELECT @price = AgentPrice FROM AgentItemPrices WHERE CustomerId = @agentId AND ItemId = @ItemId AND CustomRecipeId IS NULL;

    IF @price IS NULL
        SELECT @price = SalePrice FROM Items WHERE Id = @ItemId;

    RETURN @price;
END;
GO

/* التوافق: سعر المنتج الأساسي (بلا طلب خاص) */
CREATE OR ALTER FUNCTION fn_Sales_BaseUnitPrice (@CustomerId INT, @ItemId INT, @UseAgentPricing BIT)
RETURNS DECIMAL(18,4)
AS
BEGIN
    RETURN dbo.fn_Sales_UnitPrice(@CustomerId, @ItemId, NULL, @UseAgentPricing);
END;
GO

/* ============================================================
   إضافة سطر لفاتورة مسودة — كما في 09_sales_logic.sql، والسعر حسب متغير السطر:
   المتغير المحدد بالاسم، أو متغير التشغيلة المختارة.
   ============================================================ */
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

    DECLARE @status NVARCHAR(20), @custId INT, @agentPricing BIT, @free BIT, @rawSale BIT;
    SELECT @status = i.Status, @custId = i.CustomerId, @agentPricing = i.IsAgentPricing, @free = i.IsFreeSale,
           @rawSale = CASE WHEN w.WarehouseType = N'RawMaterial' THEN 1 ELSE 0 END
    FROM SalesInvoices i JOIN Warehouses w ON w.Id = i.WarehouseId WHERE i.Id = @InvoiceId;

    IF @status IS NULL THROW 51011, N'الفاتورة غير موجودة.', 1;
    IF @status <> N'Draft' THROW 51012, N'لا يمكن تعديل فاتورة مرحّلة.', 1;
    IF @QuantityInLevel IS NULL OR @QuantityInLevel <= 0
        THROW 51013, N'الكمية يجب أن تكون أكبر من صفر.', 1;
    IF NOT EXISTS (SELECT 1 FROM Items WHERE Id = @ItemId AND IsActive = 1)
        THROW 51014, N'الصنف غير موجود أو غير فعّال.', 1;

    DECLARE @baseUnits DECIMAL(18,3);
    -- بيع المواد الأولية (من مخزنها، بصلاحية خاصة في الواجهة) يقبل أي وحدة للمادة
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

    IF @free = 1
        SET @UnitPrice = 0;
    ELSE IF @UnitPrice IS NULL
        SET @UnitPrice = ROUND(dbo.fn_Sales_UnitPrice(@custId, @ItemId, @priceRecipe, @agentPricing) * @baseUnits, 2);

    -- سعر القائمة: العام للطلب الخاص إن حُدّد، وإلا سعر الصنف
    DECLARE @listPiece DECIMAL(18,4) = COALESCE((SELECT SalePrice FROM CustomRecipes WHERE Id = @priceRecipe), (SELECT SalePrice FROM Items WHERE Id = @ItemId));

    INSERT INTO SalesInvoiceLines
        (SalesInvoiceId, ItemId, BatchId, PackagingLevelId, QuantityInLevel, QuantityBaseUnits, UnitPrice, LineTotal, ListUnitPrice, CustomRecipeId)
    VALUES
        (@InvoiceId, @ItemId, @BatchId, @PackagingLevelId, @QuantityInLevel,
         @QuantityInLevel * @baseUnits, @UnitPrice, ROUND(@QuantityInLevel * @UnitPrice, 2),
         CASE WHEN @free = 1 THEN 0 ELSE ROUND(@listPiece * @baseUnits, 2) END,
         CASE WHEN @BatchId IS NULL THEN @CustomRecipeId END);

    SET @NewLineId = SCOPE_IDENTITY();
    SELECT @NewLineId AS LineId, @UnitPrice AS UnitPrice;
END;
GO
