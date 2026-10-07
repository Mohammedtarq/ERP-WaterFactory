using ERP.Cloud.Contracts;
using System.Text.Json;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>
/// تطبيق المندوبين — المرحلة 0: حركات كأنها من الهاتف. تلقائي: زبون جديد، بيع نقدي، آجل (للاطلاع، يتجاوز الحد بتنبيه)،
/// مجاني، تحصيل جزئي؛ وبانتظار الاعتماد: المصروف بصورة الوصل (يمنع التسوية حتى يُحسم). الرقم الفريد يمنع التكرار.
/// </summary>
[Collection("controls")]
public class RepAppTests
{
    private readonly ControlsFixture _f;
    public RepAppTests(ControlsFixture f) => _f = f;

    private static RepRequestEnvelope Env(RepRequestKind kind, object payload, Guid? id = null, byte[]? photo = null) =>
        new(id ?? Guid.NewGuid(), kind, DateTime.Now, JsonSerializer.Serialize(payload), photo);

    [Fact]
    public async Task Phone_requests_post_by_policy_once_each_and_expense_waits_for_approval()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
        var water = new Item { ItemCode = "APP-S20", ItemName = "ماء تطبيق شرنك", SalePrice = 250 };
        var rep = new Employee { FullName = "مندوب التطبيق", IsSalesRep = true, BaseSalary = 500_000 };
        var shop = new Customer { Name = "محل التطبيق", CreditLimit = 5_000 };
        db.AddRange(water, rep, shop);
        await db.SaveChangesAsync();
        var piece = new ItemPackagingLevel { ItemId = water.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 };
        var shrink = new ItemPackagingLevel { ItemId = water.Id, LevelName = "شرنك", EquivalentBaseUnits = 20 };
        db.ItemPackagingLevels.AddRange(piece, shrink);
        db.StockTransactions.Add(new StockTransaction { ItemId = water.Id, WarehouseId = fg.Id, QuantityBaseUnits = 2_000, UnitCost = 100,
                                                        TransactionType = StockTransactionType.Receipt, CreatedByUserId = _f.AdminId });
        var van = new Warehouse { BranchId = fg.BranchId, Name = "سيارة التطبيق", WarehouseType = WarehouseType.RepVan, OwnerEmployeeId = rep.Id };
        db.Warehouses.Add(van);
        await db.SaveChangesAsync();
        var ops = new RepOperationsService(db);
        var (created, order) = await ops.CreateLoadOrderAsync(van.Id, fg.Id, DateTime.Today, new[] { new RepLoadLineInput(water.Id, shrink.Id, 20) }, null, _f.AdminId);
        Assert.True(created.Success, created.ErrorMessage);
        Assert.True((await ops.PrepareLoadOrderAsync(order!.Id, null, _f.AdminId)).result.Success);

        var app = new RepAppService(db);
        var (registered, key) = await app.RegisterDeviceAsync(rep.Id, "هاتف المندوب", _f.AdminId);
        Assert.True(registered.Success, registered.ErrorMessage);
        Assert.Equal(64, key!.Length);
        Assert.False((await app.ReceiveAsync("مفتاح-خطأ", Env(RepRequestKind.NewCustomer, new NewCustomerPayload("س")))).Accepted);

        // 1) زبون جديد: يُربط بالمندوب فورًا بلا موافقة
        var newCustomerId = Guid.NewGuid();
        var nc = await app.ReceiveAsync(key, Env(RepRequestKind.NewCustomer, new NewCustomerPayload("بقالة الجديد", "07701234567"), newCustomerId));
        Assert.Equal(RepRequestStatus.Posted, nc.Status);
        Assert.True(await db.RepCustomerAssignments.AnyAsync(a => a.EmployeeId == rep.Id && a.CustomerId == nc.ResultId));

        // 2) بيع نقدي للزبون الجديد (بمرجعه من الهاتف) — وإعادة الإرسال لا تكرر الفاتورة
        var cashId = Guid.NewGuid();
        var cashEnv = Env(RepRequestKind.CashSale, new SalePayload(new CustomerRef(NewCustomerClientId: newCustomerId), new() { new SaleLinePayload(water.Id, shrink.Id, 3) }), cashId);
        var cash = await app.ReceiveAsync(key, cashEnv);
        Assert.True(cash.Accepted, cash.Message);
        Assert.Equal(RepRequestStatus.Posted, cash.Status);
        var again = await app.ReceiveAsync(key, cashEnv);
        Assert.Equal(cash.RequestId, again.RequestId);
        Assert.Equal(1, await db.SalesInvoices.CountAsync(i => i.CustomerId == nc.ResultId));
        var cashInvoice = await db.SalesInvoices.AsNoTracking().SingleAsync(i => i.Id == cash.ResultId);
        Assert.Equal((van.Id, rep.Id, 15_000m), (cashInvoice.WarehouseId, cashInvoice.SalesRepEmployeeId!.Value, cashInvoice.TotalAmount));   // 3 × 20 × 250
        Assert.Equal(15_000m, await new RepsService(db).GetWalletBalanceAsync(rep.Id));

