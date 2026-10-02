/* ============================================================
   منطق وحدة المبيعات (يُنفَّذ بعد الملفات 01 → 08)
   - التسعير الهرمي: وكيل / عميل فرعي / عميل مباشر
   - مستلزمات التحميل، الضريبة، المبيعات المجانية
   - اختيار التشغيلة تلقائيًا (FIFO حسب تاريخ الصلاحية)
   - الترحيل: خصم المخزون + القيد المحاسبي التلقائي (العقل المالي)
     + محفظة المندوب — كلها في معاملة واحدة (إما تنجح كلها أو لا شيء)

   قواعد الربط المطلوبة في AccountMappingRules (TransactionType):
     SalesInvoiceCash        مدين: الصندوق            دائن: إيرادات المبيعات
     SalesInvoiceCredit      مدين: ذمم العملاء         دائن: إيرادات المبيعات
     SalesInvoiceElectronic  مدين: البنك/الدفع الإلكتروني دائن: إيرادات المبيعات
     SalesInvoiceRepCash     مدين: عهدة المندوبين      دائن: إيرادات المبيعات
     SalesTax                (يُستخدم الحساب الدائن فقط: ضريبة مستحقة)
     LoadingSuppliesCharge   (يُستخدم الحساب الدائن فقط: إيراد مستلزمات التحميل)
   ============================================================ */

-- هذا الملف قابل لإعادة التنفيذ بأمان (كل إضافة محمية بشرط، والإجراءات CREATE OR ALTER)
-- حتى يرقّي البرنامج أي قاعدة قائمة تلقائيًا إلى آخر نسخة.

-- ============ أعمدة إضافية على رأس الفاتورة (مجاميع + تتبّع الترحيل) ============
IF COL_LENGTH('SalesInvoices', 'SubTotal') IS NULL
    ALTER TABLE SalesInvoices ADD SubTotal DECIMAL(18,2) NOT NULL CONSTRAINT DF_SalesInvoices_SubTotal DEFAULT 0;
IF COL_LENGTH('SalesInvoices', 'TaxAmount') IS NULL
    ALTER TABLE SalesInvoices ADD TaxAmount DECIMAL(18,2) NOT NULL CONSTRAINT DF_SalesInvoices_TaxAmount DEFAULT 0;
IF COL_LENGTH('SalesInvoices', 'TotalAmount') IS NULL
    ALTER TABLE SalesInvoices ADD TotalAmount DECIMAL(18,2) NOT NULL CONSTRAINT DF_SalesInvoices_TotalAmount DEFAULT 0;
IF COL_LENGTH('SalesInvoices', 'Notes') IS NULL
    ALTER TABLE SalesInvoices ADD Notes NVARCHAR(400) NULL;
IF COL_LENGTH('SalesInvoices', 'PostedByUserId') IS NULL
    ALTER TABLE SalesInvoices ADD PostedByUserId INT NULL CONSTRAINT FK_SalesInvoices_PostedBy FOREIGN KEY REFERENCES Users(Id);
IF COL_LENGTH('SalesInvoices', 'PostedAt') IS NULL
    ALTER TABLE SalesInvoices ADD PostedAt DATETIME2 NULL;
GO

