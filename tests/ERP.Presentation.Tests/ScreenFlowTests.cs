using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Finance;
using ERP.Presentation.ViewModels.Production;
using ERP.Presentation.ViewModels.Sales;
using ERP.Presentation.ViewModels.Settings;
using ERP.Presentation.ViewModels.Shell;
using ERP.Presentation.ViewModels.Suppliers;
using ERP.Presentation.ViewModels.Warehouse;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// اختبارات "تشغيل الشاشة فعليًا": نفس الـ ViewModels التي تربطها نوافذ WPF،
/// تُقاد هنا كما يفعل المستخدم (اختيار، كتابة، ضغط أزرار) على SQL Server حقيقي.
/// </summary>
[Collection("app")]
public class ScreenFlowTests
{
    private readonly AppFixture _f;
    public ScreenFlowTests(AppFixture f) => _f = f;

    private static async Task Open(ModuleViewModel module, SectionViewModel section)
    {
        module.SelectedTab = section;
        await module.IdleAsync();
        await module.LastActivation;
        await section.IdleAsync();
    }

    [Fact]
    public async Task Login_rejects_wrong_password_and_shell_shows_permitted_modules()
    {
        var nav = new RecordingNavigator();
        var login = new LoginViewModel(new AuthService(_f.ControlConnection), new RecordingDialogs(), nav) { Username = AppFixture.AdminUser };
        await login.LoginCommand.ExecuteAsync("wrong-password");
        Assert.Equal("اسم المستخدم أو كلمة المرور غير صحيحة", login.ErrorMessage);
        Assert.Null(nav.ProjectSelection);

        var (shell, _) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        Assert.Equal(9, shell.NavItems.Count);
        Assert.IsType<DashboardViewModel>(shell.CurrentModule);
        Assert.IsType<ERP.Presentation.ViewModels.Reps.RepsModuleViewModel>(shell.Open<object>(ModuleCode.Reps));
        Assert.IsType<ERP.Presentation.ViewModels.Production.ProductionModuleViewModel>(shell.Open<object>(ModuleCode.Production));
        Assert.IsType<SalesModuleViewModel>(shell.Open<object>(ModuleCode.Sales));
        Assert.Same(shell.CurrentModule, shell.Open<object>(ModuleCode.Sales));   // تُنشأ مرة واحدة

        var (clerkShell, _) = await _f.LoginAsync(AppFixture.ClerkUser, AppFixture.ClerkPassword);
        Assert.Equal(new[] { ModuleCode.Sales }, clerkShell.NavItems.Select(n => n.ModuleCode));
    }

    /// <summary>البند 5: فاتورة تجريبية ← ترحيل ← تظهر في كشف الحساب ← تُخصم من المخزون.</summary>
    [Fact]
    public async Task Sales_invoice_screen_end_to_end()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var sales = shell.Open<SalesModuleViewModel>(ModuleCode.Sales);
        Assert.IsType<HomeSectionViewModel>(sales.SelectedTab);
        Assert.Equal(9, sales.Home.Sections.Count());                 // + صندوقي + تأمينات العملاء + مرتجع زبون

        // الرصيد قبل البيع من شاشة الرصيد الحالي
        var warehouse = shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
        var stock = warehouse.Section<CurrentStockSectionViewModel>();
        await Open(warehouse, stock);
        var before = stock.Rows.Where(r => r.ItemCode == "W500" && r.WarehouseName == "مخزن المنتج التام").Sum(r => r.QuantityBaseUnits);

        shell.Open<SalesModuleViewModel>(ModuleCode.Sales);
        var inv = sales.Invoice;
        sales.Home.OpenSectionCommand.Execute(inv);
        await sales.IdleAsync();
        Assert.Same(inv, sales.SelectedTab);

        // اختيار العميل يعرض نوع التسعير تلقائيًا
        inv.Customer = inv.Customers.Single(c => c.Id == _f.SubCustomerId);
        await inv.IdleAsync();
        Assert.Contains("عميل فرعي", inv.PricingTypeLabel);
        Assert.Contains("وكيل الزبير", inv.PricingTypeLabel);
        inv.Warehouse = inv.Warehouses.Single(w => w.Id == _f.MainWarehouseId);

        // السطر: الصنف ← وحدة البيع ← السعر المقترح من الخدمة
        inv.LineItem = inv.ItemsLookup.Single(i => i.Id == _f.WaterItemId);
        await inv.IdleAsync();
        Assert.Equal("كارتون", inv.LineLevel!.LevelName);        // أكبر وحدة أولًا
        Assert.Equal(2400m, inv.LinePrice);                        // 12 × 200 سعر الوكيل الأب
        Assert.NotNull(inv.LineAvailable);
        inv.LineQuantity = 5;
        await inv.AddLineCommand.ExecuteAsync();
        await inv.IdleAsync();
        Assert.Single(inv.Lines);

        inv.LoadingEnabled = true;
        inv.TaxEnabled = true;
        inv.PaymentMethod = inv.PaymentOptions.Single(o => o.Value == InvoicePaymentMethod.Credit);
        Assert.Equal(12000m, inv.SubTotal);
        Assert.Equal(600m, inv.LoadingAmount);                     // 60 قطعة × 10
        Assert.Equal(1680m, inv.TaxAmount);
        Assert.Equal(14280m, inv.Total);
        Assert.Equal(14280m, inv.AmountDue);

        await inv.PostCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.NotNull(inv.LastPosted);
        var number = inv.LastPosted!.InvoiceNumber;
        Assert.Equal(14280m, inv.LastPosted.TotalAmount);
        Assert.NotNull(inv.LastPosted.JournalEntryId);
        Assert.Empty(inv.Lines);                                   // الشاشة جاهزة لفاتورة جديدة

        // عرض الطباعة بعد الترحيل: رقم الفاتورة الفعلي، بلا ختم مسودة، والمجاميع كما على الشاشة
        var printed = Assert.Single(dialogs.Reports);
        Assert.Equal("فاتورة مبيعات", printed.Title);
        Assert.Null(printed.Stamp);
        Assert.Contains(printed.HeaderFields, f => f.Label == "رقم الفاتورة" && f.Value == number);
        Assert.Contains(printed.HeaderFields, f => f.Label == "العميل" && f.Value == "محل أبو حيدر");
        Assert.Equal(7, printed.Columns.Count);
        Assert.Equal(new[] { "1", "ماء 500 مل (W500)", "كارتون", "5", "60", "2,400", "12,000" }, printed.Rows.Single());
        Assert.Contains(printed.Totals, t => t.Label == "الإجمالي" && t.Value == "14,280 د.ع" && t.Emphasis);
        Assert.Contains(printed.Totals, t => t.Label == "رسوم التحميل" && t.Value == "600 د.ع");

