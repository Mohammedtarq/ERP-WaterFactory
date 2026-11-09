using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.ViewModels.Production;
using Finance = ERP.Presentation.ViewModels.Finance;
using ERP.Presentation.ViewModels.Shell;
using ERP.Presentation.ViewModels.Warehouse;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>الإنتاج الإضافي كما يعمله المستخدم: يُطلب من «أوامر الإنتاج»، ثم يظهر لأمين المخزن بإشعار فيوافق عليه من «طلبات التجهيز».</summary>
[Collection("app")]
public class ExtraProductionScreenTests
{
    private readonly AppFixture _f;
    public ExtraProductionScreenTests(AppFixture f) => _f = f;

    [Fact]
    public async Task Extra_production_is_requested_from_orders_and_approved_by_the_storekeeper()
    {
        int orderId; int waterId; string moNumber;
        await using (var db = _f.NewDb())
        {
            var branch = await db.Branches.FirstAsync();
            var raw = await db.Warehouses.FirstOrDefaultAsync(w => w.WarehouseType == WarehouseType.RawMaterial);
            if (raw is null) db.Warehouses.Add(raw = new Warehouse { BranchId = branch.Id, Name = "مخزن المواد الأولية XP", WarehouseType = WarehouseType.RawMaterial, IsSellableStock = false });
            var admin = await db.Users.FirstAsync(u => u.Username == AppFixture.AdminUser);
            Item Raw(string code, string name) => new() { ItemCode = code, ItemName = name, SourcingMethod = SourcingMethod.Purchased, CostPrice = 10 };
            var pre = Raw("XP-PRE", "امبولة XP"); var cap = Raw("XP-CAP", "سدادة XP"); var lbl = Raw("XP-LBL", "ليبل XP");
            var water = new Item { ItemCode = "XP-W", ItemName = "ماء XP", SalePrice = 250 };
            db.Items.AddRange(pre, cap, lbl, water);
            await db.SaveChangesAsync();
            foreach (var r in new[] { pre, cap, lbl })
            {
                db.ItemPackagingLevels.Add(new ItemPackagingLevel { ItemId = r.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 });
                db.StockTransactions.Add(new StockTransaction { ItemId = r.Id, WarehouseId = raw.Id, QuantityBaseUnits = 2_000, UnitCost = 10,
                                                                TransactionType = StockTransactionType.Receipt, CreatedByUserId = admin.Id });
            }
            var carton = new ItemPackagingLevel { ItemId = water.Id, LevelName = "كارتون", ContainsQuantity = 12, EquivalentBaseUnits = 12 };
            db.ItemPackagingLevels.AddRange(new ItemPackagingLevel { ItemId = water.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 }, carton);
            var bom = new BillOfMaterials { FinishedItemId = water.Id };
            bom.Lines.Add(new BOMLine { RawMaterialItemId = pre.Id, QuantityPerUnit = 1, ComponentRole = "امبولة" });
            bom.Lines.Add(new BOMLine { RawMaterialItemId = cap.Id, QuantityPerUnit = 1, ComponentRole = "غطاء" });
            bom.Lines.Add(new BOMLine { RawMaterialItemId = lbl.Id, QuantityPerUnit = 1, ComponentRole = "لاصق" });
            db.BillOfMaterials.Add(bom);
            await db.SaveChangesAsync();
            var (r0, oid, _) = await new DailyProductionService(db).RecordAsync(DateTime.Today, new[] { new DailyProductionLineInput(water.Id, carton.Id, 5) }, admin.Id);
            Assert.True(r0.Success, r0.ErrorMessage);
            orderId = oid!.Value; waterId = water.Id;
            moNumber = (await db.ProductionOrders.FindAsync(orderId))!.MONumber;
        }

        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);