IF OBJECT_ID('CK_SalesInvoices_FreeSaleRecipient', 'C') IS NULL
    ALTER TABLE SalesInvoices ADD CONSTRAINT CK_SalesInvoices_FreeSaleRecipient
        CHECK (IsFreeSale = 0 OR (FreeSaleRecipient IS NOT NULL AND LEN(LTRIM(FreeSaleRecipient)) > 0));
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SalesInvoices_Customer')
    CREATE INDEX IX_SalesInvoices_Customer ON SalesInvoices (CustomerId, Status, InvoiceDate);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SalesInvoiceLines_Invoice')
    CREATE INDEX IX_SalesInvoiceLines_Invoice ON SalesInvoiceLines (SalesInvoiceId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AgentItemPrices_Item')
    CREATE INDEX IX_AgentItemPrices_Item ON AgentItemPrices (ItemId, CustomerId);
GO

-- ============ الترقيم التلقائي ============
IF OBJECT_ID('seq_SalesInvoiceNumber', 'SO') IS NULL
    CREATE SEQUENCE seq_SalesInvoiceNumber AS INT START WITH 1 INCREMENT BY 1;
IF OBJECT_ID('seq_SalesJournalNumber', 'SO') IS NULL
    CREATE SEQUENCE seq_SalesJournalNumber AS INT START WITH 1 INCREMENT BY 1;
GO

/* ============================================================
   الرصيد الحالي للمخزون لكل صنف/مخزن/تشغيلة (من سجل الحركة فقط)
   ============================================================ */
CREATE OR ALTER VIEW vw_StockBalance AS
SELECT  st.ItemId, st.WarehouseId, st.BatchId,
        SUM(st.QuantityBaseUnits) AS QuantityBaseUnits
FROM    StockTransactions st
GROUP BY st.ItemId, st.WarehouseId, st.BatchId;
GO

/* ============================================================
   صلاحية المستخدم على وحدة (View/Add/Edit/Delete/Post)
   ============================================================ */
CREATE OR ALTER FUNCTION fn_UserCan (@UserId INT, @ModuleCode NVARCHAR(50), @Action NVARCHAR(10))
RETURNS BIT
AS
BEGIN
    DECLARE @r BIT = 0;
    SELECT @r = CASE @Action
                    WHEN N'View'   THEN rp.CanView
                    WHEN N'Add'    THEN rp.CanAdd
                    WHEN N'Edit'   THEN rp.CanEdit
                    WHEN N'Delete' THEN rp.CanDelete
                    WHEN N'Post'   THEN rp.CanPost
                    ELSE 0 END
    FROM   Users u
    JOIN   RolePermissions rp ON rp.RoleId = u.RoleId AND rp.ModuleCode = @ModuleCode
    WHERE  u.Id = @UserId AND u.IsActive = 1;
    RETURN ISNULL(@r, 0);
END;
GO

/* ============================================================
   التسعير الهرمي — سعر القطعة الواحدة (الوحدة الأساسية)
   1) وكيل        ← سعره الخاص في AgentItemPrices
   2) عميل فرعي   ← سعر الوكيل الأب في AgentItemPrices
   3) غير ذلك (أو لا يوجد سعر خاص) ← Items.SalePrice
   @UseAgentPricing = 0 يُجبر السعر العادي
   ============================================================ */
CREATE OR ALTER FUNCTION fn_Sales_BaseUnitPrice (@CustomerId INT, @ItemId INT, @UseAgentPricing BIT)
RETURNS DECIMAL(18,4)
AS
BEGIN
    DECLARE @price DECIMAL(18,4), @agentId INT, @type NVARCHAR(20);

    SELECT @type = CustomerType,
           @agentId = CASE CustomerType WHEN N'Agent' THEN Id
                                        WHEN N'SubCustomer' THEN ParentAgentId END
    FROM   Customers WHERE Id = @CustomerId;

    IF @UseAgentPricing = 1 AND @agentId IS NOT NULL
        SELECT @price = AgentPrice FROM AgentItemPrices WHERE CustomerId = @agentId AND ItemId = @ItemId;

    IF @price IS NULL
        SELECT @price = SalePrice FROM Items WHERE Id = @ItemId;

    RETURN @price;
END;
GO

/* ============================================================
   سعر مستلزمات التحميل الساري في تاريخ معيّن
   ============================================================ */
CREATE OR ALTER FUNCTION fn_Sales_LoadingRate (@OnDate DATE)
RETURNS DECIMAL(18,2)
AS
BEGIN
    RETURN ISNULL((SELECT TOP 1 RatePerPiece FROM LoadingSuppliesSettings
                   WHERE EffectiveDate <= @OnDate
                   ORDER BY EffectiveDate DESC, Id DESC), 0);
END;
GO

/* ============================================================
   إنشاء فاتورة مبيعات (مسودة)
   ============================================================ */
CREATE OR ALTER PROCEDURE sp_Sales_CreateInvoice
    @CustomerId             INT,
    @WarehouseId            INT,
    @InvoiceDate            DATE,
    @PaymentMethod          NVARCHAR(20),
    @AmountPaidNow          DECIMAL(18,2)   = 0,     -- يُعتبر فقط عند Partial
    @TaxEnabled             BIT             = 0,
    @TaxRate                DECIMAL(5,2)    = 14,
    @LoadingSuppliesEnabled BIT             = 0,
    @IsAgentPricing         BIT             = NULL,  -- NULL = تلقائي حسب نوع العميل
    @IsFreeSale             BIT             = 0,
    @FreeSaleRecipient      NVARCHAR(200)   = NULL,
    @SalesRepEmployeeId     INT             = NULL,  -- NULL = مالك مخزن الكاش فان إن وُجد
    @Notes                  NVARCHAR(400)   = NULL,
    @UserId                 INT,
    @NewInvoiceId           INT             = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    IF dbo.fn_UserCan(@UserId, N'Sales', N'Add') = 0
        THROW 51000, N'لا تملك صلاحية إضافة فواتير مبيعات.', 1;

    DECLARE @custType NVARCHAR(20), @custActive BIT, @parentAgent INT;
    SELECT @custType = CustomerType, @custActive = IsActive, @parentAgent = ParentAgentId
    FROM Customers WHERE Id = @CustomerId;

    IF @custType IS NULL OR @custActive = 0
        THROW 51001, N'العميل غير موجود أو غير فعّال.', 1;
    IF @custType = N'SubCustomer' AND NOT EXISTS
        (SELECT 1 FROM Customers WHERE Id = @parentAgent AND CustomerType = N'Agent')
        THROW 51002, N'العميل الفرعي يجب أن يكون مرتبطًا بوكيل صحيح.', 1;

    DECLARE @whType NVARCHAR(30), @whSellable BIT, @whOwner INT;
    SELECT @whType = WarehouseType, @whSellable = IsSellableStock, @whOwner = OwnerEmployeeId
    FROM Warehouses WHERE Id = @WarehouseId AND IsActive = 1;

    IF @whType IS NULL
        THROW 51003, N'المخزن غير موجود أو غير فعّال.', 1;
    IF @whSellable = 0
        THROW 51004, N'لا يمكن البيع من هذا المخزن (مخزون غير قابل للبيع).', 1;

    IF @IsFreeSale = 1 AND LEN(LTRIM(ISNULL(@FreeSaleRecipient, N''))) = 0
        THROW 51005, N'المبيعات المجانية تتطلب تحديد الجهة المستفيدة.', 1;

    IF @PaymentMethod NOT IN (N'Cash', N'Credit', N'Partial', N'Electronic')
        THROW 51006, N'طريقة دفع غير صحيحة.', 1;

    IF @SalesRepEmployeeId IS NULL AND @whType = N'RepVan'
        SET @SalesRepEmployeeId = @whOwner;

    IF @SalesRepEmployeeId IS NOT NULL AND NOT EXISTS
        (SELECT 1 FROM Employees WHERE Id = @SalesRepEmployeeId AND IsSalesRep = 1 AND IsActive = 1)
        THROW 51007, N'الموظف المحدد ليس مندوب مبيعات فعّالًا.', 1;

    SET @IsAgentPricing = ISNULL(@IsAgentPricing,
                          CASE WHEN @custType IN (N'Agent', N'SubCustomer') THEN 1 ELSE 0 END);

    DECLARE @num NVARCHAR(30) = N'SI-' + CAST(YEAR(@InvoiceDate) AS NVARCHAR(4)) + N'-'
            + RIGHT(N'000000' + CAST(NEXT VALUE FOR seq_SalesInvoiceNumber AS NVARCHAR(10)), 6);

    INSERT INTO SalesInvoices
        (InvoiceNumber, CustomerId, WarehouseId, InvoiceDate, PaymentMethod, AmountPaidNow,
         TaxEnabled, TaxRate, LoadingSuppliesEnabled, IsAgentPricing, IsFreeSale,
         FreeSaleRecipient, SalesRepEmployeeId, Notes, CreatedByUserId)
    VALUES
        (@num, @CustomerId, @WarehouseId, @InvoiceDate, @PaymentMethod,
         CASE WHEN @PaymentMethod = N'Partial' THEN ISNULL(@AmountPaidNow, 0) ELSE 0 END,
         CASE WHEN @IsFreeSale = 1 THEN 0 ELSE @TaxEnabled END, @TaxRate,
         CASE WHEN @IsFreeSale = 1 THEN 0 ELSE @LoadingSuppliesEnabled END,
         @IsAgentPricing, @IsFreeSale,
         CASE WHEN @IsFreeSale = 1 THEN LTRIM(RTRIM(@FreeSaleRecipient)) END,
         @SalesRepEmployeeId, @Notes, @UserId);

    SET @NewInvoiceId = SCOPE_IDENTITY();
    SELECT @NewInvoiceId AS InvoiceId, @num AS InvoiceNumber;
END;
GO

/* ============================================================
   إضافة سطر إلى فاتورة مسودة
   - @UnitPrice = NULL ← يُحسب تلقائيًا بالتسعير الهرمي × عدد القطع في وحدة البيع
   - @BatchId   = NULL ← تُختار التشغيلات تلقائيًا (FIFO) وقت الترحيل
   ============================================================ */
CREATE OR ALTER PROCEDURE sp_Sales_AddInvoiceLine
    @InvoiceId          INT,
    @ItemId             INT,
    @PackagingLevelId   INT,
    @QuantityInLevel    DECIMAL(18,3),
    @UnitPrice          DECIMAL(18,2)   = NULL,
    @BatchId            INT             = NULL,
    @UserId             INT,
    @NewLineId          INT             = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    IF dbo.fn_UserCan(@UserId, N'Sales', N'Edit') = 0 AND dbo.fn_UserCan(@UserId, N'Sales', N'Add') = 0
        THROW 51010, N'لا تملك صلاحية تعديل فواتير المبيعات.', 1;

    DECLARE @status NVARCHAR(20), @custId INT, @agentPricing BIT, @free BIT;
    SELECT @status = Status, @custId = CustomerId, @agentPricing = IsAgentPricing, @free = IsFreeSale
    FROM SalesInvoices WHERE Id = @InvoiceId;

    IF @status IS NULL THROW 51011, N'الفاتورة غير موجودة.', 1;
    IF @status <> N'Draft' THROW 51012, N'لا يمكن تعديل فاتورة مرحّلة.', 1;
    IF @QuantityInLevel IS NULL OR @QuantityInLevel <= 0
        THROW 51013, N'الكمية يجب أن تكون أكبر من صفر.', 1;
    IF NOT EXISTS (SELECT 1 FROM Items WHERE Id = @ItemId AND IsActive = 1)
        THROW 51014, N'الصنف غير موجود أو غير فعّال.', 1;

    DECLARE @baseUnits DECIMAL(18,3);
    SELECT @baseUnits = EquivalentBaseUnits FROM ItemPackagingLevels
    WHERE Id = @PackagingLevelId AND ItemId = @ItemId AND IsSellableUnit = 1;

    IF @baseUnits IS NULL
        THROW 51015, N'وحدة البيع المختارة لا تخص هذا الصنف أو غير قابلة للبيع.', 1;

    IF @BatchId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM ItemBatches WHERE Id = @BatchId AND ItemId = @ItemId)
        THROW 51016, N'التشغيلة المختارة لا تخص هذا الصنف.', 1;

    IF @UnitPrice IS NOT NULL AND @UnitPrice < 0
        THROW 51017, N'السعر لا يمكن أن يكون سالبًا.', 1;

    IF @free = 1
        SET @UnitPrice = 0;
    ELSE IF @UnitPrice IS NULL
        SET @UnitPrice = ROUND(dbo.fn_Sales_BaseUnitPrice(@custId, @ItemId, @agentPricing) * @baseUnits, 2);

    INSERT INTO SalesInvoiceLines
        (SalesInvoiceId, ItemId, BatchId, PackagingLevelId, QuantityInLevel, QuantityBaseUnits, UnitPrice, LineTotal)
    VALUES
        (@InvoiceId, @ItemId, @BatchId, @PackagingLevelId, @QuantityInLevel,
         @QuantityInLevel * @baseUnits, @UnitPrice, ROUND(@QuantityInLevel * @UnitPrice, 2));

    SET @NewLineId = SCOPE_IDENTITY();
    SELECT @NewLineId AS LineId, @UnitPrice AS UnitPrice;