        // كشف حساب العميل يُظهر الفاتورة والرصيد
        await Open(sales, sales.Statement);
        sales.Statement.Customer = sales.Statement.Customers.Single(c => c.Id == _f.SubCustomerId);
        await sales.Statement.IdleAsync();
        var row = Assert.Single(sales.Statement.Rows, r => r.DocNumber == number);
        Assert.Equal("فاتورة مبيعات", row.TxType);
        Assert.Equal(14280m, row.Debit);
        Assert.True(sales.Statement.Balance >= 14280m);
        Assert.Contains(sales.Statement.Balances, b => b.CustomerId == _f.SubCustomerId && b.Balance == sales.Statement.Balance);
        sales.Statement.PrintCommand.Execute(null);
        var stmt = dialogs.Reports.Last();
        Assert.Equal("كشف حساب عميل", stmt.Title);
        Assert.Equal(sales.Statement.Rows.Count, stmt.Rows.Count);
        Assert.Contains(stmt.Rows, r => r[2] == number && r[4] == "14,280");

        // الفواتير بالمدفوع والمتبقي والحالة، وإجمالي الدين في أعلى الكشف
        var invoiceRow = Assert.Single(sales.Statement.Invoices, i => i.InvoiceNumber == number);
        Assert.Equal(14280m, invoiceRow.Total);
        Assert.Equal(invoiceRow.Total - invoiceRow.Paid, invoiceRow.Remaining);
        Assert.Contains("إجمالي الدين", sales.Statement.DebtHeadline);
        sales.Statement.PrintInvoicesCommand.Execute(null);
        Assert.Equal("كشف فواتير عميل (المدفوع والمتبقي)", dialogs.Reports.Last().Title);

        // المخزون خُصم 60 قطعة (يُحدَّث تلقائيًا عند فتح التبويب)
        shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
        await Open(warehouse, warehouse.Home.Sections.OfType<ItemsSectionViewModel>().Single());
        await Open(warehouse, stock);
        var after = stock.Rows.Where(r => r.ItemCode == "W500" && r.WarehouseName == "مخزن المنتج التام").Sum(r => r.QuantityBaseUnits);
        Assert.Equal(before - 60, after);

        // القيد المحاسبي من العقل المالي متوازن ومرحّل
        await using var db = _f.NewDb();
        var je = await db.JournalEntries.Include(j => j.Lines).SingleAsync(j => j.Id == inv.LastPosted.JournalEntryId);
        Assert.True(je.IsPosted && je.IsBalanced);