        // 1) من أوامر الإنتاج: القائمة ⋯ ← «إنتاج إضافي»
        var prod = shell.Open<ProductionModuleViewModel>(ModuleCode.Production);
        var orders = prod.Section<ProductionOrdersSectionViewModel>();
        prod.SelectedTab = orders;
        await prod.IdleAsync(); await orders.IdleAsync();
        var row = orders.Orders.Single(o => o.Id == orderId);
        await orders.ExtraCommand.ExecuteAsync(row);
        Assert.True(orders.IsExtra);
        Assert.True(orders.HasSidePanel);
        orders.ExtraPacks = 2;
        await orders.IdleAsync();
        Assert.False(orders.ExtraShort);
        Assert.Contains("المواد تكفي", orders.ExtraNeedsText);
        await orders.SaveExtraCommand.ExecuteAsync();      // السبب إلزامي
        Assert.Contains(dialogs.Errors, e => e.Contains("سبب"));
        dialogs.Errors.Clear();
        orders.ExtraReason = "طلب إضافي من الزبون";
        await orders.SaveExtraCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Single(orders.ExtraHistory);
        Assert.Contains("بانتظار الموافقة", orders.ExtraHistory[0]);

        // 2) الإشعار لأمين المخزن، ثم الموافقة من «طلبات التجهيز»
        await shell.RefreshAlertsAsync();
        var alert = shell.Alerts.Single(a => a.Key == "extra-production");
        Assert.Contains(moNumber, alert.Detail);
        var prepare = Assert.IsType<PrepareLoadOrdersSectionViewModel>(shell.OpenAlert(alert));
        var warehouse = Assert.IsType<WarehouseModuleViewModel>(shell.CurrentModule);
        await warehouse.IdleAsync(); await warehouse.LastActivation; await prepare.IdleAsync();
        Assert.True(prepare.HasExtraRequests);
        var req = prepare.ExtraRequests.Single();
        Assert.Equal(2m, prepare.ApprovePacks);
        prepare.ApprovePacks = 1;                          // موافقة جزئية
        await prepare.ApproveExtraCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.False(prepare.HasExtraRequests);

        await using var check = _f.NewDb();
        var saved = await check.ExtraProductionRequests.AsNoTracking().SingleAsync(r => r.Id == req.Id);
        Assert.Equal(ExtraProductionStatus.Partial, saved.Status);
        Assert.Equal(1m, saved.ApprovedPacks);
        var fgQty = await check.StockTransactions.Where(t => t.ItemId == waterId && t.Warehouse.WarehouseType == WarehouseType.FinishedGoods).SumAsync(t => t.QuantityBaseUnits);
        Assert.Equal(72m, fgQty);                          // 60 من الأمر + 12 إضافية