END;
GO

/* ============================================================
   تعديل رأس فاتورة مسودة (العميل، المخزن، الدفع، الخيارات...)
   نفس تحققات الإنشاء؛ السطور تبقى كما هي وتُعاد تسعيرتها من الواجهة عند الحاجة.
   ============================================================ */
CREATE OR ALTER PROCEDURE sp_Sales_UpdateDraftHeader
    @InvoiceId              INT,
    @CustomerId             INT,
    @WarehouseId            INT,
    @InvoiceDate            DATE,
    @PaymentMethod          NVARCHAR(20),
    @AmountPaidNow          DECIMAL(18,2)   = 0,
    @TaxEnabled             BIT             = 0,
    @TaxRate                DECIMAL(5,2)    = 14,
    @LoadingSuppliesEnabled BIT             = 0,
    @IsAgentPricing         BIT             = NULL,
    @IsFreeSale             BIT             = 0,
    @FreeSaleRecipient      NVARCHAR(200)   = NULL,
    @SalesRepEmployeeId     INT             = NULL,
    @Notes                  NVARCHAR(400)   = NULL,
    @UserId                 INT
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    IF dbo.fn_UserCan(@UserId, N'Sales', N'Edit') = 0
        THROW 51050, N'لا تملك صلاحية تعديل فواتير المبيعات.', 1;
    IF NOT EXISTS (SELECT 1 FROM SalesInvoices WHERE Id = @InvoiceId AND Status = N'Draft')
        THROW 51051, N'الفاتورة غير موجودة أو مرحّلة (لا يمكن تعديل فاتورة مرحّلة).', 1;

    DECLARE @custType NVARCHAR(20), @custActive BIT, @parentAgent INT;
    SELECT @custType = CustomerType, @custActive = IsActive, @parentAgent = ParentAgentId FROM Customers WHERE Id = @CustomerId;
    IF @custType IS NULL OR @custActive = 0
        THROW 51001, N'العميل غير موجود أو غير فعّال.', 1;
    IF @custType = N'SubCustomer' AND NOT EXISTS (SELECT 1 FROM Customers WHERE Id = @parentAgent AND CustomerType = N'Agent')
        THROW 51002, N'العميل الفرعي يجب أن يكون مرتبطًا بوكيل صحيح.', 1;

    DECLARE @whType NVARCHAR(30), @whSellable BIT, @whOwner INT;
    SELECT @whType = WarehouseType, @whSellable = IsSellableStock, @whOwner = OwnerEmployeeId
    FROM Warehouses WHERE Id = @WarehouseId AND IsActive = 1;
    IF @whType IS NULL THROW 51003, N'المخزن غير موجود أو غير فعّال.', 1;
    IF @whSellable = 0 THROW 51004, N'لا يمكن البيع من هذا المخزن (مخزون غير قابل للبيع).', 1;
    IF @IsFreeSale = 1 AND LEN(LTRIM(ISNULL(@FreeSaleRecipient, N''))) = 0
        THROW 51005, N'المبيعات المجانية تتطلب تحديد الجهة المستفيدة.', 1;
    IF @PaymentMethod NOT IN (N'Cash', N'Credit', N'Partial', N'Electronic')
        THROW 51006, N'طريقة دفع غير صحيحة.', 1;

    IF @SalesRepEmployeeId IS NULL AND @whType = N'RepVan' SET @SalesRepEmployeeId = @whOwner;
    IF @SalesRepEmployeeId IS NOT NULL AND NOT EXISTS
        (SELECT 1 FROM Employees WHERE Id = @SalesRepEmployeeId AND IsSalesRep = 1 AND IsActive = 1)
        THROW 51007, N'الموظف المحدد ليس مندوب مبيعات فعّالًا.', 1;

    SET @IsAgentPricing = ISNULL(@IsAgentPricing, CASE WHEN @custType IN (N'Agent', N'SubCustomer') THEN 1 ELSE 0 END);

    UPDATE SalesInvoices SET
        CustomerId = @CustomerId, WarehouseId = @WarehouseId, InvoiceDate = @InvoiceDate, PaymentMethod = @PaymentMethod,
        AmountPaidNow = CASE WHEN @PaymentMethod = N'Partial' THEN ISNULL(@AmountPaidNow, 0) ELSE 0 END,
        TaxEnabled = CASE WHEN @IsFreeSale = 1 THEN 0 ELSE @TaxEnabled END, TaxRate = @TaxRate,
        LoadingSuppliesEnabled = CASE WHEN @IsFreeSale = 1 THEN 0 ELSE @LoadingSuppliesEnabled END,
        IsAgentPricing = @IsAgentPricing, IsFreeSale = @IsFreeSale,
        FreeSaleRecipient = CASE WHEN @IsFreeSale = 1 THEN LTRIM(RTRIM(@FreeSaleRecipient)) END,
        SalesRepEmployeeId = @SalesRepEmployeeId, Notes = @Notes
    WHERE Id = @InvoiceId;

    -- المجانية: كل السطور بسعر صفر
    IF @IsFreeSale = 1
        UPDATE SalesInvoiceLines SET UnitPrice = 0, LineTotal = 0 WHERE SalesInvoiceId = @InvoiceId;
