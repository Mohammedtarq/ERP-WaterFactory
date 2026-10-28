/* ============================================================
   رسوم مستلزمات التحميل بالاسم والسعر والعدد (لفواتير الوكلاء وعملائهم فقط، يضبطها التطبيق)
   - SalesInvoiceFees: سطر رسم في الفاتورة؛ مجموعها = LoadingSuppliesAmount (إيراد مستلزمات التحميل 4102)
   - الفاتورة بلا سطور رسوم تبقى بحسابها السابق (سعر القطعة × القطع) فلا يتغير شيء من القديم
   - sp_Sales_PostInvoice وvw_SalesInvoiceTotals يقرآن الرسوم
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */

IF OBJECT_ID('SalesInvoiceFees', 'U') IS NULL
CREATE TABLE SalesInvoiceFees (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    SalesInvoiceId  INT           NOT NULL FOREIGN KEY REFERENCES SalesInvoices(Id) ON DELETE CASCADE,
    Name            NVARCHAR(100) NOT NULL,
    UnitPrice       DECIMAL(18,2) NOT NULL CONSTRAINT CK_SalesInvoiceFees_Price CHECK (UnitPrice >= 0),
    Quantity        DECIMAL(18,3) NOT NULL CONSTRAINT CK_SalesInvoiceFees_Qty CHECK (Quantity > 0),
    Amount          AS CAST(ROUND(UnitPrice * Quantity, 2) AS DECIMAL(18,2)) PERSISTED,
    SortOrder       INT           NOT NULL DEFAULT 0
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SalesInvoiceFees_Invoice')
    CREATE INDEX IX_SalesInvoiceFees_Invoice ON SalesInvoiceFees (SalesInvoiceId);
GO

CREATE OR ALTER VIEW vw_SalesInvoiceTotals AS
SELECT  i.Id AS InvoiceId, i.InvoiceNumber, i.Status, i.IsFreeSale,
        CASE WHEN i.Status = N'Posted' THEN i.SubTotal
             WHEN i.IsFreeSale = 1 THEN 0 ELSE ISNULL(l.Sub, 0) END AS SubTotal,
        CASE WHEN i.Status = N'Posted' THEN i.TaxAmount
             WHEN i.IsFreeSale = 1 OR i.TaxEnabled = 0 THEN 0
             ELSE ROUND(ISNULL(l.Sub, 0) * i.TaxRate / 100.0, 2) END AS TaxAmount,
        CASE WHEN i.Status = N'Posted' THEN i.LoadingSuppliesAmount
             WHEN i.IsFreeSale = 1 OR i.LoadingSuppliesEnabled = 0 THEN 0
             WHEN f.Total IS NOT NULL THEN f.Total
             ELSE ROUND(ISNULL(l.Pieces, 0) * dbo.fn_Sales_LoadingRate(i.InvoiceDate), 2) END AS LoadingSuppliesAmount,
        ISNULL(l.Pieces, 0) AS TotalPieces
FROM    SalesInvoices i
LEFT JOIN (SELECT SalesInvoiceId, SUM(LineTotal) AS Sub, SUM(QuantityBaseUnits) AS Pieces
           FROM SalesInvoiceLines GROUP BY SalesInvoiceId) l ON l.SalesInvoiceId = i.Id
LEFT JOIN (SELECT SalesInvoiceId, SUM(Amount) AS Total FROM SalesInvoiceFees GROUP BY SalesInvoiceId) f ON f.SalesInvoiceId = i.Id;
GO

CREATE OR ALTER PROCEDURE sp_Sales_PostInvoice
    @InvoiceId  INT,
    @UserId     INT,
    -- بيع تطبيق المندوب: يتجاوز حد الدين بقرار الإدارة (إعدادات التطبيق)، ويُسجَّل تنبيهًا للمراجعة
    @AllowOverLimit BIT = 0
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
        -- رسوم التحميل: سطور الرسوم المكتوبة في الفاتورة (اسم وسعر وعدد) إن وُجدت، وإلا سعر القطعة السابق
        IF @loadOn = 1
            SET @load = CASE WHEN EXISTS (SELECT 1 FROM SalesInvoiceFees WHERE SalesInvoiceId = @InvoiceId)
                             THEN (SELECT SUM(Amount) FROM SalesInvoiceFees WHERE SalesInvoiceId = @InvoiceId)
                             ELSE ROUND(@pieces * dbo.fn_Sales_LoadingRate(@date), 2) END;
    END;
    SET @total = @sub + @tax + @load;

    SET @paid = CASE WHEN @free = 1 THEN 0
                     WHEN @pay IN (N'Cash', N'Electronic') THEN @total
                     WHEN @pay = N'Credit' THEN 0
                     ELSE @paid END;

    IF @pay = N'Partial' AND @free = 0 AND (@paid <= 0 OR @paid >= @total)
        THROW 51034, N'في الدفع الجزئي يجب أن يكون المبلغ المدفوع أكبر من صفر وأقل من إجمالي الفاتورة.', 1;

    -- حد الدين: الآجل أو الجزئي يُرفض إن تجاوز رصيدُ العميل بعد الفاتورة حدَّه، إلا لمن يملك صلاحية التجاوز
    DECLARE @limit DECIMAL(18,2) = (SELECT CreditLimit FROM Customers WHERE Id = @custId);
    IF @free = 0 AND @total > @paid AND @limit IS NOT NULL
    BEGIN
        DECLARE @balance DECIMAL(18,2) = ISNULL((SELECT Balance FROM vw_CustomerBalances WHERE CustomerId = @custId), 0);
        IF @balance + (@total - @paid) > @limit AND @AllowOverLimit = 0
           AND NOT EXISTS (SELECT 1 FROM Users u JOIN RolePermissions rp ON rp.RoleId = u.RoleId
                           WHERE u.Id = @UserId AND rp.ModuleCode = N'Special:CreditOverride' AND rp.CanView = 1)
        BEGIN
            DECLARE @limitMsg NVARCHAR(400) = N'العميل تجاوز حد دينه (' + FORMAT(@limit, N'N0') + N' د.ع): رصيده الحالي ' + FORMAT(@balance, N'N0')
                     + N' والفاتورة تضيف ' + FORMAT(@total - @paid, N'N0') + N'. البيع الآجل موقوف حتى يوافق المدير، أو اقبض نقدًا.';
            THROW 51046, @limitMsg, 1;
        END;
    END;

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
    DECLARE @short TABLE (ItemId INT, Qty DECIMAL(18,3));
    DECLARE @lineItem INT, @lineBatch INT, @lineRecipe INT, @need DECIMAL(18,3), @itemName NVARCHAR(200), @msg NVARCHAR(1000), @reservedHint NVARCHAR(500);
    -- البيع من رصيد محجوز لعميل آخر (تشغيلات مطعم) بصلاحية خاصة
    DECLARE @canReserved BIT = CASE WHEN EXISTS (
            SELECT 1 FROM Users u JOIN RolePermissions rp ON rp.RoleId = u.RoleId
            WHERE u.Id = @UserId AND rp.ModuleCode = N'Special:ReservedStock' AND rp.CanView = 1) THEN 1 ELSE 0 END;
    -- البيع بانتظار الإنتاج: مخزن المنتج التام فقط، وبصلاحية خاصة تحددها الإدارة
    DECLARE @canPend BIT = CASE WHEN @whType = N'FinishedGoods' AND EXISTS (
            SELECT 1 FROM Users u JOIN RolePermissions rp ON rp.RoleId = u.RoleId
            WHERE u.Id = @UserId AND rp.ModuleCode = N'Special:SellPending' AND rp.CanView = 1) THEN 1 ELSE 0 END;

    -- الطلب مجمّعًا حسب الصنف/التشغيلة (سطران لنفس الصنف يُحسبان معًا)
    DECLARE req CURSOR LOCAL FAST_FORWARD FOR
        SELECT l.ItemId, l.BatchId, COALESCE(l.CustomRecipeId, b.CustomRecipeId), SUM(l.QuantityBaseUnits)
        FROM SalesInvoiceLines l LEFT JOIN ItemBatches b ON b.Id = l.BatchId
        WHERE l.SalesInvoiceId = @InvoiceId
        GROUP BY l.ItemId, l.BatchId, COALESCE(l.CustomRecipeId, b.CustomRecipeId)
        -- المحدد أولًا، ثم المتغير المطلوب بالاسم، ثم FIFO
        ORDER BY CASE WHEN l.BatchId IS NULL THEN 1 ELSE 0 END, CASE WHEN COALESCE(l.CustomRecipeId, b.CustomRecipeId) IS NULL THEN 1 ELSE 0 END, l.ItemId;
    OPEN req;
    FETCH NEXT FROM req INTO @lineItem, @lineBatch, @lineRecipe, @need;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        DECLARE @avail TABLE (BatchId INT NULL, Qty DECIMAL(18,3), Ord INT);
        DELETE FROM @avail;

        INSERT INTO @avail (BatchId, Qty, Ord)
        SELECT b.BatchId,
               b.QuantityBaseUnits - ISNULL((SELECT SUM(a.Qty) FROM @alloc a
                                             WHERE a.ItemId = @lineItem
                                               AND ISNULL(a.BatchId, -1) = ISNULL(b.BatchId, -1)), 0),
               ROW_NUMBER() OVER (ORDER BY p.Priority, CASE WHEN ib.ExpiryDate IS NULL THEN 1 ELSE 0 END,
                                           ib.ExpiryDate, ib.ManufactureDate, b.BatchId)
        FROM vw_StockBalance b
        LEFT JOIN ItemBatches ib ON ib.Id = b.BatchId
        CROSS APPLY (SELECT dbo.fn_Stock_BatchPriority(b.BatchId, @custId, @lineRecipe, @canReserved) AS Priority) p
        WHERE b.ItemId = @lineItem AND b.WarehouseId = @whId
          AND (@lineBatch IS NULL OR b.BatchId = @lineBatch)
          AND p.Priority IS NOT NULL;

        DELETE FROM @avail WHERE Qty <= 0;

        IF ISNULL((SELECT SUM(Qty) FROM @avail), 0) < @need AND @canPend = 1 AND @lineBatch IS NULL AND @lineRecipe IS NULL
        BEGIN
            -- يخرج المتاح كله، والفرق عجز بانتظار الإنتاج
            INSERT INTO @short (ItemId, Qty) VALUES (@lineItem, @need - ISNULL((SELECT SUM(Qty) FROM @avail), 0));
            SET @need = ISNULL((SELECT SUM(Qty) FROM @avail), 0);
        END;

        IF ISNULL((SELECT SUM(Qty) FROM @avail), 0) < @need
        BEGIN
            SELECT @itemName = ItemName FROM Items WHERE Id = @lineItem;
            -- ما لا يحق لهذا البيع من رصيد المتغيرات الأخرى، ليعرف البائع السبب
            SELECT @reservedHint = STRING_AGG(x.Name + N' ' + FORMAT(x.Qty, N'0.###'), N'، ')
            FROM (SELECT r.Name, SUM(b.QuantityBaseUnits) AS Qty
                  FROM vw_StockBalance b JOIN ItemBatches ib ON ib.Id = b.BatchId JOIN CustomRecipes r ON r.Id = ib.CustomRecipeId
                  WHERE b.ItemId = @lineItem AND b.WarehouseId = @whId AND b.QuantityBaseUnits > 0
                    AND dbo.fn_Stock_BatchPriority(b.BatchId, @custId, @lineRecipe, @canReserved) IS NULL
                  GROUP BY r.Name) x;
            SET @msg = N'الرصيد غير كافٍ للصنف "' + @itemName + N'"'
                     + ISNULL(N' (' + (SELECT Name FROM CustomRecipes WHERE Id = @lineRecipe) + N')', N'') + N': المطلوب '
                     + FORMAT(@need, N'0.###') + N' قطعة، المتاح '
                     + FORMAT(ISNULL((SELECT SUM(Qty) FROM @avail), 0), N'0.###') + N' قطعة.'
                     + ISNULL(N' ويوجد رصيد متغيرات لا يُصرف لهذا البيع: ' + @reservedHint + N' قطعة.', N'');
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

        FETCH NEXT FROM req INTO @lineItem, @lineBatch, @lineRecipe, @need;
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

    -- العجز بانتظار الإنتاج: حركة بلا تشغيلة تُسوّى عند تسجيل الإنتاج، مع سجل باسم الصنف والفاتورة
    INSERT INTO StockTransactions
        (ItemId, WarehouseId, BatchId, QuantityBaseUnits, TransactionType,
         FreeIssueRecipient, ReferenceTable, ReferenceId, TransactionDate, CreatedByUserId)
    SELECT ItemId, @whId, NULL, -SUM(Qty), @txType,
           CASE WHEN @free = 1 THEN @recipient END, N'SalesInvoices', @InvoiceId, SYSUTCDATETIME(), @UserId
    FROM @short GROUP BY ItemId;
    INSERT INTO PendingProductionShortages (ItemId, WarehouseId, Quantity, SalesInvoiceId, CreatedByUserId)
    SELECT ItemId, @whId, SUM(Qty), @InvoiceId, @UserId FROM @short GROUP BY ItemId;

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