        // وتظهر في قائمة الفواتير كمرحّلة
        await Open(sales, sales.InvoiceList);
        Assert.Contains(sales.InvoiceList.Rows, r => r.InvoiceNumber == number && r.Status == "Posted");
        // إعادة طباعة الفاتورة المرحّلة من القائمة (من قاعدة البيانات)
        await sales.InvoiceList.PrintCommand.ExecuteAsync(sales.InvoiceList.Rows.Single(r => r.InvoiceNumber == number));
        Assert.Contains(dialogs.Reports.Last().Totals, t => t.Label == "الإجمالي" && t.Value == "14,280 د.ع");
        Assert.Contains(dialogs.Reports.Last().Totals, t => t.Label == "الإجمالي كتابةً" && t.Value.Contains("أربعة عشر ألف ومئتان وثمانون"));
        Assert.Empty(_f.Unhandled);
    }

    [Fact]
    public async Task Direct_sale_uses_finished_goods_with_stock_and_pack_filter_and_controls_screens_open()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var sales = shell.Open<SalesModuleViewModel>(ModuleCode.Sales);
        var inv = sales.Invoice;
        await Open(sales, inv);

        Assert.Equal(SaleMode.Direct, inv.Mode);
        Assert.NotEmpty(inv.Warehouses);
        Assert.All(inv.Warehouses, w => Assert.Equal(WarehouseType.FinishedGoods, w.WarehouseType));
        Assert.NotNull(inv.Warehouse);
        Assert.Contains(inv.ItemsLookup, i => i.Id == _f.WaterItemId);
        Assert.Contains("كارتون", inv.PackFilters);
        inv.PackFilter = "كارتون";
        Assert.Contains(inv.ItemsLookup, i => i.Id == _f.WaterItemId);
        inv.LineItem = inv.ItemsLookup.Single(i => i.Id == _f.WaterItemId);
        await inv.IdleAsync();
        Assert.Contains("كارتون", inv.LineAvailableText);              // المتاح بالعبوات أيضًا
        Assert.Contains(inv.SaleModes, m => m.Value == SaleMode.RawMaterials);   // المدير يملك صلاحية بيع المواد الأولية

        // شاشات الضوابط: سجل الحركات وإغلاق الشهر تُفتح للمدير
        var settings = shell.Open<SettingsModuleViewModel>(ModuleCode.SystemSettings);
        var audit = settings.Section<ERP.Presentation.ViewModels.Controls.AuditLogSectionViewModel>();
        await Open(settings, audit);
        Assert.True(audit.Allowed);
        var fin = shell.Open<FinanceModuleViewModel>(ModuleCode.Finance);
        await Open(fin, fin.PeriodLock);
        Assert.Contains("لا توجد فترة مقفلة", fin.PeriodLock.LockText);
        Assert.Empty(dialogs.Errors);
    }

    [Fact]
    public async Task Purchase_invoice_screen_buys_in_cartons_and_reorder_screen_loads()
    {
        int capId;
        await using (var db = _f.NewDb())
        {
            var cap = new Item { ItemCode = "CAP-SCR", ItemName = "سدادة شاشة الشراء", SourcingMethod = SourcingMethod.Purchased, LeadTimeDays = 10 };
            db.Items.Add(cap);
            await db.SaveChangesAsync();
            var piece = new ItemPackagingLevel { ItemId = cap.Id, LevelName = "قطعة", ContainsQuantity = 1, EquivalentBaseUnits = 1 };
            db.ItemPackagingLevels.Add(piece);
            await db.SaveChangesAsync();
            db.ItemPackagingLevels.Add(new ItemPackagingLevel { ItemId = cap.Id, LevelName = "كرتون", ParentLevelId = piece.Id, ContainsQuantity = 500, EquivalentBaseUnits = 500 });
            await db.SaveChangesAsync();
            capId = cap.Id;
        }
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var sup = shell.Open<SuppliersModuleViewModel>(ModuleCode.Suppliers);
        var inv = sup.PurchaseInvoice;
        await Open(sup, inv);

        inv.Supplier = inv.SuppliersLookup.Single(x => x.Id == _f.SupplierId);
        inv.Warehouse = inv.Warehouses.Single(w => w.Id == _f.MainWarehouseId);
        var line = inv.Lines[0];
        line.Material = inv.ItemsLookup.Single(i => i.Id == capId);
        await line.LoadUnits;
        Assert.Equal("كرتون", line.Unit!.Label);                     // الوحدة الأكبر افتراضيًا
        line.Quantity = 2;
        line.UnitPrice = 10_000;
        Assert.Equal(1000m, line.Pieces);
        Assert.Equal(20m, line.CostPerPiece);
        Assert.Equal(20_000m, inv.Total);
        inv.PaidNow = 5_000;
        Assert.Equal(15_000m, inv.Remaining);
        inv.PaidNow = 0;                                              // لا حركة نقدية: صناديق الاختبارات الأخرى مشتركة
        await inv.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.NotNull(inv.LastReceiptId);
        Assert.Single(inv.Lines);                                     // الشاشة جاهزة لفاتورة جديدة

        await using (var db = _f.NewDb())
        {
            Assert.Equal(20m, (await db.Items.FirstAsync(i => i.Id == capId)).CostPrice);
            Assert.Equal(1000m, await db.StockTransactions.Where(t => t.ItemId == capId).SumAsync(t => t.QuantityBaseUnits));
        }
        await Open(sup, sup.Reorder);
        Assert.Contains(sup.Reorder.Rows, r => r.ItemId == capId && r.LeadTimeDays == 10);
    }

    [Fact]
    public async Task Stocktake_sheet_converts_big_units_to_pieces_and_production_screens_open()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var wh = shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
        var st = wh.Stocktake;
        await Open(wh, st);
        st.Warehouse = st.Warehouses.Single(w => w.Id == _f.MainWarehouseId);
        await st.IdleAsync();
        var line = st.Lines.Single(l => l.Row.ItemId == _f.WaterItemId);
        Assert.Equal("كارتون", line.Cells[0].Unit);                 // الوحدة الأكبر أولًا
        line.Cells[0].Count = 2;                                      // 2 كرتون × 12
        line.Cells.Single(c => c.PiecesPerUnit == 1).Count = 5;
        Assert.True(line.Counted);
        Assert.Equal(29m, line.CountedPieces);
        Assert.Equal(29m - line.Row.SystemQuantity, line.Variance);
        st.PrintSheetCommand.Execute(null);
        Assert.StartsWith("ورقة جرد", dialogs.Reports.Last().Title);
        Assert.DoesNotContain(dialogs.Reports.Last().Rows, r => r.Contains(line.Row.SystemQuantity.ToString("N0")) && r[0] == line.Row.ItemCode && r[3] != "");

        var prod = shell.Open<ProductionModuleViewModel>(ModuleCode.Production);
        await Open(prod, prod.Daily);
        prod.Daily.Lines[0].Packs = 0;
        await prod.Daily.SaveCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("أضف منتجًا"));
    }

    [Fact]
    public async Task Free_sale_requires_recipient_and_skips_journal()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var sales = shell.Open<SalesModuleViewModel>(ModuleCode.Sales);
        var inv = sales.Invoice;
        await Open(sales, inv);

        inv.Customer = inv.Customers.Single(c => c.Id == _f.DirectId);
        inv.Warehouse = inv.Warehouses.Single(w => w.Id == _f.MainWarehouseId);
        inv.LineItem = inv.ItemsLookup.Single(i => i.Id == _f.WaterItemId);
        await inv.IdleAsync();
        Assert.Equal(3000m, inv.LinePrice);                        // مباشر: 12 × 250
        await inv.AddLineCommand.ExecuteAsync();
        inv.IsFreeSale = true;
        Assert.Equal(0m, inv.Total);
        Assert.NotEmpty(inv.FreeSaleNotice);

        await inv.PostCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("الجهة المستفيدة"));
        Assert.Null(inv.LastPosted);

        dialogs.Errors.Clear();
        inv.FreeSaleRecipient = "جامع البصرة الكبير";
        await inv.PostCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Null(inv.LastPosted!.JournalEntryId);

        await using var db = _f.NewDb();
        var tx = await db.StockTransactions.SingleAsync(t => t.ReferenceTable == "SalesInvoices" && t.ReferenceId == inv.LastPosted.InvoiceId);
        Assert.Equal(StockTransactionType.FreeIssue, tx.TransactionType);
        Assert.Equal("جامع البصرة الكبير", tx.FreeIssueRecipient);
        Assert.Equal(-12m, tx.QuantityBaseUnits);
    }

    [Fact]
    public async Task Draft_saved_listed_reopened_repriced_and_posted()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var sales = shell.Open<SalesModuleViewModel>(ModuleCode.Sales);
        var inv = sales.Invoice;
        await Open(sales, inv);

        inv.Customer = inv.Customers.Single(c => c.Id == _f.DirectId);
        inv.Warehouse = inv.Warehouses.Single(w => w.Id == _f.MainWarehouseId);
        inv.LineItem = inv.ItemsLookup.Single(i => i.Id == _f.WaterItemId);
        await inv.IdleAsync();
        inv.LineBatch = inv.BatchOptions.Single(b => b.BatchId == _f.EarlyBatchId);
        await inv.AddLineCommand.ExecuteAsync();
        Assert.Equal(3000m, inv.Lines[0].UnitPrice);

        // تغيير العميل إلى وكيل يعيد تسعير السطر تلقائيًا
        inv.Customer = inv.Customers.Single(c => c.Id == _f.AgentId);
        await inv.IdleAsync();
        Assert.Equal(2400m, inv.Lines[0].UnitPrice);
        // إلغاء تسعير الوكيل يدويًا يعيد السعر العادي
        inv.UseAgentPricing = false;
        await inv.IdleAsync();
        Assert.Equal(3000m, inv.Lines[0].UnitPrice);
        inv.UseAgentPricing = true;
        await inv.IdleAsync();

        await inv.SaveDraftCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        var id = inv.InvoiceId!.Value;
        var number = inv.InvoiceNumber;

        inv.ResetForm();
        await Open(sales, sales.InvoiceList);
        sales.InvoiceList.CustomerFilter = sales.InvoiceList.Customers.Single(c => c.Id == _f.AgentId);
        await sales.InvoiceList.IdleAsync();
        var draftRow = Assert.Single(sales.InvoiceList.Rows, r => r.Id == id);
        Assert.Equal("Draft", draftRow.Status);

        await sales.InvoiceList.OpenCommand.ExecuteAsync(draftRow);
        await sales.IdleAsync();
        Assert.Same(inv, sales.SelectedTab);
        Assert.Equal(number, inv.InvoiceNumber);
        Assert.False(inv.IsReadOnly);
        var line = Assert.Single(inv.Lines);
        Assert.Equal(_f.EarlyBatchId, line.BatchId);

        line.QuantityInLevel = 2;                                   // تعديل الكمية في الجدول
        inv.PaymentMethod = inv.PaymentOptions.Single(o => o.Value == InvoicePaymentMethod.Partial);
        inv.AmountPaidNow = 1000;
        await inv.PostCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Equal(number, inv.LastPosted!.InvoiceNumber);       // نفس الرقم بعد التعديل
        Assert.Equal(4800m, inv.LastPosted.TotalAmount);
        Assert.Equal(3800m, inv.LastPosted.AmountDue);

        // فتح الفاتورة المرحّلة للعرض فقط
        await sales.OpenInvoiceAsync(id);
        Assert.True(inv.IsReadOnly);
        await inv.PostCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("مرحّلة"));
    }

    [Fact]
    public async Task Clerk_can_save_draft_but_cannot_post()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.ClerkUser, AppFixture.ClerkPassword);
        var sales = shell.Open<SalesModuleViewModel>(ModuleCode.Sales);
        Assert.False(sales.Invoice.CanPost);
        var inv = sales.Invoice;
        await Open(sales, inv);
        inv.Customer = inv.Customers.Single(c => c.Id == _f.DirectId);
        inv.Warehouse = inv.Warehouses.Single(w => w.Id == _f.MainWarehouseId);
        inv.LineItem = inv.ItemsLookup.Single(i => i.Id == _f.WaterItemId);
        await inv.IdleAsync();
        await inv.AddLineCommand.ExecuteAsync();
        await inv.SaveDraftCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        await inv.PostCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("صلاحية"));
    }

    [Fact]
    public async Task Crud_sections_customers_and_items()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var sales = shell.Open<SalesModuleViewModel>(ModuleCode.Sales);
        var customers = sales.Section<CustomersSectionViewModel>();
        await Open(sales, customers);

        await customers.NewCommand.ExecuteAsync();
        customers.Editor!.Name = "عميل فرعي جديد";
        customers.Editor.CustomerType = CustomerType.SubCustomer;
        await customers.SaveCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("ربطه بوكيل"));   // التحقق قبل الحفظ
        dialogs.Errors.Clear();
        customers.Editor.ParentAgentId = _f.AgentId;
        await customers.SaveCommand.ExecuteAsync();
        Assert.Null(customers.Editor);
        var added = Assert.Single(customers.Items, c => c.Name == "عميل فرعي جديد");
        Assert.Equal("وكيل الزبير", added.ParentAgent!.Name);

        customers.SearchText = "جديد";
        Assert.Single(customers.Items);
        await customers.EditCommand.ExecuteAsync(added);
        customers.Editor!.Phone = "07801112233";
        await customers.SaveCommand.ExecuteAsync();
        customers.SearchText = "0780111";
        Assert.Single(customers.Items);
        await customers.DeleteCommand.ExecuteAsync(customers.Items[0]);
        Assert.Empty(customers.Items);

        // صنف جديد يحصل تلقائيًا على مستوى "قطعة" قابل للبيع
        var wh = shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
        var items = wh.Section<ItemsSectionViewModel>();
        await Open(wh, items);
        await items.NewCommand.ExecuteAsync();
        items.Editor!.ItemCode = "W1500";
        items.Editor.ItemName = "ماء 1.5 لتر";
        items.Editor.SalePrice = 500;
        items.Editor.BarCode = "6260000000024";
        await items.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        await using var db = _f.NewDb();
        var newItem = await db.Items.SingleAsync(i => i.ItemCode == "W1500");
        Assert.True(await db.ItemPackagingLevels.AnyAsync(p => p.ItemId == newItem.Id && p.EquivalentBaseUnits == 1));

        // مستوى شرنك (6 قطع) يُحسب عدد قطعه تلقائيًا
        var pack = wh.Section<PackagingSectionViewModel>();
        await Open(wh, pack);
        pack.SelectedItem = pack.ItemsLookup.Single(i => i.Id == newItem.Id);
        await pack.IdleAsync();
        await pack.NewCommand.ExecuteAsync();
        pack.Editor!.LevelName = "شرنك";
        pack.Editor.ParentLevelId = pack.Items.Single().Id;
        pack.Editor.ContainsQuantity = 6;
        await pack.SaveCommand.ExecuteAsync();
        Assert.Equal(6m, pack.Items.Single(p => p.LevelName == "شرنك").EquivalentBaseUnits);

        // عبوة ثانية على المنتج نفسه تُمنع: الكارتون منتج مستقل بقائمة مواده ورصيده
        await pack.NewCommand.ExecuteAsync();
        pack.Editor!.LevelName = "كارتون";
        pack.Editor.ParentLevelId = pack.Items.Single(p => p.EquivalentBaseUnits == 1).Id;
        pack.Editor.ContainsQuantity = 12;
        await pack.SaveCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("كل عبوة منتج مستقل") && e.Contains("ماء 1.5 لتر كارتون"));
        Assert.DoesNotContain(pack.Items, p => p.LevelName == "كارتون");
        pack.CancelCommand.Execute(null);

        // حذف صنف له حركات يُرفض برسالة واضحة
        await items.DeleteCommand.ExecuteAsync(items.Items.Single(i => i.ItemCode == "W500"));
        Assert.Contains(dialogs.Errors, e => e.Contains("مرتبط بحركات"));
    }

    [Fact]
    public async Task Stock_adjustment_blocks_negative_balance()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var wh = shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
        await wh.IdleAsync();   // تبويبات المخازن تُضاف في الخلفية — لا نعدّد التبويبات أثناء إضافتها
        Assert.DoesNotContain(wh.Tabs, t => t is StockAdjustmentSectionViewModel);    // مخفية من الوحدة مؤقتًا
        var adj = wh.LegacyAdjustment;
        await Open(wh, adj);

        adj.AdjustItem = adj.ItemsLookup.Single(i => i.Id == _f.WaterItemId);
        adj.Warehouse = adj.Warehouses.Single(w => w.Id == _f.MainWarehouseId);
        await adj.IdleAsync();
        var available = adj.AvailableBalance!.Value;

        adj.Quantity = available + 1;
        adj.Reason = adj.ReasonOptions[1];
        await adj.SubmitCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("الرصيد غير كافٍ"));

        adj.Quantity = 3;
        await adj.SubmitCommand.ExecuteAsync();
        await adj.IdleAsync();
        Assert.Equal(available - 3, adj.AvailableBalance);
        Assert.Contains(adj.RecentAdjustments, r => r.Quantity == -3 && r.TypeLabel.Contains("داخل المخزن"));
    }

    [Fact]
    public async Task Finance_journal_balance_check_and_customer_receipt_voucher()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var fin = shell.Open<FinanceModuleViewModel>(ModuleCode.Finance);
        var je = fin.Section<JournalEntriesSectionViewModel>();
        await Open(fin, je);

        je.NewEntryCommand.Execute(null);
        je.NewLines[0].Account = je.Accounts.Single(a => a.AccountCode == "5101");
        je.NewLines[0].Debit = 500;
        je.NewLines[1].Account = je.Accounts.Single(a => a.AccountCode == "1101");
        je.NewLines[1].Credit = 400;
        Assert.False(je.IsBalanced);
        await je.PostCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("غير متوازن"));
        je.NewLines[1].Credit = 500;
        Assert.True(je.IsBalanced);
        await je.PostCommand.ExecuteAsync();
        Assert.False(je.IsComposing);
        Assert.Contains(je.Entries, e => e.TypeLabel == "يدوي" && e.Total == 500);

        // سند قبض من عميل يخفّض رصيده في كشف الحساب
        var v = fin.Section<VouchersSectionViewModel>();
        await Open(fin, v);
        v.PartyType = v.PartyTypes.Single(p => p.Value == VoucherPartyType.Customer);
        await v.IdleAsync();
        v.Party = v.Parties.Single(p => p.Id == _f.DirectId);
        v.Amount = 750;
        Assert.Equal("CashReceiptVoucher", v.MappingRule!.TransactionType);
        Assert.Contains("الصندوق", v.RulePreview);
        await v.SaveCommand.ExecuteAsync();
        Assert.DoesNotContain(dialogs.Errors, e => !e.Contains("غير متوازن"));

        var sales = shell.Open<SalesModuleViewModel>(ModuleCode.Sales);
        await Open(sales, sales.Statement);
        sales.Statement.Customer = sales.Statement.Customers.Single(c => c.Id == _f.DirectId);
        await sales.Statement.IdleAsync();
        Assert.Contains(sales.Statement.Rows, r => r.TxType == "سند قبض" && r.Credit == 750);
        // السند يظهر في الدفعات بتوزيعه (الأقدم أولًا) على فواتير العميل إن وُجدت
        var paymentRow = Assert.Single(sales.Statement.Payments, p => p.Amount == 750);
        Assert.Equal(750m, paymentRow.Allocated + paymentRow.Unallocated);
        Assert.Equal(sales.Statement.Invoices.Sum(i => i.Paid), sales.Statement.Invoices.Sum(i => i.Total) - sales.Statement.Invoices.Sum(i => i.Remaining));

        // طباعة السند (بالمبلغ كتابةً) والقيد
        await v.PrintCommand.ExecuteAsync(v.Vouchers.First(x => x.Amount == 750));
        var vr = dialogs.Reports.Last();
        Assert.Equal("سند قبض", vr.Title);
        Assert.Contains(vr.HeaderFields, f => f.Label == "استلمنا من" && f.Value == "زبون مباشر");
        Assert.Contains(vr.Totals, t => t.Value == "فقط سبعمئة وخمسون دينار عراقي لا غير");
        await je.PrintCommand.ExecuteAsync(je.Entries.First(e => e.Total == 500));
        Assert.Equal("قيد يومية", dialogs.Reports.Last().Title);
        Assert.Contains(dialogs.Reports.Last().Totals, t => t.Value == "متوازن ✓");

        var rules = fin.Section<MappingRulesSectionViewModel>();
        await Open(fin, rules);
        // القواعد الناقصة (مثل الدفع الإلكتروني) تُضاف تلقائيًا عند فتح المشروع
        Assert.Contains(rules.Items, r => r.TransactionType == "SalesInvoiceElectronic");
        Assert.Equal("كل القواعد المطلوبة معرّفة ✓", rules.MissingText);
    }

    [Fact]
    public async Task Purchase_order_and_goods_receipt_increase_stock_and_supplier_balance()
    {
        // أمر الشراء لمخزن المواد الأولية فقط
        int rawId;
        await using (var db = _f.NewDb())
        {
            var raw = await db.Warehouses.FirstOrDefaultAsync(w => w.WarehouseType == WarehouseType.RawMaterial && w.IsActive);
            if (raw is null)
            {
                raw = new Warehouse { BranchId = await db.Branches.Select(b => b.Id).FirstAsync(), Name = "مخزن المواد الأولية", WarehouseType = WarehouseType.RawMaterial };
                db.Warehouses.Add(raw);
                await db.SaveChangesAsync();
            }
            rawId = raw.Id;
        }
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var sup = shell.Open<SuppliersModuleViewModel>(ModuleCode.Suppliers);
        var po = sup.Section<PurchaseOrdersSectionViewModel>();
        await Open(sup, po);
        Assert.All(po.Warehouses, w => Assert.Equal(WarehouseType.RawMaterial, w.WarehouseType));

        po.NewOrderCommand.Execute(null);
        po.Supplier = po.SuppliersLookup.Single(s => s.Id == _f.SupplierId);
        po.Warehouse = po.Warehouses.Single(w => w.Id == rawId);
        po.Lines[0].LineItem = po.ItemsLookup.Single(i => i.Id == _f.WaterItemId);
        po.Lines[0].Quantity = 240;
        po.Lines[0].UnitCost = 90;
        Assert.Equal(21600m, po.Total);
        await po.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        var order = po.Orders.First();

        var gr = sup.Section<GoodsReceiptSectionViewModel>();
        await Open(sup, gr);
        gr.SelectedOrder = gr.OpenOrders.Single(o => o.Id == order.Id);
        await gr.IdleAsync();
        gr.FillRemainingCommand.Execute(null);
        Assert.Equal(240m, gr.Lines.Single().QuantityNow);
        await gr.ReceiveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Contains(gr.Receipts, r => r.PONumber == order.PONumber && r.Total == 21600m);
        Assert.DoesNotContain(gr.OpenOrders, o => o.Id == order.Id);   // اكتمل الاستلام
        await gr.PrintCommand.ExecuteAsync(gr.Receipts.First(r => r.PONumber == order.PONumber));
        Assert.Equal("محضر استلام بضاعة", dialogs.Reports.Last().Title);
        Assert.Contains(dialogs.Reports.Last().Totals, t => t.Value == "21,600 د.ع");
        await po.PrintCommand.ExecuteAsync(order);
        Assert.Equal("أمر شراء", dialogs.Reports.Last().Title);
        Assert.Contains(dialogs.Reports.Last().Rows, r => r[2] == "240" && r[5] == "240");

        var st = sup.Section<SupplierStatementSectionViewModel>();
        await Open(sup, st);
        st.Supplier = st.SuppliersLookup.Single(s => s.Id == _f.SupplierId);
        await st.IdleAsync();
        Assert.True(st.Balance >= 21600m);
    }

    [Fact]
    public async Task Settings_roles_matrix_saves_and_view_is_implied()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var settings = shell.Open<SettingsModuleViewModel>(ModuleCode.SystemSettings);
        var roles = settings.Section<RolesPermissionsSectionViewModel>();
        await Open(settings, roles);

        roles.NewRoleName = "مراقب الجودة";
        await roles.AddRoleCommand.ExecuteAsync();
        Assert.Equal("مراقب الجودة", roles.SelectedRole!.Name);
        var whRow = roles.Matrix.Single(m => m.ModuleCode == ModuleCode.Warehouse);
        whRow.CanAdd = true;                                        // بدون عرض — يُضاف تلقائيًا
        await roles.SaveCommand.ExecuteAsync();

        await using var db = _f.NewDb();
        var perm = await db.RolePermissions.SingleAsync(p => p.Role.Name == "مراقب الجودة" && p.ModuleCode == ModuleCode.Warehouse);
        Assert.True(perm.CanView && perm.CanAdd && !perm.CanDelete);
        Assert.Empty(dialogs.Errors);
    }

    [Fact]
    public async Task Dashboard_tiles_load()
    {
        var (shell, _) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var dash = Assert.IsType<DashboardViewModel>(shell.CurrentModule);
        await dash.IdleAsync();
        Assert.Equal(6, dash.Tiles.Count);                         // + تذكير النسخ الاحتياطي للمدير
        Assert.Contains(dash.Tiles, t => t.Title == "مبيعات اليوم");
        Assert.Contains(dash.Tiles, t => t.Title == "آخر نسخة احتياطية");

        var (clerkShell, _) = await _f.LoginAsync(AppFixture.ClerkUser, AppFixture.ClerkPassword);
        var clerkDash = new DashboardViewModel(clerkShell.Session, new RecordingDialogs());   // بلا صلاحية إعدادات النظام
        await clerkDash.IdleAsync();
        Assert.DoesNotContain(clerkDash.Tiles, t => t.Title == "آخر نسخة احتياطية");
    }

    /// <summary>
    /// لوحة كل وحدة تُحمَّل على SQL Server حقيقي برسمين يوميين على الأقل، ولوحة الإنتاج تضم المختبر؛
    /// وزر إظهار/إخفاء "القوائم الفرعية" يُحفظ لكل وحدة.
    /// </summary>
    [Fact]
    public async Task Every_module_dashboard_loads_with_more_charts_and_sections_toggle_persists()
    {
        var settings = Path.Combine(Path.GetTempPath(), $"ui-{Guid.NewGuid():N}.json");
        Environment.SetEnvironmentVariable("ERP_UI_SETTINGS", settings);
        UiPreferences.Reset();
        try
        {
            var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
            var dashboards = 0;
            foreach (var nav in shell.NavItems)
            {
                if (shell.Open<object>(nav.ModuleCode) is not ModuleViewModel m || m.Dashboard is null) continue;
                await m.Dashboard.LoadAsync();
                Assert.True(m.Dashboard.Error is null, $"{m.Title}: {m.Dashboard.Error}");
                Assert.True(m.Dashboard.Columns.Count >= 2, $"{m.Title}: رسم يومي واحد فقط");
                Assert.True(m.Dashboard.Ranks.Count >= 2, $"{m.Title}: ترتيب واحد فقط");
                dashboards++;
            }
            Assert.True(dashboards >= 6);

            var prod = shell.Open<ProductionModuleViewModel>(ModuleCode.Production);
            await prod.Dashboard!.LoadAsync();
            Assert.Contains(prod.Dashboard.Tiles, t => t.Title == "المختبر — نسبة النجاح");
            Assert.Contains(prod.Dashboard.Columns, c => c.Title.StartsWith("المختبر — نتائج الفحص اليومية"));
            Assert.Contains(prod.Dashboard.Ranks, r => r.Title.StartsWith("المختبر — الاختبارات الأكثر رسوبًا"));

            // إخفاء القوائم الفرعية في وحدة يُحفظ لها وحدها
            Assert.True(prod.Home.ShowSections);
            prod.Home.ToggleSectionsCommand.Execute(null);
            Assert.False(prod.Home.ShowSections);
            Assert.Equal("إظهار القوائم الفرعية", prod.Home.ToggleSectionsText);
            UiPreferences.Reset();
            Assert.False(new HomeSectionViewModel(prod).ShowSections);
            Assert.True(new HomeSectionViewModel(shell.Open<SalesModuleViewModel>(ModuleCode.Sales)).ShowSections);
            Assert.True(File.Exists(settings));
            Assert.Empty(dialogs.Errors);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ERP_UI_SETTINGS", null);
            UiPreferences.Reset();
            File.Delete(settings);
        }
    }

    /// <summary>فتح كل تبويب في كل وحدة يعمل على SQL Server حقيقي (يلتقط أخطاء ترجمة الاستعلامات).</summary>
    [Fact]
    public async Task Final_accounts_screens_load_and_expense_needs_an_amount()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var fin = shell.Open<FinanceModuleViewModel>(ModuleCode.Finance);

        var exp = fin.Expenses;
        await Open(fin, exp);
        Assert.Contains(exp.Categories, c => c.Name == "توسعة" && c.Kind == FinanceCategoryKind.NonOperating);
        exp.Category = exp.Categories.Single(c => c.Name == "توسعة");
        Assert.Contains("غير تشغيلي", exp.KindHint);
        await exp.SaveCommand.ExecuteAsync();                        // بلا مبلغ: لا حركة نقدية على الصناديق المشتركة
        Assert.Contains(dialogs.Errors, e => e.Contains("المبلغ"));

        Assert.NotNull(fin.FinalAccounts);
        await Open(fin, fin.FinalAccounts!);
        Assert.NotNull(fin.FinalAccounts!.Report);
        Assert.Equal("صافي ربح الشهر", fin.FinalAccounts.Lines.Last().Label);
        fin.FinalAccounts.PrintCommand.Execute(null);
        Assert.StartsWith("هامش كل منتج", dialogs.Reports.Last().Title);

        await Open(fin, fin.WorkingCapital!);
        Assert.NotNull(fin.WorkingCapital!.Snapshot);

        var sim = fin.CostSimulation!;
        await Open(fin, sim);
        Assert.True(sim.Results.Count > 0 || sim.StatusMessage!.Contains("وصفات"));   // قاعدة الشاشات قد تخلو من الوصفات
        var before = sim.Results.ToDictionary(r => r.ItemId, r => r.SimulatedMaterialCost);
        foreach (var m in sim.Materials) m.Proposed = m.Current * 2;
        await sim.SimulateCommand.ExecuteAsync();
        Assert.All(sim.Results, r => Assert.Equal(before[r.ItemId] * 2, r.SimulatedMaterialCost, 1));

        await Open(fin, fin.DailyCash);
        Assert.NotEmpty(fin.DailyCash.Boxes);
        fin.DailyCash.PrintCommand.Execute(null);
        Assert.StartsWith("التقرير اليومي للصناديق", dialogs.Reports.Last().Title);
    }

    [Fact]
    public async Task Every_section_of_every_module_loads()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var opened = 0;
        foreach (var nav in shell.NavItems)
        {
            if (shell.Open<object>(nav.ModuleCode) is not ModuleViewModel m) continue;
            foreach (var section in m.Tabs.OfType<SectionViewModel>().ToList())
            {
                await Open(m, section);
                await m.LastActivation;
                opened++;
            }
        }
        Assert.True(opened > 40, $"فُتح {opened} تبويب فقط");

        // لوحة كل قسم: مؤشرات + رسم نشاط يومي لآخر 14 يومًا (عدا الإعدادات)
        var dashboards = 0;
        foreach (var nav in shell.NavItems)
        {
            if (shell.Open<object>(nav.ModuleCode) is not ModuleViewModel { Dashboard: { } dash } m) continue;
            await m.IdleAsync();
            await dash.IdleAsync();
            Assert.True(dash.Error is null, $"{m.Title}: {dash.Error}");
            Assert.NotEmpty(dash.Tiles);
            Assert.True(dash.Columns.Count >= 2, $"{m.Title}: رسم يومي واحد فقط");
            foreach (var chart in dash.Columns)
            {
                Assert.Equal(14, chart.Categories.Count);
                Assert.Equal(DateTime.Today.ToString("dd/MM", System.Globalization.CultureInfo.InvariantCulture), chart.Categories[^1].Label);
                Assert.All(chart.Categories.SelectMany(c => c.Bars), b => Assert.InRange(b.Height, 0, 150));
            }
            Assert.True(m.Home.HasDashboard);
            dashboards++;
        }
        Assert.Equal(7, dashboards);

        // المبيعات المرحّلة اليوم تظهر في عمود اليوم وفي الترتيب
        var salesDash = shell.Open<SalesModuleViewModel>(ModuleCode.Sales).Dashboard!;
        await salesDash.RefreshCommand.ExecuteAsync();
        var todayBar = salesDash.Columns[0].Categories[^1].Bars.Single();
        await using var db = _f.NewDb();
        var todaySales = await db.SalesInvoices.Where(i => i.Status == DocumentStatus.Posted && !i.IsFreeSale && i.InvoiceDate == DateTime.Today)
                                               .SumAsync(i => (decimal?)i.TotalAmount) ?? 0;
        Assert.Equal(todaySales, todayBar.Value);
        if (todaySales > 0)
        {
            Assert.Equal(150d, salesDash.Columns[0].Categories.SelectMany(c => c.Bars).Max(b => b.Height));
            Assert.NotNull(salesDash.Columns[0].Categories[^1].ValueLabel);              // ملصق آخر يوم
            Assert.Contains(salesDash.Ranks[0].Bars, b => b.Label == "ماء 500 مل" && b.Ratio == 1);
        }
        Assert.Empty(dialogs.Errors);
        Assert.Empty(_f.Unhandled);
    }

    [Fact]
    public async Task Backup_section_backs_up_project_and_control_databases()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var settings = shell.Open<SettingsModuleViewModel>(ModuleCode.SystemSettings);
        var backup = settings.Section<BackupSectionViewModel>();
        await Open(settings, backup);
        Assert.False(string.IsNullOrWhiteSpace(backup.Folder));    // مجلد السيرفر الافتراضي
        Assert.Equal("ERP_ControlDB", backup.ControlDatabase);

        await backup.BackupCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Equal(2, backup.Log.Count(l => l.StartsWith("✓")));
        Assert.Contains(backup.History, h => h.DatabaseName == backup.ProjectDatabase && h.FilePath.EndsWith(".bak"));
        Assert.Contains(backup.History, h => h.DatabaseName == "ERP_ControlDB");
        Assert.False(backup.IsOverdue);
        Assert.StartsWith("✓", backup.LastBackupText);

        // الاسترداد: بلا ملف رسالة، ونسخة قاعدة التحكم مرفوضة لقاعدة المشروع دون مساسها
        await backup.RestoreCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("اختر ملف النسخة"));
        dialogs.Errors.Clear();
        backup.SelectedHistory = backup.History.First(h => h.DatabaseName == backup.ProjectDatabase);
        Assert.Equal(backup.SelectedHistory.FilePath, backup.RestoreFile);
        backup.RestoreFile = backup.History.First(h => h.DatabaseName == "ERP_ControlDB").FilePath;
        await backup.RestoreCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("تعذّر الاسترداد") && e.Contains("ليست"));
        dialogs.Errors.Clear();

        // مجلد غير موجود على السيرفر: رسالة واضحة بدل انهيار
        backup.Folder = "/no/such/folder";
        await backup.BackupCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("تعذّر النسخ الاحتياطي"));
        Assert.Empty(_f.Unhandled);
    }
    [Fact]
    public async Task Customer_deposit_receipt_refund_void_and_statement_stays_debt_only()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var sales = shell.Open<SalesModuleViewModel>(ModuleCode.Sales);
        await sales.IdleAsync();
        var dep = sales.Deposits;
        await Open(sales, dep);

        dep.Customer = dep.Customers.Single(c => c.Id == _f.DirectId);
        await dep.IdleAsync();
        var before = dep.Balance;
        Assert.Equal(CustomerDepositsSectionViewModel.DefaultPurpose, dep.Purpose);
        Assert.True(dep.IsReceipt);
        dep.Amount = 200_000;
        await dep.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Equal(before + 200_000, dep.Balance);
        Assert.Equal(0, dep.Amount);

        // سند الاستلام يُطبع مباشرة بعد الحفظ
        var receipt = dialogs.Reports.Last();
        Assert.Equal("سند استلام تأمين", receipt.Title);
        Assert.True(receipt.ReceiptCapable);
        Assert.Contains(receipt.HeaderFields, f => f.Label == "استلمنا من" && f.Value == "زبون مباشر");
        Assert.Contains(receipt.HeaderFields, f => f.Label == "الغرض" && f.Value == CustomerDepositsSectionViewModel.DefaultPurpose);
        Assert.Contains(receipt.Totals, t => t.Label == "المبلغ" && t.Value == "200,000 د.ع" && t.Emphasis);
        Assert.Contains(receipt.Totals, t => t.Label == "رصيد تأمين العميل بعد السند" && t.Value == $"{before + 200_000:N0} د.ع");
        Assert.Contains(dep.Balances, b => b.CustomerId == _f.DirectId && b.Balance == dep.Balance);

        // الإرجاع لا يتجاوز الرصيد
        dep.Kind = dep.Kinds.Single(k => k.Value == CustomerDepositKind.Refund);
        Assert.False(dep.IsReceipt);
        dep.Amount = dep.Balance + 1;
        await dep.SaveCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("لا يمكن إرجاع أكثر منه"));
        dialogs.Errors.Clear();
        dep.Amount = 50_000;
        await dep.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Equal(before + 150_000, dep.Balance);
        Assert.Equal("سند إرجاع تأمين", dialogs.Reports.Last().Title);

        // كشف الحساب: التأمين يظهر للعلم فقط ولا يدخل في الدين
        await Open(sales, sales.Statement);
        sales.Statement.Customer = sales.Statement.Customers.Single(c => c.Id == _f.DirectId);
        await sales.Statement.IdleAsync();
        Assert.Contains($"تأمين قائم: {before + 150_000:N0}", sales.Statement.CustomerInfo);
        Assert.DoesNotContain(sales.Statement.Rows, r => r.DocNumber.StartsWith("DP-"));

        // إلغاء سند الإرجاع (للأدمن) يعيد الرصيد — الشاشة عادت نظيفة بعد الخروج منها، فيُختار العميل من جديد
        await Open(sales, dep);
        Assert.Null(dep.Customer);
        dep.Customer = dep.Customers.Single(c => c.Id == _f.DirectId);
        await dep.IdleAsync();
        var refundRow = dep.History.First(r => r.Kind == CustomerDepositKind.Refund && !r.IsVoided);
        dep.BeginVoidCommand.Execute(refundRow);
        Assert.True(dep.IsVoiding);
        await dep.VoidCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("سبب الإلغاء"));
        dialogs.Errors.Clear();
        dep.VoidReason = "إرجاع بالخطأ";
        await dep.VoidCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Equal(before + 200_000, dep.Balance);
        Assert.False(dep.IsVoiding);
        Assert.True(dep.History.Single(r => r.Id == refundRow.Id).IsVoided);

        // موظف المبيعات يسجّل التأمينات لكن لا يلغيها
        var (clerkShell, clerkDialogs) = await _f.LoginAsync(AppFixture.ClerkUser, AppFixture.ClerkPassword);
        var clerkSales = clerkShell.Open<SalesModuleViewModel>(ModuleCode.Sales);
        await clerkSales.IdleAsync();
        await Open(clerkSales, clerkSales.Deposits);
        clerkSales.Deposits.Customer = clerkSales.Deposits.Customers.Single(c => c.Id == _f.DirectId);
        await clerkSales.Deposits.IdleAsync();
        clerkSales.Deposits.BeginVoidCommand.Execute(clerkSales.Deposits.History.First(r => !r.IsVoided));
        clerkSales.Deposits.VoidReason = "محاولة";
        await clerkSales.Deposits.VoidCommand.ExecuteAsync();
        Assert.Contains(clerkDialogs.Errors, e => e.Contains("للأدمن فقط"));

        // تنظيف: إلغاء الاستلام يعيد الصندوق والرصيد كما كانا (لا أثر على اختبارات الصناديق)
        await dep.LoadAsync();
        dep.BeginVoidCommand.Execute(dep.History.First(r => r.Kind == CustomerDepositKind.Receipt && !r.IsVoided));
        dep.VoidReason = "تنظيف الاختبار";
        await dep.VoidCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Equal(before, dep.Balance);
        Assert.Empty(_f.Unhandled);
    }
    [Fact]
    public async Task Reconciliation_screen_valuation_choice_partners_and_baseline()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var fin = shell.Open<FinanceModuleViewModel>(ModuleCode.Finance);
        await fin.IdleAsync();

        // الشركاء: تُضاف يدويًا بنسب مختلفة، ومحمد المدير
        var partners = fin.Partners!;
        await Open(fin, partners);
        Assert.Contains("لا يوجد شركاء", partners.PercentHint);
        async Task AddPartner(string name, decimal percent, bool manager)
        {
            partners.NewCommand.Execute(null);
            partners.Name = name;
            partners.SharePercent = percent;
            partners.IsManager = manager;
            await partners.SaveCommand.ExecuteAsync();
        }
        await AddPartner("محمد", 50, true);
        await AddPartner("شريك ب", 30, false);
        Assert.Contains("80%", partners.PercentHint);
        await AddPartner("شريك ج", 30, false);                       // يتجاوز 100%
        Assert.Contains(dialogs.Errors, e => e.Contains("المتاح 20%"));
        dialogs.Errors.Clear();
        partners.SharePercent = 20;
        await partners.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Equal("مجموع النسب 100% ✓", partners.PercentHint);
        Assert.True(partners.Partners.Single(p => p.Name == "محمد").IsManager);

        // المطابقة: المنتج التام بسعر الكلفة أو بسعر البيع
        var rec = fin.Reconciliation!;
        await Open(fin, rec);
        await rec.ComputeCommand.ExecuteAsync();
        Assert.True(rec.HasSnapshot);
        var atCost = rec.Snapshot!;
        rec.Valuation = rec.Valuations.Single(v => v.Value == FinishedGoodsValuation.SalePrice);
        await rec.IdleAsync();
        var atSale = rec.Snapshot!;
        Assert.Equal(FinishedGoodsValuation.SalePrice, atSale.Valuation);
        Assert.Equal(atCost.RawMaterials, atSale.RawMaterials);                // المواد الأولية بالكلفة دائمًا
        Assert.Equal(atCost.CustomerDebts, atSale.CustomerDebts);
        Assert.Contains(rec.Lines, l => l.Section == Data.Services.ReconciliationService.FgSection && l.Description.Contains("W500") && l.UnitValue == 250);
        rec.PrintPreviewCommand.Execute(null);
        Assert.Equal("معاينة — غير معتمدة", dialogs.Reports.Last().Stamp);
        Assert.Contains(dialogs.Reports.Last().HeaderFields, f => f.Label == "تقييم المنتج التام" && f.Value == "بسعر البيع");

        // أول مطابقة = أساس، تُطبع بعد الاعتماد
        Assert.True(atSale.IsBaseline);
        Assert.Equal("مطابقة أساس", rec.SurplusLabel);
        await rec.PostCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        var saved = Assert.Single(rec.History);
        Assert.True(saved.IsBaseline);
        Assert.Equal("مطابقة الموجودات", dialogs.Reports.Last().Title);
        Assert.Contains(dialogs.Reports.Last().Totals, t => t.Label == "الفائض" && t.Value.Contains("أساس"));

        // لا أرباح بعد ← السحب مرفوض، وكشف الشريك فارغ
        await Open(fin, partners);
        partners.Selected = partners.Partners.Single(p => p.Name == "محمد");
        await partners.IdleAsync();
        Assert.Empty(partners.Statement);
        partners.Amount = 1_000;
        await partners.WithdrawCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("رصيد أرباح محمد 0"));
        await partners.PrintStatementCommand.ExecuteAsync();
        Assert.Equal("كشف حساب شريك", dialogs.Reports.Last().Title);
        Assert.Empty(_f.Unhandled);
    }
}
