using System.Text.Json;
using ERP.Cloud.Contracts;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>
/// جرس التنبيهات: كل مستخدم يرى ما يخص عمله فقط — أمين المخزن يرى طلبات التجهيز والسيارات التي لم تُسلّم مرتجعها ونقص المواد،
/// ولا يرى طلبات الاعتماد ولا أوامر الشراء؛ والتنبيه يزول حين يُعالج سببه.
/// </summary>
[Collection("controls")]
public class AlertsTests
{
    private readonly ControlsFixture _f;
    public AlertsTests(ControlsFixture f) => _f = f;

    private static UserPermissions Perms(params string[] modules) =>
        new(modules.Select(m => new RolePermission { ModuleCode = m, CanView = true, CanAdd = true, CanEdit = true }));

    [Fact]
    public async Task Each_role_sees_its_alerts_and_they_clear_when_handled()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
        var water = new Item { ItemCode = "AL-S20", ItemName = "ماء التنبيهات", SalePrice = 250, CostPrice = 100, MinStockAlertLevel = 100_000 };
        var rep = new Employee { FullName = "مندوب التنبيهات", IsSalesRep = true, BaseSalary = 500_000 };
        var customer = new Customer { Name = "زبون التنبيهات" };
        var supplier = new Supplier { Name = "مورد التنبيهات" };
        db.AddRange(water, rep, customer, supplier);
        await db.SaveChangesAsync();
        var shrink = new ItemPackagingLevel { ItemId = water.Id, LevelName = "شرنك", EquivalentBaseUnits = 20 };
        db.ItemPackagingLevels.AddRange(new ItemPackagingLevel { ItemId = water.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 }, shrink);
        db.StockTransactions.Add(new StockTransaction { ItemId = water.Id, WarehouseId = fg.Id, QuantityBaseUnits = 400, UnitCost = 100,
                                                        TransactionType = StockTransactionType.Receipt, CreatedByUserId = _f.AdminId });
        var van = new Warehouse { BranchId = fg.BranchId, Name = "سيارة التنبيهات", WarehouseType = WarehouseType.RepVan, OwnerEmployeeId = rep.Id };
        db.Warehouses.Add(van);
        await db.SaveChangesAsync();

        var storekeeper = Perms(ModuleCode.Warehouse);
        var admin = Perms(ModuleCode.Warehouse, ModuleCode.Reps, ModuleCode.Suppliers, ModuleCode.Production, SpecialPermission.RepApproval);
        var alerts = new AlertsService(db);

        // 1) طلب تحميل بانتظار التجهيز (أمس) → عند أمين المخزن، ويفتح شاشة «طلبات التجهيز» في المخازن
        var ops = new RepOperationsService(db);
        var (_, order) = await ops.CreateLoadOrderAsync(van.Id, fg.Id, DateTime.Today.AddDays(-1), new[] { new RepLoadLineInput(water.Id, shrink.Id, 10) }, null, _f.AdminId);
        var load = (await alerts.ForAsync(storekeeper)).Single(a => a.Key == "load-orders");
        Assert.Contains("مندوب التنبيهات", load.Detail);
        Assert.Equal((AlertLevel.Danger, ModuleCode.Warehouse, "PrepareLoadOrdersSectionViewModel"), (load.Level, load.ModuleCode, load.Section));
        // نقص المواد: الرصيد تحت حد التنبيه
        Assert.Contains("ماء التنبيهات", (await alerts.ForAsync(storekeeper)).Single(a => a.Key == "low-stock").Detail);

        // 2) جُهّز أمس ولم يُسوَّ → مرتجع بانتظار الاستلام، ويزول بعد التسوية
        Assert.True((await ops.PrepareLoadOrderAsync(order!.Id, null, _f.AdminId)).result.Success);
        var after = await alerts.ForAsync(storekeeper);
        Assert.DoesNotContain(after, a => a.Key == "load-orders" && a.Detail.Contains("مندوب التنبيهات"));
        Assert.Contains("مندوب التنبيهات", after.Single(a => a.Key == "van-returns").Detail);

        // 3) طلبات المندوب بانتظار الاعتماد (مرتجع زبون) → للمعتمِد فقط
        var app = new RepAppService(db);
        var (_, key) = await app.RegisterDeviceAsync(rep.Id, "هاتف التنبيهات", _f.AdminId);
        var ret = await app.ReceiveAsync(key!, new RepRequestEnvelope(Guid.NewGuid(), RepRequestKind.Return, DateTime.Now,
            JsonSerializer.Serialize(new ReturnPayload(new CustomerRef(customer.Id), new() { new ReturnLinePayload(water.Id, shrink.Id, 1) }, "تسرب"))));
        Assert.Equal(RepRequestStatus.Pending, ret.Status);
        Assert.Contains("مرتجع زبون", (await alerts.ForAsync(admin)).Single(a => a.Key == "rep-requests").Detail);
        Assert.DoesNotContain(await alerts.ForAsync(storekeeper), a => a.Key == "rep-requests");

        // 4) أمر شراء مُرسل بانتظار الاستلام → للمشتريات فقط
        db.PurchaseOrders.Add(new PurchaseOrder { PONumber = "PO-AL-1", SupplierId = supplier.Id, WarehouseId = fg.Id, OrderDate = DateTime.Today, Status = PurchaseOrderStatus.Sent, CreatedByUserId = _f.AdminId });
        await db.SaveChangesAsync();
        Assert.Contains("مورد التنبيهات", (await alerts.ForAsync(admin)).Single(a => a.Key == "purchase-receipt").Detail);
        Assert.DoesNotContain(await alerts.ForAsync(storekeeper), a => a.Key == "purchase-receipt");

        // التسوية تستلم المرتجع: يزول تنبيه السيارة
        Assert.True((await app.RejectAsync(ret.RequestId!.Value, "اختبار", _f.AdminId)).Success);
        var (settled, _) = await ops.SettleAsync(new RepSettlementRequest(van.Id, fg.Id, DateTime.Today,
            new[] { new StockDocumentLineInput(water.Id, shrink.Id, 10) }, Array.Empty<RepFreeLineInput>(), Array.Empty<RepExpenseInput>(), 0, _f.AdminId));
        Assert.True(settled.Success, settled.ErrorMessage);
        Assert.DoesNotContain(await alerts.ForAsync(admin), a => a.Key == "van-returns" && a.Detail.Contains("مندوب التنبيهات"));

        // بلا صلاحيات: لا تنبيهات
        Assert.Empty(await alerts.ForAsync(Perms()));
    }
}