        // 3) عمود «إضافي» في أوامر الإنتاج
        await orders.RefreshCommand.ExecuteAsync();
        Assert.Equal("+ 12", orders.Orders.Single(o => o.Id == orderId).ExtraText);
    }

    [Fact]
    public async Task Draft_is_edited_from_the_orders_screen_and_checked_in_the_lab_screen_before_start()
    {
        int orderId;
        await using (var db = _f.NewDb())
        {
            var branch = await db.Branches.FirstAsync();
            var raw = await db.Warehouses.FirstOrDefaultAsync(w => w.WarehouseType == WarehouseType.RawMaterial);
            if (raw is null) db.Warehouses.Add(raw = new Warehouse { BranchId = branch.Id, Name = "مخزن المواد الأولية DE", WarehouseType = WarehouseType.RawMaterial, IsSellableStock = false });
            var admin = await db.Users.FirstAsync(u => u.Username == AppFixture.AdminUser);
            Item Raw(string code, string name) => new() { ItemCode = code, ItemName = name, SourcingMethod = SourcingMethod.Purchased, CostPrice = 10 };
            var pre = Raw("DE-PRE", "امبولة DE"); var cap = Raw("DE-CAP", "سدادة DE");
            var a = new Item { ItemCode = "DE-A", ItemName = "ماء DE أ", SalePrice = 250 };
            var b = new Item { ItemCode = "DE-B", ItemName = "ماء DE ب", SalePrice = 250 };
            db.Items.AddRange(pre, cap, a, b);
            await db.SaveChangesAsync();
            foreach (var r in new[] { pre, cap })
            {
                db.ItemPackagingLevels.Add(new ItemPackagingLevel { ItemId = r.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 });
                db.StockTransactions.Add(new StockTransaction { ItemId = r.Id, WarehouseId = raw.Id, QuantityBaseUnits = 5_000, UnitCost = 10, TransactionType = StockTransactionType.Receipt, CreatedByUserId = admin.Id });
            }
            foreach (var w in new[] { a, b })
            {
                db.ItemPackagingLevels.AddRange(new ItemPackagingLevel { ItemId = w.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 }, new ItemPackagingLevel { ItemId = w.Id, LevelName = "كارتون", ContainsQuantity = 12, EquivalentBaseUnits = 12 });
                var bom = new BillOfMaterials { FinishedItemId = w.Id };
                bom.Lines.Add(new BOMLine { RawMaterialItemId = pre.Id, QuantityPerUnit = 1, ComponentRole = "امبولة" });
                bom.Lines.Add(new BOMLine { RawMaterialItemId = cap.Id, QuantityPerUnit = 1, ComponentRole = "غطاء" });
                db.BillOfMaterials.Add(bom);
            }
            await db.SaveChangesAsync();
            var (_, machineId) = await new MachineService(db).SaveAsync(null, "خط DE", "تعبئة", null, null, true);
            var (cr, oid) = await new ProductionService(db).CreateOrderAsync(new[] { new ProductionLineInput(a.Id, 120), new ProductionLineInput(b.Id, 24) }, raw.Id, machineId, admin.Id);
            Assert.True(cr.Success, cr.ErrorMessage);
            orderId = oid!.Value;
        }
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);

        // المسودة تظهر في قائمة فحص المختبر قبل التشغيل
        var lab = shell.Open<LabModuleViewModel>(ModuleCode.Lab);
        lab.SelectedTab = lab.Qc;
        await lab.IdleAsync(); await lab.Qc.IdleAsync();
        var qcRow = lab.Qc.QcOrders.Single(o => o.OrderId == orderId);
        Assert.True(qcRow.IsDraft);
        Assert.Contains("مسودة", qcRow.Display);

        // التعديل: حذف الصنف الثاني من لوحة الأمر ثم حفظ
        var prod = shell.Open<ProductionModuleViewModel>(ModuleCode.Production);
        var orders = prod.Section<ProductionOrdersSectionViewModel>();
        prod.SelectedTab = orders;
        await prod.IdleAsync(); await orders.IdleAsync();
        await orders.EditDraftCommand.ExecuteAsync(orders.Orders.Single(o => o.Id == orderId));
        Assert.True(orders.IsComposing);
        Assert.Equal(2, orders.ComposeLines.Count);
        Assert.Contains("تعديل المسودة", orders.ComposeTitle);
        Assert.Equal("حفظ التعديل", orders.CreateButtonText);
        await orders.RemoveLineCommand.ExecuteAsync(orders.ComposeLines[1]);
        await orders.CreateCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.False(orders.IsComposing);
        await using var check = _f.NewDb();
        var lines = await check.ProductionOrderLines.AsNoTracking().Where(l => l.ProductionOrderId == orderId).ToListAsync();
        Assert.Single(lines);
        Assert.Equal(120m, lines[0].QuantityToProduce);
    }

    [Fact]
    public async Task Warehouse_workspace_can_show_in_production_instead_of_warehouses_and_daily_production_is_hidden_by_default()
    {
        int whId;
        await using (var db = _f.NewDb())
        {
            var branch = await db.Branches.FirstAsync();
            var w = new Warehouse { BranchId = branch.Id, Name = "مخزن تالف الإنتاج SM", WarehouseType = WarehouseType.Damaged, IsSellableStock = false, ShowInModule = ModuleCode.Production };
            db.Warehouses.Add(w);
            await db.SaveChangesAsync();
            whId = w.Id;
        }
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var prod = shell.Open<ProductionModuleViewModel>(ModuleCode.Production);
        await prod.IdleAsync();
        await prod.RefreshWorkspacesAsync();
        var ws = Assert.Single(prod.Workspaces, x => x.WarehouseId == whId);
        Assert.Equal(ModuleCode.Production, ws.Module);          // صلاحياته على الإنتاج
        Assert.Equal("المخازن", ws.MovedFrom);
        Assert.Contains(ws, prod.Tabs);
        var warehouse = shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
        await warehouse.IdleAsync();
        await warehouse.RefreshWorkspacesAsync();
        Assert.DoesNotContain(warehouse.Workspaces, x => x.WarehouseId == whId);

        // إعادته إلى المخازن من تعريف المخزن تعيد المساحة هناك وتزيلها من الإنتاج
        await using (var db = _f.NewDb())
            await db.Warehouses.Where(x => x.Id == whId).ExecuteUpdateAsync(u => u.SetProperty(x => x.ShowInModule, ""));
        await warehouse.RefreshWorkspacesAsync();
        await prod.RefreshWorkspacesAsync();
        Assert.Contains(warehouse.Workspaces, x => x.WarehouseId == whId);
        Assert.DoesNotContain(prod.Workspaces, x => x.WarehouseId == whId);

        // «إنتاج اليوم»: مخفية افتراضيًا، وتظهر إن فُعِّل ظهورها، وبأرقام خطوات متتابعة
        ProductionModuleViewModel.ShowDailyProduction = false;
        try
        {
            var hidden = new ProductionModuleViewModel(shell.Session, dialogs);
            Assert.DoesNotContain(hidden.Tabs, t => t is DailyProductionSectionViewModel);
            Assert.Equal(new int?[] { 1, 2, 3, 4 }, hidden.Tabs.OfType<SectionViewModel>().Where(t => t.Step is not null).Select(t => t.Step).ToArray());
            Assert.DoesNotContain(SectionCatalog.QuickEntries(shell.Session, dialogs), e => e.Key == "DailyProduction");
        }
        finally { ProductionModuleViewModel.ShowDailyProduction = true; }
        var shown = new ProductionModuleViewModel(shell.Session, dialogs);
        Assert.Contains(shown.Tabs, t => t is DailyProductionSectionViewModel);
    }

    [Fact]
    public async Task Variant_wizard_lists_components_without_roles_and_proposes_the_label_by_customer_name()
    {
        int waterId; int customerId; string customerName;
        await using (var db = _f.NewDb())
        {
            Item Raw(string code, string name) => new() { ItemCode = code, ItemName = name, SourcingMethod = SourcingMethod.Purchased, CostPrice = 5, BaseUnitName = "قطعة" };
            var pre = Raw("NS-PRE", "بريفورم NS"); var cap = Raw("NS-CAP", "غطاء NS"); var lbl = Raw("NS-LBL", "ليبل الرحمة NS");
            var water = new Item { ItemCode = "NS-W", ItemName = "ماء NS", SalePrice = 250 };
            var customer = new Customer { Name = "مطعم بيت الشناشيل NS" };
            db.Items.AddRange(pre, cap, lbl, water);
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
            db.BillOfMaterials.Add(new BillOfMaterials { FinishedItemId = water.Id, IsActive = true, Lines =
            {
                new BOMLine { RawMaterialItemId = pre.Id, QuantityPerUnit = 1 }, new BOMLine { RawMaterialItemId = cap.Id, QuantityPerUnit = 1 },
                new BOMLine { RawMaterialItemId = lbl.Id, QuantityPerUnit = 1 }
            } });
            await db.SaveChangesAsync();
            (waterId, customerId, customerName) = (water.Id, customer.Id, customer.Name);
        }
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var prod = shell.Open<ProductionModuleViewModel>(ModuleCode.Production);
        var recipes = prod.Section<CustomRecipesSectionViewModel>();
        prod.SelectedTab = recipes;
        await prod.IdleAsync(); await recipes.IdleAsync();
        Assert.False(recipes.HasSelectedRecipe);                       // الأدوات المتقدمة مطوية بلا وصفة مختارة
        recipes.OpenWizardCommand.Execute(null);
        recipes.WizardProduct = recipes.FinishedItems.Single(i => i.Id == waterId);
        await recipes.IdleAsync();
        Assert.Equal(3, recipes.WizardRoles.Count);                    // كل المكونات تظهر رغم غياب الأدوار
        Assert.All(recipes.WizardRoles, r => Assert.False(r.HasRole));
        recipes.WizardCustomer = recipes.WizardCustomers.Single(c => c.Id == customerId);
        var label = recipes.WizardRoles.Single(r => r.BaseName.StartsWith("ليبل"));
        Assert.True(label.CreateNew);                                  // الليبل يُقترح له صنف جديد تلقائيًا
        Assert.Equal($"ليبل {customerName}", label.NewItemName);
        Assert.All(recipes.WizardRoles.Where(r => r != label), r => Assert.False(r.CreateNew));
        await recipes.CreateVariantCommand.ExecuteAsync();             // ثلاث حركات: المنتج ثم الزبون ثم «إنشاء»
        Assert.Empty(dialogs.Errors);
        Assert.False(recipes.IsWizardOpen);
        Assert.True(recipes.HasSelectedRecipe);
        await recipes.IdleAsync();
        Assert.Contains(recipes.Lines, l => l.ComponentName == $"ليبل {customerName}" && l.ReplacesName == "ليبل الرحمة NS");
    }

    [Fact]
    public async Task Reconciliation_hub_is_one_page_with_three_tabs_and_hides_the_separate_screens_by_default()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        Finance.FinanceModuleViewModel.ShowSeparateReconciliationScreens = false;
        try
        {
            var module = new Finance.FinanceModuleViewModel(shell.Session, dialogs);
            var hub = Assert.IsType<Finance.ReconciliationHubSectionViewModel>(module.ReconciliationHub);
            Assert.Contains(hub, module.Tabs);
            // الشاشات الثلاث لا تظهر منفصلة في القائمة ولا في البحث السريع
            Assert.DoesNotContain(module.Tabs, t => t is Finance.ReconciliationSectionViewModel or Finance.PartnersSectionViewModel or Finance.WorkingCapitalSectionViewModel);
            Assert.DoesNotContain(SectionCatalog.QuickEntries(shell.Session, dialogs), e => e.Key is "Reconciliation" or "Partners" or "WorkingCapital");
            Assert.Contains(SectionCatalog.QuickEntries(shell.Session, dialogs), e => e.Key == "ReconciliationHub");
        }
        finally { Finance.FinanceModuleViewModel.ShowSeparateReconciliationScreens = true; }

        // التبويبات تحمّل شاشاتها: المطابقة افتراضيًا، ثم الشركاء، ثم الفائض
        var fin = shell.Open<Finance.FinanceModuleViewModel>(ModuleCode.Finance);
        var page = Assert.IsType<Finance.ReconciliationHubSectionViewModel>(fin.ReconciliationHub);
        fin.SelectedTab = page;
        await fin.IdleAsync(); await page.IdleAsync();
        Assert.Equal(1, page.SelectedIndex);
        Assert.Same(page.Reconciliation, page.SelectedChild);
        page.SelectedIndex = 2;
        await page.IdleAsync(); await page.Partners.IdleAsync();
        Assert.Same(page.Partners, page.SelectedChild);
        page.SelectedIndex = 0;
        await page.IdleAsync(); await page.Surplus.IdleAsync();
        Assert.Same(page.Surplus, page.SelectedChild);
        Assert.NotNull(page.Surplus.Snapshot);                          // مؤشر الفائض يُحمَّل عند فتح تبويبه
        Assert.Empty(dialogs.Errors);
        Assert.Empty(_f.Unhandled);
    }
}