END;
GO

CREATE OR ALTER PROCEDURE sp_Sales_DeleteInvoiceLine
    @LineId INT,
    @UserId INT
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF dbo.fn_UserCan(@UserId, N'Sales', N'Edit') = 0
        THROW 51020, N'لا تملك صلاحية تعديل فواتير المبيعات.', 1;

    IF NOT EXISTS (SELECT 1 FROM SalesInvoiceLines l JOIN SalesInvoices i ON i.Id = l.SalesInvoiceId
                   WHERE l.Id = @LineId AND i.Status = N'Draft')
        THROW 51021, N'السطر غير موجود أو أن الفاتورة مرحّلة.', 1;

    DELETE FROM SalesInvoiceLines WHERE Id = @LineId;
END;
GO

CREATE OR ALTER PROCEDURE sp_Sales_DeleteDraftInvoice
    @InvoiceId INT,
    @UserId    INT
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF dbo.fn_UserCan(@UserId, N'Sales', N'Delete') = 0
        THROW 51025, N'لا تملك صلاحية حذف فواتير المبيعات.', 1;
    IF NOT EXISTS (SELECT 1 FROM SalesInvoices WHERE Id = @InvoiceId AND Status = N'Draft')
        THROW 51026, N'الفاتورة غير موجودة أو مرحّلة (الفاتورة المرحّلة لا تُحذف).', 1;

    BEGIN TRY
        BEGIN TRAN;
            DELETE FROM SalesInvoiceLines WHERE SalesInvoiceId = @InvoiceId;
            DELETE FROM SalesInvoices WHERE Id = @InvoiceId;
        COMMIT;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO

