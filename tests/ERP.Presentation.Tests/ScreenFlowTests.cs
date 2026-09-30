using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Finance;
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
        Assert.Equal(6, sales.Home.Sections.Count());

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
        Assert.Empty(_f.Unhandled);
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

        // حذف صنف له حركات يُرفض برسالة واضحة
        await items.DeleteCommand.ExecuteAsync(items.Items.Single(i => i.ItemCode == "W500"));
        Assert.Contains(dialogs.Errors, e => e.Contains("مرتبط بحركات"));
    }

    [Fact]
    public async Task Stock_adjustment_blocks_negative_balance()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var wh = shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
        var adj = wh.Section<StockAdjustmentSectionViewModel>();
        await Open(wh, adj);

        adj.Item = adj.ItemsLookup.Single(i => i.Id == _f.WaterItemId);
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

        var rules = fin.Section<MappingRulesSectionViewModel>();
        await Open(fin, rules);
        // القواعد الناقصة (مثل الدفع الإلكتروني) تُضاف تلقائيًا عند فتح المشروع
        Assert.Contains(rules.Items, r => r.TransactionType == "SalesInvoiceElectronic");
        Assert.Equal("كل القواعد المطلوبة معرّفة ✓", rules.MissingText);
    }

    [Fact]
    public async Task Purchase_order_and_goods_receipt_increase_stock_and_supplier_balance()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var sup = shell.Open<SuppliersModuleViewModel>(ModuleCode.Suppliers);
        var po = sup.Section<PurchaseOrdersSectionViewModel>();
        await Open(sup, po);

        po.NewOrderCommand.Execute(null);
        po.Supplier = po.SuppliersLookup.Single(s => s.Id == _f.SupplierId);
        po.Warehouse = po.Warehouses.Single(w => w.Id == _f.MainWarehouseId);
        po.Lines[0].Item = po.ItemsLookup.Single(i => i.Id == _f.WaterItemId);
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

        // مجلد غير موجود على السيرفر: رسالة واضحة بدل انهيار
        backup.Folder = "/no/such/folder";
        await backup.BackupCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("تعذّر النسخ الاحتياطي"));
        Assert.Empty(_f.Unhandled);
    }
}
