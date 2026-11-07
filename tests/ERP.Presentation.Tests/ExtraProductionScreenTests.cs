using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.ViewModels.Production;
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
}
