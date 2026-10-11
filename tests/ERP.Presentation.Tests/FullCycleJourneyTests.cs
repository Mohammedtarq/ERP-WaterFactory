using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Data.Setup;
using ERP.Presentation.ViewModels.Production;
using ERP.Presentation.ViewModels.Reps;
using ERP.Presentation.ViewModels.Shell;
using ERP.Presentation.ViewModels.Warehouse;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// الدورة الكاملة لطلب خاص (نفس مسار الدليل المصوّر full-cycle.md، بلا رسم): ليبل «مطعم زوارق الشاطئ» يُعرَّف ويُستلم،
/// وصفة المطعم تستبدل الليبل العام (ليبلان للقنينة)، أمر إنتاج واحد بسطر عام وسطر خاص بالكارتون (40 قنينة)،
/// تحت التصنيع، فحص المختبر للأمر كله، التعبئة لمخزن المنتج التام، طلب تحميل لسيارة «كاش فان علي» بسطر عام وسطر باسم المطعم،
/// تجهيز أمين المخزن، ثم لوحة السيارات.
/// </summary>
[Collection("app")]
public class FullCycleJourneyTests
{
    private readonly AppFixture _f;
    public FullCycleJourneyTests(AppFixture f) => _f = f;

    public const string Restaurant = "مطعم زوارق الشاطئ";
    public const string ProductCode = "W-330C";

