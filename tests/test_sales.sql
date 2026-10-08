/* ============================================================
   اختبارات وحدة المبيعات — تُنفَّذ على قاعدة بيانات فارغة
   بعد تنفيذ الملفات 01 → 09. أي فشل يوقف التنفيذ برسالة واضحة.
   ============================================================ */
SET NOCOUNT ON; SET XACT_ABORT ON;
GO

-- كل فحص يُسجَّل هنا لحظة تنفيذه، ثم يُطبع التقرير في النهاية ويفشل التنفيذ إن وُجد خطأ
CREATE TABLE #T (Seq INT IDENTITY(1,1), Name NVARCHAR(200), Actual SQL_VARIANT, Expected SQL_VARIANT);
GO

-- ============ بيانات أولية ============
INSERT INTO Branches (Name) VALUES (N'الفرع الرئيسي - البصرة');
INSERT INTO Roles (Name) VALUES (N'مدير عام'), (N'موظف مبيعات');
INSERT INTO RolePermissions (RoleId, ModuleCode, CanView, CanAdd, CanEdit, CanDelete, CanPost)
VALUES (1, N'Sales', 1, 1, 1, 1, 1),
       (2, N'Sales', 1, 1, 1, 0, 0);          -- موظف المبيعات لا يرحّل
INSERT INTO Employees (FullName, IsSalesRep) VALUES (N'مدير النظام', 0), (N'علي المندوب', 1);
INSERT INTO Users (Username, PasswordHash, RoleId, EmployeeId) VALUES (N'admin', N'x', 1, 1), (N'clerk', N'x', 2, NULL);

INSERT INTO ChartOfAccounts (AccountCode, AccountName, AccountType) VALUES
 (N'1101', N'الصندوق', N'Asset'), (N'1102', N'البنك', N'Asset'), (N'1103', N'عهدة المندوبين', N'Asset'),
 (N'1201', N'ذمم العملاء', N'Asset'), (N'2101', N'ضريبة مبيعات مستحقة', N'Liability'),
 (N'4101', N'إيرادات المبيعات', N'Revenue'), (N'4102', N'إيراد مستلزمات التحميل', N'Revenue');

INSERT INTO AccountMappingRules (TransactionType, DebitAccountId, CreditAccountId) VALUES
 (N'SalesInvoiceCash', 1, 6), (N'SalesInvoiceCredit', 4, 6), (N'SalesInvoiceRepCash', 3, 6),
 (N'SalesTax', 4, 5), (N'LoadingSuppliesCharge', 4, 7);
 -- SalesInvoiceElectronic غير معرّفة عمدًا لاختبار رسالة الخطأ

INSERT INTO LoadingSuppliesSettings (RatePerPiece, EffectiveDate) VALUES (5, '2026-01-01'), (10, '2026-09-01');

INSERT INTO Items (ItemCode, ItemName, SalePrice) VALUES (N'W500', N'ماء 500 مل', 250), (N'W1500', N'ماء 1.5 لتر', 500);
INSERT INTO ItemPackagingLevels (ItemId, LevelName, ParentLevelId, ContainsQuantity, EquivalentBaseUnits) VALUES
 (1, N'قطعة', NULL, 1, 1), (1, N'كارتون', 1, 12, 12), (2, N'قطعة', NULL, 1, 1), (2, N'شرنك', 3, 6, 6);

INSERT INTO Warehouses (BranchId, Name, WarehouseType, IsSellableStock, OwnerEmployeeId) VALUES
 (1, N'مخزن المنتج التام', N'FinishedGoods', 1, NULL),
 (1, N'كاش فان علي', N'RepVan', 1, 2),
 (1, N'مخزن التالف', N'Damaged', 0, NULL);

INSERT INTO ItemBatches (ItemId, BatchNumber, ExpiryDate) VALUES
 (1, N'B-LATE', '2027-06-01'), (1, N'B-EARLY', '2027-01-01'), (2, N'C-1', '2027-03-01');

