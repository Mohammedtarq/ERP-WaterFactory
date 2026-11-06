using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.ViewModels.Reps;
using ERP.Presentation.ViewModels.Shell;
using ERP.Presentation.ViewModels.Warehouse;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// من طلب التحميل إلى أمين المخزن: «المتاح في المخزن» يظهر عند كتابة الطلب (بالأحمر إن لم يكفِ)، والجرس ينبّه أمين المخزن
/// ويفتح «طلبات التجهيز» في وحدة المخازن، والتجهيز يُمنع فوق المتاح ويُخرج المتاح للسيارة.
/// </summary>
[Collection("app")]
public class WarehousePrepareScreenTests
{
    private readonly AppFixture _f;
    public WarehousePrepareScreenTests(AppFixture f) => _f = f;

    [Fact]
    public async Task Bell_opens_warehouse_prepare_screen_with_available_stock_and_blocks_over_preparing()
    {
        int orderId, vanId, itemId, fgId;
        await using (var db = _f.NewDb())
        {
            var admin = await db.Users.FirstAsync(u => u.Username == AppFixture.AdminUser);
            var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods && w.IsActive);
            var water = new Item { ItemCode = "PRP-S20", ItemName = "ماء التجهيز شرنك", SalePrice = 250, CostPrice = 100 };
            var rep = new Employee { FullName = "مندوب التجهيز", IsSalesRep = true, BaseSalary = 500_000 };
            db.AddRange(water, rep);
            await db.SaveChangesAsync();
            var shrink = new ItemPackagingLevel { ItemId = water.Id, LevelName = "شرنك", EquivalentBaseUnits = 20 };
            db.ItemPackagingLevels.AddRange(new ItemPackagingLevel { ItemId = water.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 }, shrink);
            db.StockTransactions.Add(new StockTransaction { ItemId = water.Id, WarehouseId = fg.Id, QuantityBaseUnits = 100, UnitCost = 100,
                                                            TransactionType = StockTransactionType.Receipt, CreatedByUserId = admin.Id });
            var van = new Warehouse { BranchId = fg.BranchId, Name = "سيارة التجهيز", WarehouseType = WarehouseType.RepVan, OwnerEmployeeId = rep.Id };
            db.Warehouses.Add(van);
            await db.SaveChangesAsync();
            var (r, order) = await new RepOperationsService(db).CreateLoadOrderAsync(van.Id, fg.Id, DateTime.Today,
                new[] { new RepLoadLineInput(water.Id, shrink.Id, 8) }, null, admin.Id);
            Assert.True(r.Success, r.ErrorMessage);
            (orderId, vanId, itemId, fgId) = (order!.Id, van.Id, water.Id, fg.Id);
        }

        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        await shell.RefreshAlertsAsync();
        var alert = shell.Alerts.Single(a => a.Key == "load-orders");
        Assert.Contains("مندوب التجهيز", alert.Detail);
        Assert.True(shell.HasAlerts);

        // الجرس يفتح «طلبات التجهيز» في وحدة المخازن
        var opened = shell.OpenAlert(alert);
        var screen = Assert.IsType<PrepareLoadOrdersSectionViewModel>(opened);
        var warehouse = Assert.IsType<WarehouseModuleViewModel>(shell.CurrentModule);
        Assert.Same(screen, warehouse.SelectedTab);
        await warehouse.IdleAsync();
        await warehouse.LastActivation;
        await screen.IdleAsync();
        screen.SelectedOrder = screen.Orders.Single(o => o.Id == orderId);
        await screen.IdleAsync();
        var line = Assert.Single(screen.PrepareLines);
        Assert.Equal("5 شرنك", line.AvailableText);                         // 100 قطعة متاحة
        Assert.True(line.IsShort);                                           // طُلب 8 شرنك

        await screen.PrepareCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("لا يكفي"));
        line.Prepared = 5;
        Assert.False(line.IsShort);
        await screen.PrepareCommand.ExecuteAsync();
        Assert.Contains("جُهّزت", screen.StatusMessage);
        Assert.DoesNotContain(screen.Orders, o => o.Id == orderId && o.Status == RepLoadOrderStatus.Pending);
        await using (var db = _f.NewDb())
            Assert.Equal(100m, await db.StockTransactions.Where(t => t.WarehouseId == vanId && t.ItemId == itemId).SumAsync(t => t.QuantityBaseUnits));
        await shell.RefreshAlertsAsync();
        Assert.DoesNotContain(shell.Alerts, a => a.Key == "load-orders" && a.Detail.Contains("مندوب التجهيز"));

        // كتابة طلب جديد: المتاح يظهر، والنقص بالأحمر قبل الإرسال
        var reps = shell.Open<RepsModuleViewModel>(ModuleCode.Reps);
        var orders = reps.LoadOrders;
        reps.SelectedTab = orders;
        await reps.IdleAsync();
        await orders.IdleAsync();
        orders.Van = orders.Vans.Single(v => v.Id == vanId);
        orders.Store = orders.Stores.Single(w => w.Id == fgId);
        await orders.IdleAsync();
        orders.Lines.Clear();
        var draft = new LoadLineDraft(orders);
        orders.Lines.Add(draft);
        draft.Product = orders.Products.Single(p => p.Id == itemId);
        draft.Quantity = 1;
        Assert.Equal("0", draft.AvailableText);                              // نُقل كله للسيارة
        Assert.True(draft.IsShort);
        Assert.Contains("لا يكفي", orders.ShortageText);
        Assert.Empty(_f.Unhandled);
    }
}
