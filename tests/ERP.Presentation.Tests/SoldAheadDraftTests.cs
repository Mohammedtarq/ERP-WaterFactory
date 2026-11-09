using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.ViewModels.Production;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// بيع بانتظار الإنتاج يدخل مسودة أمر الإنتاج تلقائيًا: كل صنف بكمية المباع الذي لا يغطيه أمر آخر مفتوح (مقرّبًا لوحدة البيع)،
/// وللمخطّط تعديله بحرية، وتنبيه خفيف عند بدء أمر أقل من المباع.
/// </summary>
[Collection("app")]
public class SoldAheadDraftTests
{
    private readonly AppFixture _f;
    public SoldAheadDraftTests(AppFixture f) => _f = f;

    [Fact]
    public async Task New_draft_receives_sold_ahead_items_and_already_covered_ones_are_skipped()
    {
        int waterId, boxId = 0, rawWh, machineId;
        await using (var db = _f.NewDb())
        {
            var branch = await db.Branches.FirstAsync();
            var raw = await db.Warehouses.FirstOrDefaultAsync(w => w.WarehouseType == WarehouseType.RawMaterial);
            if (raw is null) db.Warehouses.Add(raw = new Warehouse { BranchId = branch.Id, Name = "مخزن المواد SA", WarehouseType = WarehouseType.RawMaterial, IsSellableStock = false });
            var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
            // لكل ماكينة مخزن تحت تصنيع خاص بها (فريد)
            var wip = new Warehouse { BranchId = branch.Id, Name = "تحت التصنيع SA", WarehouseType = WarehouseType.WorkInProcess, IsSellableStock = false };
            db.Warehouses.Add(wip);
            var admin = await db.Users.FirstAsync(u => u.Username == AppFixture.AdminUser);
            var pre = new Item { ItemCode = "SA-PRE", ItemName = "امبولة SA", SourcingMethod = SourcingMethod.Purchased, CostPrice = 10 };
            var water = new Item { ItemCode = "SA-W", ItemName = "ماء SA", SalePrice = 250 };
            db.Items.AddRange(pre, water);
            await db.SaveChangesAsync();
            db.ItemPackagingLevels.Add(new ItemPackagingLevel { ItemId = pre.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 });
            db.StockTransactions.Add(new StockTransaction { ItemId = pre.Id, WarehouseId = raw.Id, QuantityBaseUnits = 5_000, UnitCost = 10, TransactionType = StockTransactionType.Receipt, CreatedByUserId = admin.Id });
            var shrink = new ItemPackagingLevel { ItemId = water.Id, LevelName = "شرنك", ContainsQuantity = 20, EquivalentBaseUnits = 20 };
            db.ItemPackagingLevels.AddRange(new ItemPackagingLevel { ItemId = water.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 }, shrink);
            var bom = new BillOfMaterials { FinishedItemId = water.Id };
            bom.Lines.Add(new BOMLine { RawMaterialItemId = pre.Id, QuantityPerUnit = 1, ComponentRole = "امبولة" });
            db.BillOfMaterials.Add(bom);
            var machine = new Machine { Name = "ماكينة SA", MachineType = "تعبئة", WipWarehouseId = wip.Id };
            db.Machines.Add(machine);
            await db.SaveChangesAsync();
            // بيع 45 قطعة قبل الإنتاج (شرنك 20 → يُقرَّب للإنتاج إلى 3 شرنك = 60 قطعة)
            db.PendingProductionShortages.Add(new PendingProductionShortage { ItemId = water.Id, WarehouseId = fg.Id, Quantity = 45, CreatedByUserId = admin.Id });
            await db.SaveChangesAsync();
            waterId = water.Id; boxId = shrink.Id; rawWh = raw.Id; machineId = machine.Id;
        }

        // خدمة المباع مقدمًا: 45 قطعة غير مغطاة، بلا وحدة بيع (لا فاتورة) فتُعرض بالقطع
        await using (var db = _f.NewDb())
        {
            var x = Assert.Single(await new PendingProductionService(db).SoldAheadAsync(), a => a.ItemId == waterId);
            Assert.Equal((45m, 0m, 45m), (x.OpenPieces, x.CoveredByOrders, x.Uncovered));
            Assert.Equal(60m, PendingProductionService.CeilToLevel(45, 20));
            Assert.Equal(45m, PendingProductionService.CeilToLevel(45, 1));
        }

        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var prod = shell.Open<ProductionModuleViewModel>(ModuleCode.Production);
        var orders = prod.Section<ProductionOrdersSectionViewModel>();
        prod.SelectedTab = orders;
        await prod.IdleAsync(); await orders.IdleAsync();

        // مسودة جديدة: يدخل الصنف تلقائيًا بكمية المباع، ويظهر ما يبقى للمخزن
        await orders.NewOrderCommand.ExecuteAsync();
        await orders.IdleAsync();
        var line = Assert.Single(orders.ComposeLines, l => l.FinishedItemId == waterId);
        Assert.Equal(45m, line.Quantity);           // بلا وحدة بيع معروفة: بالقطعة كما بيعت
        Assert.Equal(45m, line.SoldAheadPieces);
        Assert.True(line.HasSoldAhead && !line.IsShort);
        Assert.Contains("مباع مقدمًا", line.SoldAheadText);
        Assert.Contains("للمخزن بعد التسوية: 0 قطعة", line.ToStockText);

        // حذف البند حرّ (لا قفل ولا سبب)
        await orders.RemoveLineCommand.ExecuteAsync(line);
        Assert.DoesNotContain(orders.ComposeLines, l => l.FinishedItemId == waterId);
        orders.CancelComposeCommand.Execute(null);
        Assert.Empty(dialogs.Errors);

        // أمر مفتوح يغطي 60 قطعة: المسودة الجديدة لا تكرره (مغطى)، ولا يظهر في الأوامر بلا حاجة
        await using (var db = _f.NewDb())
        {
            var (r, oid) = await new ProductionService(db).CreateOrderAsync(new[] { new ProductionLineInput(waterId, 60, null, "9001") }, rawWh, machineId, (await db.Users.FirstAsync(u => u.Username == AppFixture.AdminUser)).Id);
            Assert.True(r.Success, r.ErrorMessage);
            var x = Assert.Single(await new PendingProductionService(db).SoldAheadAsync(), a => a.ItemId == waterId);
            Assert.Equal(0m, x.Uncovered);                                     // مغطى بالأمر المفتوح
            var excl = Assert.Single(await new PendingProductionService(db).SoldAheadAsync(oid), a => a.ItemId == waterId);
            Assert.Equal(45m, excl.Uncovered);                                 // عند تعديل هذا الأمر نفسه يُحسب المباع كاملًا
        }
        await orders.NewOrderCommand.ExecuteAsync();
        await orders.IdleAsync();
        Assert.DoesNotContain(orders.ComposeLines, l => l.FinishedItemId == waterId);
        orders.CancelComposeCommand.Execute(null);
        Assert.Empty(_f.Unhandled);
    }

    [Fact]
    public void Draft_line_warns_softly_when_quantity_is_below_sold()
    {
        var below = new OrderLineDraft { Quantity = 20, SoldAheadPieces = 40, SaleLevelName = "شرنك", SaleLevelPer = 20 };
        Assert.True(below.IsShort);
        Assert.Contains("1 شرنك (20 قطعة)", below.ToStockText);   // ناقص شرنك واحد
        var enough = new OrderLineDraft { Quantity = 60, SoldAheadPieces = 40, SaleLevelName = "شرنك", SaleLevelPer = 20 };
        Assert.False(enough.IsShort);
        Assert.Equal("للمخزن بعد التسوية: 1 شرنك (20 قطعة)", enough.ToStockText);
        Assert.False(new OrderLineDraft { Quantity = 5 }.HasSoldAhead);
    }
}