/* ============================================================
   ترحيل فاتورة المبيعات — العملية الكاملة في معاملة واحدة
   1) احتساب المجاميع (سطور + ضريبة + مستلزمات تحميل)
   2) خصم المخزون من سجل الحركة (مع FIFO للتشغيلات) ومنع الرصيد السالب
   3) توليد القيد المحاسبي المتوازن وترحيله (عدا المبيعات المجانية)
   4) قيد المبلغ النقدي في محفظة المندوب (عند البيع من كاش فان)
   ============================================================ */
CREATE OR ALTER PROCEDURE sp_Sales_PostInvoice
    @InvoiceId  INT,
    @UserId     INT
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    IF dbo.fn_UserCan(@UserId, N'Sales', N'Post') = 0
        THROW 51030, N'لا تملك صلاحية ترحيل فواتير المبيعات.', 1;

    BEGIN TRY
    BEGIN TRAN;

    DECLARE @status NVARCHAR(20), @num NVARCHAR(30), @custId INT, @whId INT, @date DATE,
            @pay NVARCHAR(20), @paid DECIMAL(18,2), @taxOn BIT, @taxRate DECIMAL(5,2),
            @loadOn BIT, @free BIT, @recipient NVARCHAR(200), @repId INT, @whType NVARCHAR(30);

    SELECT @status = i.Status, @num = i.InvoiceNumber, @custId = i.CustomerId, @whId = i.WarehouseId,
           @date = i.InvoiceDate, @pay = i.PaymentMethod, @paid = i.AmountPaidNow,
           @taxOn = i.TaxEnabled, @taxRate = i.TaxRate, @loadOn = i.LoadingSuppliesEnabled,
           @free = i.IsFreeSale, @recipient = i.FreeSaleRecipient, @repId = i.SalesRepEmployeeId,
           @whType = w.WarehouseType
    FROM SalesInvoices i WITH (UPDLOCK, HOLDLOCK)
    JOIN Warehouses w ON w.Id = i.WarehouseId
    WHERE i.Id = @InvoiceId;

    IF @status IS NULL THROW 51031, N'الفاتورة غير موجودة.', 1;
    IF @status <> N'Draft' THROW 51032, N'الفاتورة مرحّلة مسبقًا.', 1;
    IF NOT EXISTS (SELECT 1 FROM SalesInvoiceLines WHERE SalesInvoiceId = @InvoiceId)
        THROW 51033, N'لا يمكن ترحيل فاتورة بلا سطور.', 1;

    -- ---------- 1) المجاميع ----------
    DECLARE @sub DECIMAL(18,2), @pieces DECIMAL(18,3), @tax DECIMAL(18,2) = 0,
            @load DECIMAL(18,2) = 0, @total DECIMAL(18,2);

    SELECT @sub = SUM(LineTotal), @pieces = SUM(QuantityBaseUnits)
    FROM SalesInvoiceLines WHERE SalesInvoiceId = @InvoiceId;

    IF @free = 1
        SELECT @sub = 0, @tax = 0, @load = 0;
    ELSE
    BEGIN
        IF @taxOn = 1  SET @tax  = ROUND(@sub * @taxRate / 100.0, 2);
        IF @loadOn = 1 SET @load = ROUND(@pieces * dbo.fn_Sales_LoadingRate(@date), 2);
    END;
    SET @total = @sub + @tax + @load;

    SET @paid = CASE WHEN @free = 1 THEN 0
                     WHEN @pay IN (N'Cash', N'Electronic') THEN @total
                     WHEN @pay = N'Credit' THEN 0
                     ELSE @paid END;

    IF @pay = N'Partial' AND @free = 0 AND (@paid <= 0 OR @paid >= @total)
        THROW 51034, N'في الدفع الجزئي يجب أن يكون المبلغ المدفوع أكبر من صفر وأقل من إجمالي الفاتورة.', 1;

    -- ---------- 2) خصم المخزون ----------
    -- قفل على مستوى المخزن لمنع بيعين متزامنين لنفس الكمية
    DECLARE @lockName NVARCHAR(100) = N'Stock:' + CAST(@whId AS NVARCHAR(10)), @lockRes INT;
    EXEC @lockRes = sp_getapplock @Resource = @lockName, @LockMode = N'Exclusive',
                                  @LockOwner = N'Transaction', @LockTimeout = 15000;
    IF @lockRes < 0 THROW 51035, N'المخزن مشغول بعملية أخرى، أعد المحاولة.', 1;

    DECLARE @txType NVARCHAR(30) =
        CASE WHEN @whType = N'RepVan' THEN CASE WHEN @free = 1 THEN N'RepFreeSale' ELSE N'RepSale' END
             ELSE CASE WHEN @free = 1 THEN N'FreeIssue' ELSE N'SalesIssue' END END;

    DECLARE @alloc TABLE (ItemId INT, BatchId INT NULL, Qty DECIMAL(18,3));
    DECLARE @lineItem INT, @lineBatch INT, @need DECIMAL(18,3), @itemName NVARCHAR(200), @msg NVARCHAR(400);

    -- الطلب مجمّعًا حسب الصنف/التشغيلة (سطران لنفس الصنف يُحسبان معًا)
    DECLARE req CURSOR LOCAL FAST_FORWARD FOR
        SELECT ItemId, BatchId, SUM(QuantityBaseUnits)
        FROM SalesInvoiceLines WHERE SalesInvoiceId = @InvoiceId
        GROUP BY ItemId, BatchId
        ORDER BY CASE WHEN BatchId IS NULL THEN 1 ELSE 0 END, ItemId;  -- المحدد أولًا ثم FIFO
    OPEN req;
    FETCH NEXT FROM req INTO @lineItem, @lineBatch, @need;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        DECLARE @avail TABLE (BatchId INT NULL, Qty DECIMAL(18,3), Ord INT);
        DELETE FROM @avail;

        INSERT INTO @avail (BatchId, Qty, Ord)
        SELECT b.BatchId,
               b.QuantityBaseUnits - ISNULL((SELECT SUM(a.Qty) FROM @alloc a
                                             WHERE a.ItemId = @lineItem
                                               AND ISNULL(a.BatchId, -1) = ISNULL(b.BatchId, -1)), 0),
               ROW_NUMBER() OVER (ORDER BY CASE WHEN ib.ExpiryDate IS NULL THEN 1 ELSE 0 END,
                                           ib.ExpiryDate, ib.ManufactureDate, b.BatchId)
        FROM vw_StockBalance b
        LEFT JOIN ItemBatches ib ON ib.Id = b.BatchId
        WHERE b.ItemId = @lineItem AND b.WarehouseId = @whId
          AND (@lineBatch IS NULL OR b.BatchId = @lineBatch);

        DELETE FROM @avail WHERE Qty <= 0;

        IF ISNULL((SELECT SUM(Qty) FROM @avail), 0) < @need
        BEGIN
            SELECT @itemName = ItemName FROM Items WHERE Id = @lineItem;
            SET @msg = N'الرصيد غير كافٍ للصنف "' + @itemName + N'": المطلوب '
                     + FORMAT(@need, N'0.###') + N' قطعة، المتاح '
                     + FORMAT(ISNULL((SELECT SUM(Qty) FROM @avail), 0), N'0.###') + N' قطعة.';
            THROW 51036, @msg, 1;
        END;

        -- توزيع الكمية على التشغيلات بالترتيب (FIFO)
        INSERT INTO @alloc (ItemId, BatchId, Qty)
        SELECT @lineItem, BatchId,
               CASE WHEN Cum - Qty >= @need THEN 0
                    WHEN Cum <= @need THEN Qty
                    ELSE @need - (Cum - Qty) END
        FROM (SELECT BatchId, Qty, SUM(Qty) OVER (ORDER BY Ord ROWS UNBOUNDED PRECEDING) AS Cum
              FROM @avail) x
        WHERE Cum - Qty < @need;

        FETCH NEXT FROM req INTO @lineItem, @lineBatch, @need;
    END;
    CLOSE req; DEALLOCATE req;

    INSERT INTO StockTransactions
        (ItemId, WarehouseId, BatchId, QuantityBaseUnits, TransactionType,
         FreeIssueRecipient, ReferenceTable, ReferenceId, TransactionDate, CreatedByUserId)
    SELECT ItemId, @whId, BatchId, -SUM(Qty), @txType,
           CASE WHEN @free = 1 THEN @recipient END, N'SalesInvoices', @InvoiceId,
           SYSUTCDATETIME(), @UserId
    FROM @alloc WHERE Qty > 0
    GROUP BY ItemId, BatchId;

    -- ---------- 3) القيد المحاسبي (العقل المالي) ----------
    DECLARE @jeId INT = NULL;

    IF @free = 0 AND @total > 0
    BEGIN
        DECLARE @cashRule NVARCHAR(60) =
            CASE WHEN @pay = N'Electronic' THEN N'SalesInvoiceElectronic'
                 WHEN @repId IS NOT NULL   THEN N'SalesInvoiceRepCash'
                 ELSE N'SalesInvoiceCash' END;
        DECLARE @revenueRule NVARCHAR(60) = CASE WHEN @pay IN (N'Credit', N'Partial')
                                                 THEN N'SalesInvoiceCredit' ELSE @cashRule END;

        DECLARE @cashAcc INT, @arAcc INT, @revAcc INT, @taxAcc INT, @loadAcc INT;
        SELECT @cashAcc = DebitAccountId FROM AccountMappingRules WHERE TransactionType = @cashRule;
        SELECT @arAcc   = DebitAccountId FROM AccountMappingRules WHERE TransactionType = N'SalesInvoiceCredit';
        SELECT @revAcc  = CreditAccountId FROM AccountMappingRules WHERE TransactionType = @revenueRule;
        SELECT @taxAcc  = CreditAccountId FROM AccountMappingRules WHERE TransactionType = N'SalesTax';
        SELECT @loadAcc = CreditAccountId FROM AccountMappingRules WHERE TransactionType = N'LoadingSuppliesCharge';

        IF @paid > 0 AND @cashAcc IS NULL
        BEGIN SET @msg = N'قاعدة الربط المحاسبي "' + @cashRule + N'" غير معرّفة في الإعدادات المالية.'; THROW 51040, @msg, 1; END;
        IF @paid < @total AND @arAcc IS NULL
            THROW 51041, N'قاعدة الربط المحاسبي "SalesInvoiceCredit" (ذمم العملاء) غير معرّفة.', 1;
        IF @revAcc IS NULL
        BEGIN SET @msg = N'قاعدة الربط المحاسبي "' + @revenueRule + N'" (إيرادات المبيعات) غير معرّفة.'; THROW 51042, @msg, 1; END;
        IF @tax > 0 AND @taxAcc IS NULL
            THROW 51043, N'قاعدة الربط المحاسبي "SalesTax" (الضريبة المستحقة) غير معرّفة.', 1;
        IF @load > 0 AND @loadAcc IS NULL
            THROW 51044, N'قاعدة الربط المحاسبي "LoadingSuppliesCharge" (مستلزمات التحميل) غير معرّفة.', 1;

        DECLARE @custName NVARCHAR(200) = (SELECT Name FROM Customers WHERE Id = @custId);
        DECLARE @jeNum NVARCHAR(30) = N'SJ-' + CAST(YEAR(@date) AS NVARCHAR(4)) + N'-'
                + RIGHT(N'000000' + CAST(NEXT VALUE FOR seq_SalesJournalNumber AS NVARCHAR(10)), 6);

        INSERT INTO JournalEntries (EntryNumber, EntryDate, EntryType, Description, CreatedByUserId,
                                    IsPosted, SourceTable, SourceId)
        VALUES (@jeNum, @date, N'AutoSales',
                N'فاتورة مبيعات ' + @num + N' — ' + @custName, @UserId, 0, N'SalesInvoices', @InvoiceId);
        SET @jeId = SCOPE_IDENTITY();

        INSERT INTO JournalEntryLines (JournalEntryId, AccountId, Debit, Credit, Description)
        SELECT @jeId, AccountId, Debit, Credit, Description
        FROM (VALUES
            (@cashAcc, @paid,         0,    N'المقبوض عند البيع'),
            (@arAcc,   @total - @paid, 0,   N'ذمم العميل: ' + @custName),
            (@revAcc,  0,             @sub, N'إيرادات مبيعات'),
            (@taxAcc,  0,             @tax, N'ضريبة مبيعات'),
            (@loadAcc, 0,             @load, N'مستلزمات التحميل')
        ) v(AccountId, Debit, Credit, Description)
        WHERE Debit > 0 OR Credit > 0;

        IF (SELECT SUM(Debit) - SUM(Credit) FROM JournalEntryLines WHERE JournalEntryId = @jeId) <> 0
            THROW 51045, N'القيد غير متوازن (مدين ≠ دائن) — تم إلغاء الترحيل.', 1;

        UPDATE JournalEntries SET IsPosted = 1 WHERE Id = @jeId;
    END;

    -- ---------- 4) محفظة المندوب (النقد المقبوض ميدانيًا فقط) ----------
    IF @repId IS NOT NULL AND @paid > 0 AND @pay <> N'Electronic'
        INSERT INTO RepWalletTransactions (EmployeeId, Description, AmountIn, AmountOut,
                                           ReferenceTable, ReferenceId, JournalEntryId)
        VALUES (@repId, N'مبيعات نقدية — فاتورة ' + @num, @paid, 0, N'SalesInvoices', @InvoiceId, @jeId);

    -- ---------- 4ب) الصندوق: النقد المقبوض في المصنع (لا مندوب، ولا دفع إلكتروني) ----------
    -- يدخل صندوق المستخدم (أو الافتراضي) — sp_CashBox_RecordAuto في 12_warehouse_docs_cashboxes.sql
    IF @repId IS NULL AND @paid > 0 AND @pay <> N'Electronic' AND @whType <> N'RepVan'
       AND OBJECT_ID('dbo.sp_CashBox_RecordAuto', 'P') IS NOT NULL
    BEGIN
        DECLARE @cbDesc NVARCHAR(400) = N'مبيعات نقدية — فاتورة ' + @num,
                @cbParty NVARCHAR(200) = (SELECT Name FROM Customers WHERE Id = @custId);
        EXEC dbo.sp_CashBox_RecordAuto @UserId = @UserId, @TxType = N'SalesReceipt', @Amount = @paid, @TxDate = @date,
             @ReferenceTable = N'SalesInvoices', @ReferenceId = @InvoiceId, @PartyName = @cbParty,
             @Description = @cbDesc, @JournalEntryId = @jeId;
    END;

    -- ---------- 5) إغلاق الفاتورة ----------
    UPDATE SalesInvoices
    SET SubTotal = @sub, TaxAmount = @tax, LoadingSuppliesAmount = @load, TotalAmount = @total,
        AmountPaidNow = @paid, Status = N'Posted', JournalEntryId = @jeId,
        PostedByUserId = @UserId, PostedAt = SYSUTCDATETIME()
    WHERE Id = @InvoiceId;

    COMMIT;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;
        THROW;
    END CATCH;

    SELECT @InvoiceId AS InvoiceId, @num AS InvoiceNumber, @sub AS SubTotal, @tax AS TaxAmount,
           @load AS LoadingSuppliesAmount, @total AS TotalAmount, @paid AS AmountPaidNow,
           @total - @paid AS AmountDue, @jeId AS JournalEntryId;