        // 3) آجل يتجاوز حد الدين: يُرحَّل بتنبيه (قرار الإدارة)، ويظهر للاطلاع
        var credit = await app.ReceiveAsync(key, Env(RepRequestKind.CreditSale, new SalePayload(new CustomerRef(shop.Id), new() { new SaleLinePayload(water.Id, shrink.Id, 2) })));
        Assert.Equal(RepRequestStatus.Posted, credit.Status);
        Assert.Contains("تجاوز حد الدين", credit.Warning);
        // ومنع التجاوز من الإعدادات: يتعذّر دون أن يترك مسودة
        Assert.True((await app.SaveSettingsAsync(false, 2, _f.AdminId)).Success);
        var blocked = await app.ReceiveAsync(key, Env(RepRequestKind.CreditSale, new SalePayload(new CustomerRef(shop.Id), new() { new SaleLinePayload(water.Id, shrink.Id, 1) })));
        Assert.False(blocked.Accepted);
        Assert.Contains("حد دينه", blocked.Message);
        Assert.Equal(1, await db.SalesInvoices.CountAsync(i => i.CustomerId == shop.Id));
        Assert.True((await app.SaveSettingsAsync(true, 2, _f.AdminId)).Success);

        // 4) مجاني بسبب: يُرحَّل للاطلاع، ويُطرح من حافز المندوب
        var free = await app.ReceiveAsync(key, Env(RepRequestKind.Free, new SalePayload(new CustomerRef(shop.Id), new() { new SaleLinePayload(water.Id, shrink.Id, 1) }, "ضيافة")));
        Assert.Equal(RepRequestStatus.Posted, free.Status);
        Assert.False((await app.ReceiveAsync(key, Env(RepRequestKind.Free, new SalePayload(new CustomerRef(shop.Id), new() { new SaleLinePayload(water.Id, shrink.Id, 1) })))).Accepted);
        Assert.Equal(1m, (await new RepIncentiveService(db).RowsAsync(DateTime.Today, DateTime.Today, rep.Id)).Single().Free);

        // 5) تحصيل جزء من الدين
        var collected = await app.ReceiveAsync(key, Env(RepRequestKind.Collection, new CollectionPayload(new CustomerRef(shop.Id), 4_000)));
        Assert.Equal(RepRequestStatus.Posted, collected.Status);
        Assert.Equal(19_000m, await new RepsService(db).GetWalletBalanceAsync(rep.Id));

        // 6) مصروف بصورة الوصل: بانتظار الاعتماد، ويمنع التسوية حتى يُحسم
        var expense = await app.ReceiveAsync(key, Env(RepRequestKind.Expense, new ExpensePayload(3_000, "وقود"), photo: new byte[] { 1, 2, 3 }));
        Assert.Equal(RepRequestStatus.Pending, expense.Status);
        Assert.Contains("بانتظار الاعتماد", (await ops.SettleAsync(new RepSettlementRequest(van.Id, fg.Id, DateTime.Today, Array.Empty<StockDocumentLineInput>(),
            Array.Empty<RepFreeLineInput>(), Array.Empty<RepExpenseInput>(), 0, _f.AdminId))).result.ErrorMessage);
        Assert.False((await app.RejectAsync(expense.RequestId!.Value, " ", _f.AdminId)).Success);     // السبب إجباري
        Assert.False((await app.RejectAsync(credit.RequestId!.Value, "خطأ", _f.AdminId)).Success);    // الآجل لا يُرفض
        Assert.True((await app.ApproveAsync(expense.RequestId.Value, _f.AdminId)).Success);
        Assert.Equal(16_000m, await new RepsService(db).GetWalletBalanceAsync(rep.Id));
        Assert.Equal(new byte[] { 1, 2, 3 }, await app.PhotoAsync(expense.RequestId.Value));

        // 7) قائمة الاطلاع: الآجل والمجاني، ثم «تمت المراجعة»
        var toReview = await app.ListAsync(RepRequestFilter.ToReview, DateTime.Today, DateTime.Today, rep.Id);
        Assert.Equal(new[] { RepRequestKind.Free, RepRequestKind.CreditSale }, toReview.Select(r => r.Kind));
        Assert.True((await app.MarkReviewedAsync(toReview.Select(r => r.Id).ToList(), _f.AdminId)).Success);
        Assert.Empty(await app.ListAsync(RepRequestFilter.ToReview, DateTime.Today, DateTime.Today, rep.Id));
        Assert.Empty(await app.ListAsync(RepRequestFilter.Pending, DateTime.Today, DateTime.Today, rep.Id));

        // 8) حركة ناقصة من الهاتف تُرفض برسالة (لا تعطل الخدمة)، والجهاز الموقوف لا يرسل
        var broken = await app.ReceiveAsync(key, Env(RepRequestKind.Return, new { }));
        Assert.False(broken.Accepted);
        Assert.Contains("الزبون غير موجود", broken.Message);
        var device = await db.RepDevices.SingleAsync(d => d.DeviceKey == key);
        Assert.NotNull(device.LastSeenAt);
        Assert.True((await app.SetDeviceActiveAsync(device.Id, false, _f.AdminId)).Success);
        Assert.Contains("موقوف", (await app.ReceiveAsync(key, Env(RepRequestKind.Collection, new CollectionPayload(new CustomerRef(shop.Id), 1)))).Message);

        // لوحة السيارات: آخر اتصال وبلا طلبات معلّقة
        var card = (await new RepVanBoardService(db).CardsAsync(DateTime.Today)).Single(c => c.VanWarehouseId == van.Id);
        Assert.NotNull(card.LastSeenAt);
        Assert.Equal(0, card.PendingRequests);
    }
}