    /// <summary>التحضير المشترك مع الدليل المصوّر: المطعم والمنتج «ماء 330 مل كارتون 40» بقائمة مواده (ليبلان للقنينة).</summary>
    public static async Task PrepareAsync(string projectConnection)
    {
        await using var db = new ProjectDbContext(new DbContextOptionsBuilder<ProjectDbContext>().UseSqlServer(projectConnection).Options);
        db.Customers.Add(new Customer { Name = Restaurant, CustomerType = CustomerType.Direct, Province = "البصرة" });
        var product = new Item { ItemCode = ProductCode, ItemName = "ماء الرحمة 330 مل كارتون 40", SalePrice = 150, SourcingMethod = SourcingMethod.Manufactured, MinStockAlertLevel = 400 };
        db.Items.Add(product);
        await db.SaveChangesAsync();
        var piece = new ItemPackagingLevel { ItemId = product.Id, LevelName = "قطعة", ContainsQuantity = 1, EquivalentBaseUnits = 1 };
        db.ItemPackagingLevels.Add(piece);
        await db.SaveChangesAsync();
        db.ItemPackagingLevels.Add(new ItemPackagingLevel { ItemId = product.Id, LevelName = "كارتون", ParentLevelId = piece.Id, ContainsQuantity = 40, EquivalentBaseUnits = 40, IsSellableUnit = true });
        var bom = new BillOfMaterials { FinishedItemId = product.Id };
        foreach (var (code, qty, role) in new[] { ("RM-PRE", 1m, "امبولة"), ("RM-CAP", 1m, "غطاء"), ("RM-LBL", 2m, "لاصق") })
            bom.Lines.Add(new BOMLine { RawMaterialItemId = await db.Items.Where(i => i.ItemCode == code).Select(i => i.Id).SingleAsync(), QuantityPerUnit = qty, ComponentRole = role });
        db.BillOfMaterials.Add(bom);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Special_restaurant_order_from_label_receipt_to_the_reps_van()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var controlCs = new SqlConnectionStringBuilder(_f.ControlConnection) { InitialCatalog = $"ERP_FcCtl_{suffix}" }.ConnectionString;
        var install = await new ProvisioningService().InstallAsync(new InstallRequest(
            controlCs, "معمل مياه الرحمة", $"ERP_FcPrj_{suffix}", "المدير العام", "admin", "Admin@123", DemoData: true));
        Assert.True(install.Success, install.ErrorMessage);
        await PrepareAsync(install.ProjectConnectionString);

        var dialogs = new RecordingDialogs();
        var nav = new RecordingNavigator();
        var login = new LoginViewModel(new AuthService(controlCs), dialogs, nav) { Username = "admin" };
        await login.LoginCommand.ExecuteAsync("Admin@123");
        Assert.Null(login.ErrorMessage);
        await nav.ProjectSelection!.OpenCommand.ExecuteAsync(null);
        var shell = nav.Shell!;

        async Task Open(ModuleViewModel module, SectionViewModel section)
        {
            module.SelectedTab = section;
            await module.LastActivation;
            await section.IdleAsync();
        }
        void NoErrors(string step) => Assert.True(dialogs.Errors.Count == 0, $"{step}: " + string.Join(" | ", dialogs.Errors));

        // ---------- 1) المخازن: تعريف الليبل الخاص واستلامه ----------
        var wh = shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
        await shell.IdleAllAsync();
        var items = wh.Section<ItemsSectionViewModel>();
        await Open(wh, items);
        await items.NewCommand.ExecuteAsync();
        items.Editor!.ItemCode = "RM-LBL-ZWQ";
        items.Editor.ItemName = $"ليبل {Restaurant}";
        items.Editor.SourcingMethod = SourcingMethod.Purchased;
        await items.SaveCommand.ExecuteAsync();
        await items.IdleAsync();
        NoErrors("تعريف الليبل");

        var raw = wh.Workspaces.First(w => w.WarehouseType == WarehouseType.RawMaterial);
        await Open(wh, raw);
        raw.Operation = raw.OperationOptions.Single(o => o.Value == StockDocumentType.Receipt);
        raw.PartyName = "مطبعة الفيحاء — فاتورة 1530";
        raw.LineItem = raw.ItemsLookup.Single(i => i.ItemCode == "RM-LBL-ZWQ");
        await raw.IdleAsync();
        raw.LineQuantity = 1000;
        raw.LineNewBatch = "ZWQ-01";
        raw.LineExpiry = DateTime.Today.AddYears(2);
        await raw.AddLineCommand.ExecuteAsync();
        await raw.SaveCommand.ExecuteAsync();
        await raw.IdleAsync();
        NoErrors("استلام الليبل");

        // ---------- 2) الإنتاج: وصفة المطعم (ليبله يحل محل الليبل العام، ليبلان للقنينة) ----------
        var prod = shell.Open<ProductionModuleViewModel>(ModuleCode.Production);
        await prod.IdleAsync();
        var recipes = prod.Section<CustomRecipesSectionViewModel>();
        await Open(prod, recipes);
        await recipes.NewCommand.ExecuteAsync();
        recipes.Editor!.Name = Restaurant;
        recipes.Editor.FinishedItemId = recipes.FinishedItems.Single(i => i.ItemCode == ProductCode).Id;
        recipes.Editor.CustomerId = recipes.Customers.Single(c => c.Name == Restaurant).Id;
        await recipes.SaveCommand.ExecuteAsync();
        await recipes.IdleAsync();
        recipes.SelectedRecipe = recipes.Items.Single(r => r.Name == Restaurant);
        await recipes.IdleAsync();
        recipes.NewComponent = recipes.RawItems.Single(i => i.ItemCode == "RM-LBL-ZWQ");
        recipes.NewLabel = "لاصق";
        recipes.NewQuantity = 2;
        recipes.NewReplaces = recipes.RawItems.Single(i => i.ItemCode == "RM-LBL");
        await recipes.AddLineCommand.ExecuteAsync();
        await recipes.IdleAsync();
        NoErrors("الوصفة");

        // ---------- 3) أمر إنتاج واحد: 10 كراتين عام + 5 كراتين للمطعم ----------
        var orders = prod.Orders;
        await Open(prod, orders);
        Assert.False(orders.HasSidePanel);
        await orders.NewOrderCommand.ExecuteAsync();
        Assert.True(orders.HasSidePanel);                       // النموذج مفتوح: يُخفى عمودا «الماكينة» و«رقم الدفعة» من الجدول
        orders.Machine = orders.Machines.Single(m => m.Name == "نافخة 1");
        orders.FinishedItem = orders.FinishedItems.Single(i => i.ItemCode == ProductCode);
        await orders.IdleAsync();
        Assert.Equal("كارتون", orders.Level!.LevelName);
        orders.Packs = 10;
        Assert.Equal("10 كارتون = 400 قطعة", orders.PiecesText);
        await orders.AddLineCommand.ExecuteAsync();
        NoErrors("السطر العام");
        orders.FinishedItem = orders.FinishedItems.Single(i => i.ItemCode == ProductCode);
        await orders.IdleAsync();
        orders.Recipe = orders.Recipes.Single(r => r.Name == Restaurant);
        await orders.IdleAsync();
        orders.Packs = 5;
        await orders.PreviewCommand.ExecuteAsync();
        Assert.True(orders.AllSufficient);
        // الليبل الخاص: 5 كراتين × 40 × 2 = 400، والعام 10 × 40 × 2 = 800
        Assert.Contains(orders.Preview, p => p.RawMaterialName == $"ليبل {Restaurant}" && p.Required == 400);
        Assert.Contains(orders.Preview, p => p.RawMaterialName == "لاصق أمامي" && p.Required == 800);
        await orders.CreateCommand.ExecuteAsync();
        NoErrors("إنشاء الأمر");
        Assert.False(orders.HasSidePanel);                      // أُغلق النموذج: تعود كل الأعمدة
        var order = orders.Orders.First();
        Assert.Equal(2, order.LinesCount);
        Assert.Equal("15 كارتون", order.QuantityText);
        await orders.StartCommand.ExecuteAsync(order);
        NoErrors("بدء التشغيل");

        var wip = prod.Wip;
        await Open(prod, wip);
        NoErrors("تحت التصنيع");

        // ---------- 4) المختبر: فحص واحد للأمر بصنفيه (الجرس يفتحه) ----------
        await shell.RefreshAlertsAsync();
        var labAlert = Assert.Single(shell.Alerts, a => a.Key == "lab-waiting");
        var qc = Assert.IsType<QcSectionViewModel>(shell.OpenAlert(labAlert));
        var lab = Assert.IsType<LabModuleViewModel>(shell.CurrentModule);
        await lab.LastActivation;
        await qc.IdleAsync();
        Assert.Equal(order.Id, qc.Order!.OrderId);
        qc.Lines.Single(l => l.TestName.StartsWith("درجة")).Measured = "7.3";
        qc.Lines.Single(l => l.TestName.StartsWith("الأملاح")).Measured = "135";
        qc.Lines.Single(l => l.TestName.StartsWith("إحكام")).Measured = "سليم";
        await qc.SaveCommand.ExecuteAsync();
        NoErrors("الفحص");
        Assert.Contains("ناجحة", qc.LastResult);

        // ---------- 5) التعبئة: كل سطر بالكارتون إلى مخزن المنتج التام ----------
        shell.Open<ProductionModuleViewModel>(ModuleCode.Production);
        var packing = prod.Packing;
        await Open(prod, packing);
        foreach (var (recipe, cartons) in new[] { ((string?)null, 10m), (Restaurant, 5m) })
        {
            packing.Order = packing.ReadyOrders.Single(o => o.OrderId == order.Id && o.RecipeName == recipe);
            await packing.IdleAsync();
            Assert.Equal("كارتون", packing.Level!.LevelName);
            Assert.Equal(cartons, packing.Units);
            await packing.PackCommand.ExecuteAsync();
            await packing.IdleAsync();
            NoErrors($"تعبئة {recipe ?? "العام"}");
        }

        shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
        var fg = wh.Workspaces.First(w => w.WarehouseType == WarehouseType.FinishedGoods);
        await Open(wh, fg);
        Assert.Contains(fg.Balances, b => b.ItemCode == ProductCode && b.Variant == Restaurant && b.Breakdown == "5 كارتون");
        Assert.Contains(fg.Balances, b => b.ItemCode == ProductCode && b.Variant == null && b.Breakdown == "10 كارتون");

        // ---------- 6) المندوبون: طلب تحميل لسيارة «كاش فان علي» — 6 كراتين عامة + 5 باسم المطعم ----------
        var reps = shell.Open<RepsModuleViewModel>(ModuleCode.Reps);
        await reps.IdleAsync();
        var loads = reps.LoadOrders;
        await Open(reps, loads);
        loads.Van = loads.Vans.Single(v => v.Name == "كاش فان علي");
        loads.Store = loads.Stores.Single(w => w.WarehouseType == WarehouseType.FinishedGoods);
        await loads.IdleAsync();
        loads.Lines.Clear();
        foreach (var (recipe, qty) in new[] { ((string?)null, 6m), (Restaurant, 5m) })
        {
            loads.AddLineCommand.Execute(null);
            var l = loads.Lines[^1];
            l.Product = loads.Products.Single(i => i.ItemCode == ProductCode);
            l.Recipe = recipe is null ? l.Recipes[0] : l.Recipes.Single(r => r.Name == recipe);
            l.Level = l.Levels.Single(x => x.LevelName == "كارتون");
            l.Quantity = qty;
        }
        Assert.All(loads.Lines, l => Assert.False(l.IsShort));
        await loads.CreateCommand.ExecuteAsync();
        await loads.IdleAsync();
        NoErrors("طلب التحميل");

        // ---------- 7) المخازن: أمين المخزن يجهّز الطلب (الجرس يفتح «طلبات التجهيز») ----------
        await shell.RefreshAlertsAsync();
        var prepare = Assert.IsType<PrepareLoadOrdersSectionViewModel>(shell.OpenAlert(shell.Alerts.Single(a => a.Key == "load-orders")));
        await wh.LastActivation;
        await prepare.IdleAsync();
        prepare.SelectedOrder = prepare.Orders.Single(o => o.Status == RepLoadOrderStatus.Pending);
        await prepare.IdleAsync();
        Assert.Equal(2, prepare.PrepareLines.Count);
        await prepare.PrepareCommand.ExecuteAsync();
        await prepare.IdleAsync();
        NoErrors("التجهيز");
        Assert.Contains("جُهّزت", prepare.StatusMessage);

        // ---------- 8) لوحة السيارات: الحمولة على «كاش فان علي» بالكارتون ومتغير المطعم ----------
        shell.Open<RepsModuleViewModel>(ModuleCode.Reps);
        var board = reps.Vans;
        await Open(reps, board);
        Assert.Equal("11 كارتون", board.TotalText);                         // الملخّص بالعبوات لا بالقطع
        board.SelectedCard = board.Cards.Single(c => c.VanName == "كاش فان علي");
        await board.IdleAsync();
        var detail = board.Detail!;
        await detail.IdleAsync();
        Assert.Contains(detail.Balances, b => b.ItemCode == ProductCode && b.Variant == Restaurant && b.Breakdown == "5 كارتون");
        Assert.Contains(detail.Balances, b => b.ItemCode == ProductCode && b.Variant == null && b.Breakdown == "6 كارتون");

        await shell.IdleAllAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Empty(_f.Unhandled);
    }
}