INSERT INTO StockTransactions (ItemId, WarehouseId, BatchId, QuantityBaseUnits, TransactionType, CreatedByUserId) VALUES
 (1, 1, 1, 1000, N'Receipt', 1),   -- B-LATE
 (1, 1, 2,  100, N'Receipt', 1),   -- B-EARLY (تنتهي أولًا)
 (2, 1, 3,  600, N'Receipt', 1),
 (1, 2, 2,  240, N'RepLoad', 1);   -- تحميل الكاش فان

INSERT INTO Customers (Name, CustomerType, ParentAgentId) VALUES
 (N'وكيل الزبير', N'Agent', NULL), (N'محل أبو حيدر', N'SubCustomer', 1), (N'زبون مباشر', N'Direct', NULL);
INSERT INTO AgentItemPrices (CustomerId, ItemId, AgentPrice) VALUES (1, 1, 200);
GO

DECLARE @inv INT, @line INT, @price DECIMAL(18,2), @err INT;

PRINT N'--- 1) التسعير الهرمي ---';
INSERT INTO #T (Name, Actual, Expected) VALUES (N'سعر الوكيل للقطعة', dbo.fn_Sales_BaseUnitPrice(1, 1, 1), CAST(200 AS DECIMAL(18,4)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'العميل الفرعي يرث سعر وكيله', dbo.fn_Sales_BaseUnitPrice(2, 1, 1), CAST(200 AS DECIMAL(18,4)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'العميل المباشر بالسعر العادي', dbo.fn_Sales_BaseUnitPrice(3, 1, 1), CAST(250 AS DECIMAL(18,4)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'صنف بلا سعر وكيل ← السعر العادي', dbo.fn_Sales_BaseUnitPrice(1, 2, 1), CAST(500 AS DECIMAL(18,4)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'إلغاء تسعير الوكيل يدويًا', dbo.fn_Sales_BaseUnitPrice(1, 1, 0), CAST(250 AS DECIMAL(18,4)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'سعر التحميل الساري', dbo.fn_Sales_LoadingRate('2026-09-30'), CAST(10 AS DECIMAL(18,2)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'سعر التحميل القديم', dbo.fn_Sales_LoadingRate('2026-05-01'), CAST(5 AS DECIMAL(18,2)));

PRINT N'--- 2) فاتورة نقدية لوكيل + ضريبة + مستلزمات تحميل + FIFO ---';
EXEC sp_Sales_CreateInvoice @CustomerId = 1, @WarehouseId = 1, @InvoiceDate = '2026-09-30',
     @PaymentMethod = N'Cash', @TaxEnabled = 1, @TaxRate = 14, @LoadingSuppliesEnabled = 1,
     @UserId = 1, @NewInvoiceId = @inv OUTPUT;
EXEC sp_Sales_AddInvoiceLine @InvoiceId = @inv, @ItemId = 1, @PackagingLevelId = 2, @QuantityInLevel = 10,
     @UserId = 1, @NewLineId = @line OUTPUT;                                   -- 10 كارتون = 120 قطعة
INSERT INTO #T (Name, Actual, Expected) VALUES (N'سعر الكارتون للوكيل = 12 × 200', (SELECT UnitPrice FROM SalesInvoiceLines WHERE Id = @line), CAST(2400 AS DECIMAL(18,2)));
EXEC sp_Sales_PostInvoice @InvoiceId = @inv, @UserId = 1;