END;
GO

/* ============================================================
   معاينة مجاميع الفاتورة قبل الترحيل (للعرض في الشاشة)
   ============================================================ */
CREATE OR ALTER VIEW vw_SalesInvoiceTotals AS
SELECT  i.Id AS InvoiceId, i.InvoiceNumber, i.Status, i.IsFreeSale,
        CASE WHEN i.Status = N'Posted' THEN i.SubTotal
             WHEN i.IsFreeSale = 1 THEN 0 ELSE ISNULL(l.Sub, 0) END AS SubTotal,
        CASE WHEN i.Status = N'Posted' THEN i.TaxAmount
             WHEN i.IsFreeSale = 1 OR i.TaxEnabled = 0 THEN 0
             ELSE ROUND(ISNULL(l.Sub, 0) * i.TaxRate / 100.0, 2) END AS TaxAmount,
        CASE WHEN i.Status = N'Posted' THEN i.LoadingSuppliesAmount
             WHEN i.IsFreeSale = 1 OR i.LoadingSuppliesEnabled = 0 THEN 0
             ELSE ROUND(ISNULL(l.Pieces, 0) * dbo.fn_Sales_LoadingRate(i.InvoiceDate), 2) END AS LoadingSuppliesAmount,
        ISNULL(l.Pieces, 0) AS TotalPieces