INSERT INTO #T (Name, Actual, Expected) VALUES (N'المجموع الفرعي', (SELECT SubTotal FROM SalesInvoices WHERE Id = @inv), CAST(24000 AS DECIMAL(18,2)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'الضريبة 14%', (SELECT TaxAmount FROM SalesInvoices WHERE Id = @inv), CAST(3360 AS DECIMAL(18,2)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'مستلزمات التحميل 120 × 10', (SELECT LoadingSuppliesAmount FROM SalesInvoices WHERE Id = @inv), CAST(1200 AS DECIMAL(18,2)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'الإجمالي', (SELECT TotalAmount FROM SalesInvoices WHERE Id = @inv), CAST(28560 AS DECIMAL(18,2)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'نقدي ← مدفوع بالكامل', (SELECT AmountPaidNow FROM SalesInvoices WHERE Id = @inv), CAST(28560 AS DECIMAL(18,2)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'FIFO: التشغيلة الأقرب انتهاءً استُنفدت (100)',
     (SELECT SUM(QuantityBaseUnits) FROM StockTransactions WHERE ReferenceId = @inv AND ReferenceTable = N'SalesInvoices' AND BatchId = 2), CAST(-100 AS DECIMAL(18,3)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'FIFO: الباقي (20) من التشغيلة التالية',
     (SELECT SUM(QuantityBaseUnits) FROM StockTransactions WHERE ReferenceId = @inv AND ReferenceTable = N'SalesInvoices' AND BatchId = 1), CAST(-20 AS DECIMAL(18,3)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'نوع الحركة SalesIssue',
     (SELECT MIN(TransactionType) FROM StockTransactions WHERE ReferenceId = @inv AND ReferenceTable = N'SalesInvoices'), CAST(N'SalesIssue' AS NVARCHAR(30)));

DECLARE @je INT = (SELECT JournalEntryId FROM SalesInvoices WHERE Id = @inv);
INSERT INTO #T (Name, Actual, Expected) VALUES (N'القيد مرحّل', (SELECT IsPosted FROM JournalEntries WHERE Id = @je), CAST(1 AS BIT));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'القيد متوازن', (SELECT SUM(Debit) - SUM(Credit) FROM JournalEntryLines WHERE JournalEntryId = @je), CAST(0 AS DECIMAL(38,2)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'مدين الصندوق', (SELECT Debit FROM JournalEntryLines WHERE JournalEntryId = @je AND AccountId = 1), CAST(28560 AS DECIMAL(18,2)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'دائن الإيرادات', (SELECT Credit FROM JournalEntryLines WHERE JournalEntryId = @je AND AccountId = 6), CAST(24000 AS DECIMAL(18,2)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'دائن الضريبة', (SELECT Credit FROM JournalEntryLines WHERE JournalEntryId = @je AND AccountId = 5), CAST(3360 AS DECIMAL(18,2)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'دائن التحميل', (SELECT Credit FROM JournalEntryLines WHERE JournalEntryId = @je AND AccountId = 7), CAST(1200 AS DECIMAL(18,2)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'لا سطر ذمم في النقدي', (SELECT COUNT(*) FROM JournalEntryLines WHERE JournalEntryId = @je AND AccountId = 4), 0);

PRINT N'--- 3) منع الترحيل المكرر ---';
BEGIN TRY EXEC sp_Sales_PostInvoice @InvoiceId = @inv, @UserId = 1; SET @err = 0; END TRY
BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
INSERT INTO #T (Name, Actual, Expected) VALUES (N'رفض إعادة الترحيل', @err, 51032);

PRINT N'--- 4) فاتورة جزئية لعميل فرعي (سطران لصنفين) ---';
EXEC sp_Sales_CreateInvoice @CustomerId = 2, @WarehouseId = 1, @InvoiceDate = '2026-09-30',
     @PaymentMethod = N'Partial', @AmountPaidNow = 5000, @UserId = 1, @NewInvoiceId = @inv OUTPUT;
EXEC sp_Sales_AddInvoiceLine @InvoiceId = @inv, @ItemId = 1, @PackagingLevelId = 1, @QuantityInLevel = 30, @UserId = 1;   -- 30 × 200
EXEC sp_Sales_AddInvoiceLine @InvoiceId = @inv, @ItemId = 2, @PackagingLevelId = 4, @QuantityInLevel = 5, @UserId = 1;    -- 5 شرنك × 6 × 500
EXEC sp_Sales_PostInvoice @InvoiceId = @inv, @UserId = 1;
SET @je = (SELECT JournalEntryId FROM SalesInvoices WHERE Id = @inv);
INSERT INTO #T (Name, Actual, Expected) VALUES (N'الإجمالي 6000 + 15000', (SELECT TotalAmount FROM SalesInvoices WHERE Id = @inv), CAST(21000 AS DECIMAL(18,2)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'مدين الصندوق = المدفوع', (SELECT Debit FROM JournalEntryLines WHERE JournalEntryId = @je AND AccountId = 1), CAST(5000 AS DECIMAL(18,2)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'مدين الذمم = المتبقي', (SELECT Debit FROM JournalEntryLines WHERE JournalEntryId = @je AND AccountId = 4), CAST(16000 AS DECIMAL(18,2)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'رصيد العميل الفرعي مستقل', (SELECT Balance FROM vw_CustomerBalances WHERE CustomerId = 2), CAST(16000 AS DECIMAL(38,2)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'رصيد الوكيل صفر (نقدي)', (SELECT Balance FROM vw_CustomerBalances WHERE CustomerId = 1), CAST(0 AS DECIMAL(38,2)));

INSERT INTO Vouchers (VoucherNumber, VoucherType, PartyType, PartyId, Amount, PaymentMethod, VoucherDate, CreatedByUserId)
VALUES (N'RV-1', N'Receipt', N'Customer', 2, 6000, N'Cash', '2026-10-01', 1);
INSERT INTO #T (Name, Actual, Expected) VALUES (N'الرصيد بعد سند القبض', (SELECT Balance FROM vw_CustomerBalances WHERE CustomerId = 2), CAST(10000 AS DECIMAL(38,2)));

PRINT N'--- 5) دفع جزئي بمبلغ غير منطقي ---';
EXEC sp_Sales_CreateInvoice @CustomerId = 3, @WarehouseId = 1, @InvoiceDate = '2026-09-30',
     @PaymentMethod = N'Partial', @AmountPaidNow = 999999, @UserId = 1, @NewInvoiceId = @inv OUTPUT;
EXEC sp_Sales_AddInvoiceLine @InvoiceId = @inv, @ItemId = 1, @PackagingLevelId = 1, @QuantityInLevel = 1, @UserId = 1;
BEGIN TRY EXEC sp_Sales_PostInvoice @InvoiceId = @inv, @UserId = 1; SET @err = 0; END TRY
BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
INSERT INTO #T (Name, Actual, Expected) VALUES (N'رفض دفع جزئي ≥ الإجمالي', @err, 51034);
EXEC sp_Sales_DeleteDraftInvoice @InvoiceId = @inv, @UserId = 1;
INSERT INTO #T (Name, Actual, Expected) VALUES (N'حذف المسودة', (SELECT COUNT(*) FROM SalesInvoices WHERE Id = @inv), 0);

PRINT N'--- 6) رصيد غير كافٍ ← لا شيء يُسجَّل (ذرّية) ---';
DECLARE @stBefore INT = (SELECT COUNT(*) FROM StockTransactions), @jeBefore INT = (SELECT COUNT(*) FROM JournalEntries);
EXEC sp_Sales_CreateInvoice @CustomerId = 3, @WarehouseId = 1, @InvoiceDate = '2026-09-30',
     @PaymentMethod = N'Credit', @UserId = 1, @NewInvoiceId = @inv OUTPUT;
EXEC sp_Sales_AddInvoiceLine @InvoiceId = @inv, @ItemId = 2, @PackagingLevelId = 3, @QuantityInLevel = 10, @UserId = 1;   -- متاح
EXEC sp_Sales_AddInvoiceLine @InvoiceId = @inv, @ItemId = 1, @PackagingLevelId = 2, @QuantityInLevel = 100, @UserId = 1;  -- 1200 > 980
BEGIN TRY EXEC sp_Sales_PostInvoice @InvoiceId = @inv, @UserId = 1; SET @err = 0; END TRY
BEGIN CATCH SET @err = ERROR_NUMBER(); PRINT N'  رسالة: ' + ERROR_MESSAGE(); END CATCH;
INSERT INTO #T (Name, Actual, Expected) VALUES (N'رفض الرصيد غير الكافي', @err, 51036);
INSERT INTO #T (Name, Actual, Expected) VALUES (N'لم تُسجَّل أي حركة مخزون', (SELECT COUNT(*) FROM StockTransactions), @stBefore);
INSERT INTO #T (Name, Actual, Expected) VALUES (N'لم يُسجَّل أي قيد', (SELECT COUNT(*) FROM JournalEntries), @jeBefore);
INSERT INTO #T (Name, Actual, Expected) VALUES (N'الفاتورة بقيت مسودة', (SELECT Status FROM SalesInvoices WHERE Id = @inv), CAST(N'Draft' AS NVARCHAR(20)));
EXEC sp_Sales_DeleteDraftInvoice @InvoiceId = @inv, @UserId = 1;

PRINT N'--- 7) تشغيلة محددة يدويًا ---';
EXEC sp_Sales_CreateInvoice @CustomerId = 3, @WarehouseId = 1, @InvoiceDate = '2026-09-30',
     @PaymentMethod = N'Credit', @UserId = 1, @NewInvoiceId = @inv OUTPUT;
BEGIN TRY EXEC sp_Sales_AddInvoiceLine @InvoiceId = @inv, @ItemId = 1, @PackagingLevelId = 1, @QuantityInLevel = 1, @BatchId = 3, @UserId = 1; SET @err = 0; END TRY
BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
INSERT INTO #T (Name, Actual, Expected) VALUES (N'رفض تشغيلة تخص صنفًا آخر', @err, 51016);
BEGIN TRY EXEC sp_Sales_AddInvoiceLine @InvoiceId = @inv, @ItemId = 1, @PackagingLevelId = 4, @QuantityInLevel = 1, @UserId = 1; SET @err = 0; END TRY
BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
INSERT INTO #T (Name, Actual, Expected) VALUES (N'رفض وحدة تعبئة تخص صنفًا آخر', @err, 51015);
EXEC sp_Sales_AddInvoiceLine @InvoiceId = @inv, @ItemId = 1, @PackagingLevelId = 1, @QuantityInLevel = 50, @BatchId = 1, @UnitPrice = 240, @UserId = 1;
EXEC sp_Sales_PostInvoice @InvoiceId = @inv, @UserId = 1;
INSERT INTO #T (Name, Actual, Expected) VALUES (N'السعر اليدوي محترم', (SELECT TotalAmount FROM SalesInvoices WHERE Id = @inv), CAST(12000 AS DECIMAL(18,2)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'الخصم من التشغيلة المحددة', (SELECT SUM(QuantityBaseUnits) FROM StockTransactions WHERE ReferenceTable = N'SalesInvoices' AND ReferenceId = @inv AND BatchId = 1), CAST(-50 AS DECIMAL(18,3)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'آجل ← كامل المبلغ ذمم', (SELECT Balance FROM vw_CustomerBalances WHERE CustomerId = 3), CAST(12000 AS DECIMAL(38,2)));

PRINT N'--- 8) المبيعات المجانية ---';
BEGIN TRY EXEC sp_Sales_CreateInvoice @CustomerId = 3, @WarehouseId = 1, @InvoiceDate = '2026-09-30',
     @PaymentMethod = N'Cash', @IsFreeSale = 1, @UserId = 1; SET @err = 0; END TRY
BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
INSERT INTO #T (Name, Actual, Expected) VALUES (N'المجانية تتطلب جهة مستفيدة', @err, 51005);
SET @jeBefore = (SELECT COUNT(*) FROM JournalEntries);
EXEC sp_Sales_CreateInvoice @CustomerId = 3, @WarehouseId = 1, @InvoiceDate = '2026-09-30',
     @PaymentMethod = N'Cash', @IsFreeSale = 1, @FreeSaleRecipient = N'جامع البصرة الكبير',
     @TaxEnabled = 1, @LoadingSuppliesEnabled = 1, @UserId = 1, @NewInvoiceId = @inv OUTPUT;
EXEC sp_Sales_AddInvoiceLine @InvoiceId = @inv, @ItemId = 2, @PackagingLevelId = 4, @QuantityInLevel = 10, @UserId = 1;
EXEC sp_Sales_PostInvoice @InvoiceId = @inv, @UserId = 1;
INSERT INTO #T (Name, Actual, Expected) VALUES (N'إجمالي المجانية صفر', (SELECT TotalAmount FROM SalesInvoices WHERE Id = @inv), CAST(0 AS DECIMAL(18,2)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'لا قيد مالي للمجانية', (SELECT COUNT(*) FROM JournalEntries), @jeBefore);
INSERT INTO #T (Name, Actual, Expected) VALUES (N'حركة FreeIssue باسم المستفيد',
     (SELECT FreeIssueRecipient FROM StockTransactions WHERE ReferenceTable = N'SalesInvoices' AND ReferenceId = @inv AND TransactionType = N'FreeIssue'),
     CAST(N'جامع البصرة الكبير' AS NVARCHAR(200)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'خصم 60 قطعة', (SELECT SUM(QuantityBaseUnits) FROM StockTransactions WHERE ReferenceTable = N'SalesInvoices' AND ReferenceId = @inv), CAST(-60 AS DECIMAL(18,3)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'رصيد العميل لم يتأثر', (SELECT Balance FROM vw_CustomerBalances WHERE CustomerId = 3), CAST(12000 AS DECIMAL(38,2)));

PRINT N'--- 9) البيع من الكاش فان ← محفظة المندوب ---';
EXEC sp_Sales_CreateInvoice @CustomerId = 3, @WarehouseId = 2, @InvoiceDate = '2026-09-30',
     @PaymentMethod = N'Cash', @UserId = 1, @NewInvoiceId = @inv OUTPUT;
INSERT INTO #T (Name, Actual, Expected) VALUES (N'المندوب يُحدَّد تلقائيًا من مالك الكاش فان', (SELECT SalesRepEmployeeId FROM SalesInvoices WHERE Id = @inv), 2);
EXEC sp_Sales_AddInvoiceLine @InvoiceId = @inv, @ItemId = 1, @PackagingLevelId = 2, @QuantityInLevel = 2, @UserId = 1;    -- 24 × 250
EXEC sp_Sales_PostInvoice @InvoiceId = @inv, @UserId = 1;
SET @je = (SELECT JournalEntryId FROM SalesInvoices WHERE Id = @inv);
INSERT INTO #T (Name, Actual, Expected) VALUES (N'حركة RepSale', (SELECT MIN(TransactionType) FROM StockTransactions WHERE ReferenceTable = N'SalesInvoices' AND ReferenceId = @inv), CAST(N'RepSale' AS NVARCHAR(30)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'مدين عهدة المندوب', (SELECT Debit FROM JournalEntryLines WHERE JournalEntryId = @je AND AccountId = 3), CAST(6000 AS DECIMAL(18,2)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'محفظة المندوب', (SELECT SUM(AmountIn - AmountOut) FROM RepWalletTransactions WHERE EmployeeId = 2), CAST(6000 AS DECIMAL(38,2)));

PRINT N'--- 10) الصلاحيات وقواعد الربط ---';
EXEC sp_Sales_CreateInvoice @CustomerId = 3, @WarehouseId = 1, @InvoiceDate = '2026-09-30',
     @PaymentMethod = N'Electronic', @UserId = 2, @NewInvoiceId = @inv OUTPUT;           -- الموظف يُنشئ
EXEC sp_Sales_AddInvoiceLine @InvoiceId = @inv, @ItemId = 1, @PackagingLevelId = 1, @QuantityInLevel = 4, @UserId = 2;
BEGIN TRY EXEC sp_Sales_PostInvoice @InvoiceId = @inv, @UserId = 2; SET @err = 0; END TRY
BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
INSERT INTO #T (Name, Actual, Expected) VALUES (N'موظف بلا صلاحية ترحيل يُرفض', @err, 51030);
SET @stBefore = (SELECT COUNT(*) FROM StockTransactions);
BEGIN TRY EXEC sp_Sales_PostInvoice @InvoiceId = @inv, @UserId = 1; SET @err = 0; END TRY
BEGIN CATCH SET @err = ERROR_NUMBER(); PRINT N'  رسالة: ' + ERROR_MESSAGE(); END CATCH;
INSERT INTO #T (Name, Actual, Expected) VALUES (N'قاعدة ربط ناقصة توقف الترحيل', @err, 51040);
INSERT INTO #T (Name, Actual, Expected) VALUES (N'والمخزون لم يُخصم (تراجع كامل)', (SELECT COUNT(*) FROM StockTransactions), @stBefore);
BEGIN TRY EXEC sp_Sales_CreateInvoice @CustomerId = 3, @WarehouseId = 3, @InvoiceDate = '2026-09-30', @PaymentMethod = N'Cash', @UserId = 1; SET @err = 0; END TRY
BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
INSERT INTO #T (Name, Actual, Expected) VALUES (N'رفض البيع من مخزن التالف', @err, 51004);

PRINT N'--- 11) الأرصدة النهائية ---';
INSERT INTO #T (Name, Actual, Expected) VALUES (N'رصيد ماء 500 في المخزن الرئيسي (1100 - 120 - 30 - 50)',
     (SELECT SUM(QuantityBaseUnits) FROM vw_StockBalance WHERE ItemId = 1 AND WarehouseId = 1), CAST(900 AS DECIMAL(38,3)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'رصيد الكاش فان (240 - 24)',
     (SELECT SUM(QuantityBaseUnits) FROM vw_StockBalance WHERE ItemId = 1 AND WarehouseId = 2), CAST(216 AS DECIMAL(38,3)));
INSERT INTO #T (Name, Actual, Expected) VALUES (N'كل القيود الآلية متوازنة',
     (SELECT COUNT(*) FROM (SELECT JournalEntryId FROM JournalEntryLines GROUP BY JournalEntryId HAVING SUM(Debit) <> SUM(Credit)) x), 0);

GO

DECLARE @n INT = (SELECT COUNT(*) FROM #T),
        @f INT = (SELECT COUNT(*) FROM #T WHERE NOT ((Actual IS NULL AND Expected IS NULL) OR Actual = Expected));
DECLARE @line NVARCHAR(600), @c CURSOR;
SET @c = CURSOR FAST_FORWARD FOR
    SELECT CASE WHEN (Actual IS NULL AND Expected IS NULL) OR Actual = Expected THEN N'  ✓ ' + Name
                ELSE N'  ✗ ' + Name + N'  | المتوقع=' + ISNULL(CAST(Expected AS NVARCHAR(100)), N'NULL')
                     + N'  الفعلي=' + ISNULL(CAST(Actual AS NVARCHAR(100)), N'NULL') END
    FROM #T ORDER BY Seq;
OPEN @c; FETCH NEXT FROM @c INTO @line;
WHILE @@FETCH_STATUS = 0 BEGIN PRINT @line; FETCH NEXT FROM @c INTO @line; END;
PRINT N'';
IF @f > 0
BEGIN
    DECLARE @m NVARCHAR(200) = N'❌ فشل ' + CAST(@f AS NVARCHAR(10)) + N' من ' + CAST(@n AS NVARCHAR(10)) + N' فحصًا';
    THROW 59999, @m, 1;
END;
PRINT N'✅ نجحت جميع الفحوصات (' + CAST(@n AS NVARCHAR(10)) + N')';
GO