FROM    SalesInvoices i
LEFT JOIN (SELECT SalesInvoiceId, SUM(LineTotal) AS Sub, SUM(QuantityBaseUnits) AS Pieces
           FROM SalesInvoiceLines GROUP BY SalesInvoiceId) l ON l.SalesInvoiceId = i.Id;
GO

/* ============================================================
   قائمة الفواتير (لشاشة البحث/العرض)
   ============================================================ */
CREATE OR ALTER VIEW vw_SalesInvoiceList AS
SELECT  i.Id, i.InvoiceNumber, i.InvoiceDate, i.Status,
        i.CustomerId, c.Name AS CustomerName, c.CustomerType,
        w.Name AS WarehouseName, e.FullName AS SalesRepName,
        i.PaymentMethod, i.IsFreeSale, i.FreeSaleRecipient, i.IsAgentPricing,
        t.SubTotal, t.TaxAmount, t.LoadingSuppliesAmount,
        t.SubTotal + t.TaxAmount + t.LoadingSuppliesAmount AS TotalAmount,
        i.AmountPaidNow,
        CASE WHEN i.Status = N'Posted' THEN i.TotalAmount - i.AmountPaidNow END AS AmountDue,
        i.JournalEntryId
FROM    SalesInvoices i
JOIN    Customers c  ON c.Id = i.CustomerId
JOIN    Warehouses w ON w.Id = i.WarehouseId
LEFT JOIN Employees e ON e.Id = i.SalesRepEmployeeId
JOIN    vw_SalesInvoiceTotals t ON t.InvoiceId = i.Id;
GO

/* ============================================================
   كشف حساب العميل (مديونية كل عميل مستقلة — فواتيره وسنداته فقط)
   مدين = قيمة الفاتورة، دائن = المدفوع عند البيع + سندات القبض
   ============================================================ */
CREATE OR ALTER VIEW vw_CustomerStatement AS
SELECT  i.CustomerId, i.InvoiceDate AS TxDate, N'SalesInvoice' AS TxType,
        i.InvoiceNumber AS DocNumber, i.Id AS DocId,
        i.TotalAmount AS Debit, CAST(0 AS DECIMAL(18,2)) AS Credit,
        N'فاتورة مبيعات' AS Description
FROM    SalesInvoices i
WHERE   i.Status = N'Posted' AND i.IsFreeSale = 0
UNION ALL
SELECT  i.CustomerId, i.InvoiceDate, N'PaidAtSale', i.InvoiceNumber, i.Id,
        0, i.AmountPaidNow, N'مدفوع عند البيع'
FROM    SalesInvoices i
WHERE   i.Status = N'Posted' AND i.IsFreeSale = 0 AND i.AmountPaidNow > 0
UNION ALL
SELECT  v.PartyId, v.VoucherDate,
        CASE v.VoucherType WHEN N'Receipt' THEN N'ReceiptVoucher' ELSE N'PaymentVoucher' END,
        v.VoucherNumber, v.Id,
        CASE WHEN v.VoucherType = N'Payment' THEN v.Amount ELSE 0 END,
        CASE WHEN v.VoucherType = N'Receipt' THEN v.Amount ELSE 0 END,
        CASE v.VoucherType WHEN N'Receipt' THEN N'سند قبض' ELSE N'سند صرف' END
FROM    Vouchers v
WHERE   v.PartyType = N'Customer' AND v.PartyId IS NOT NULL;
GO

CREATE OR ALTER VIEW vw_CustomerBalances AS
SELECT  c.Id AS CustomerId, c.Name, c.CustomerType, c.ParentAgentId,
        ISNULL(SUM(s.Debit), 0)  AS TotalDebit,
        ISNULL(SUM(s.Credit), 0) AS TotalCredit,
        ISNULL(SUM(s.Debit - s.Credit), 0) AS Balance     -- موجب = على العميل
FROM    Customers c
LEFT JOIN vw_CustomerStatement s ON s.CustomerId = c.Id
GROUP BY c.Id, c.Name, c.CustomerType, c.ParentAgentId;
GO
