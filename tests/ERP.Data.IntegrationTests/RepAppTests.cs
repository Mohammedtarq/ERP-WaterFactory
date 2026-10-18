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

        // المصروف يدخل مجموعة المصروفات بالمالية تحت «سيارة المندوب» باسم المندوب، دون قيد ثانٍ ودون صندوق، ولا يُحسب مرتين
        // يدخل باسم سيارة المندوب نفسها (اسم مخزن سيارته هنا)
        var van3 = await db.FinanceEntries.Include(e => e.Category).SingleAsync(e => e.Category.Name == van.Name);
        Assert.Equal((3_000m, rep.FullName, "وقود"), (van3.Amount, van3.PartyName, van3.Notes));
        Assert.Equal(FinanceCategoryKind.Operating, van3.Category.Kind);
        Assert.Contains(await new FinanceEntryService(db).ListAsync(DateTime.Today, DateTime.Today, van3.CategoryId), r => r.EntryNumber == van3.EntryNumber);
        Assert.Equal(1, await db.JournalEntries.CountAsync(j => j.Id == van3.JournalEntryId));
        Assert.False(await db.CashBoxTransactions.AnyAsync(t => t.ReferenceTable == "FinanceEntries" && t.ReferenceId == van3.Id));
        // إلغاء المصروف يعيد المبلغ لمحفظة المندوب
        Assert.True((await new FinanceEntryService(db).VoidAsync(van3.Id, "خطأ في المبلغ", _f.AdminId)).Success);
        Assert.Equal(19_000m, await new RepsService(db).GetWalletBalanceAsync(rep.Id));
        Assert.True((await new RepsService(db).RecordFieldExpenseAsync(rep.Id, 3_000, "وقود", DateTime.Today, _f.AdminId)).Success);   // يعود كما كان لبقية الاختبار
        Assert.Equal(16_000m, await new RepsService(db).GetWalletBalanceAsync(rep.Id));
        // وإن كانت له سيارة مسجلة (كنتر 1) فباسمها وبرقمها
        var counter = new Vehicle { VehicleName = "كنتر 1", AssignedEmployeeId = rep.Id };
        db.Vehicles.Add(counter);
        await db.SaveChangesAsync();
        Assert.True((await new RepsService(db).RecordFieldExpenseAsync(rep.Id, 500, "غسيل", DateTime.Today, _f.AdminId)).Success);
        var viaVehicle = await db.FinanceEntries.Include(e => e.Category).OrderByDescending(e => e.Id).FirstAsync();
        Assert.Equal(("كنتر 1", counter.Id, 500m), (viaVehicle.Category.Name, viaVehicle.VehicleId, viaVehicle.Amount));
        Assert.Contains(await new FinanceEntryService(db).ListAsync(DateTime.Today, DateTime.Today, viaVehicle.CategoryId, counter.Id), r => r.EntryNumber == viaVehicle.EntryNumber);
        Assert.True((await new FinanceEntryService(db).VoidAsync(viaVehicle.Id, "تجربة", _f.AdminId)).Success);
        Assert.Equal(16_000m, await new RepsService(db).GetWalletBalanceAsync(rep.Id));
        Assert.Equal(new byte[] { 1, 2, 3 }, await app.PhotoAsync(expense.RequestId.Value));

        // 7) قائمة الاطلاع: الآجل والمجاني والزبون الجديد (نوعه وحد دينه)، ثم «تمت المراجعة»
        var toReview = await app.ListAsync(RepRequestFilter.ToReview, DateTime.Today, DateTime.Today, rep.Id);
        Assert.Equal(new[] { RepRequestKind.Free, RepRequestKind.CreditSale, RepRequestKind.NewCustomer }, toReview.Select(r => r.Kind));
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

    /// <summary>دخول التطبيق: حرف إلى 4 خانات، فريد، مجزّأ، ويصل الهاتف في نسخة العمل.</summary>
    [Fact]
    public async Task App_login_is_validated_unique_hashed_and_travels_in_the_snapshot()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var a = new Employee { FullName = "مندوب دخول 1", IsSalesRep = true, BaseSalary = 1 };
        var b = new Employee { FullName = "مندوب دخول 2", IsSalesRep = true, BaseSalary = 1 };
        db.AddRange(a, b);
        await db.SaveChangesAsync();
        var svc = new RepAppService(db);

        Assert.False((await svc.SetLoginAsync(a.Id, "toolong", "12", _f.AdminId)).Success);      // أطول من 4
        Assert.False((await svc.SetLoginAsync(a.Id, "ab", "a b", _f.AdminId)).Success);          // فراغ
        Assert.True((await svc.SetLoginAsync(a.Id, "AB", "12", _f.AdminId)).Success);
        Assert.False((await svc.SetLoginAsync(b.Id, "ab", "34", _f.AdminId)).Success);           // الاسم لمندوب آخر
        Assert.True((await svc.SetLoginAsync(a.Id, "ab", "99", _f.AdminId)).Success);            // تعديل مندوبه نفسه

        var stored = await db.Employees.AsNoTracking().FirstAsync(e => e.Id == a.Id);
        Assert.Equal("ab", stored.RepLoginName);
        // مخزّنة مجزّأة لا نصًا (التجزئة العشوائية قد تحوي «99» صدفةً فلا يُفحص النص الجزئي — كان سبب فشل متقطع)
        Assert.NotEqual("99", stored.RepLoginHash);
        Assert.Equal(32, Convert.FromBase64String(stored.RepLoginHash!).Length);

        var snap = await new CloudSyncService(db).BuildSnapshotAsync(a.Id);
        var login = Assert.IsType<SnapshotLogin>(snap!.Login);
        Assert.True(RepLogin.Verify(login, "AB", "99"));
        Assert.False(RepLogin.Verify(login, "ab", "12"));
        Assert.Null((await new CloudSyncService(db).BuildSnapshotAsync(b.Id))!.Login);

        Assert.True((await svc.ClearLoginAsync(a.Id, _f.AdminId)).Success);
        Assert.Null((await new CloudSyncService(db).BuildSnapshotAsync(a.Id))!.Login);
    }

    /// <summary>
    /// «زبائني من النظام»: المندوب يختار زبائن من غير المربوطين به، والطلب ينتظر موافقة الإدارة —
    /// لا تتغير قائمته قبلها، وبعدها يظهرون في نسخة العمل؛ والمرفوض يبقى بلا ربط.
    /// </summary>
    [Fact]
    public async Task Customer_link_from_phone_waits_for_management_approval()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var rep = new Employee { FullName = "مندوب ربط الزبائن", IsSalesRep = true, BaseSalary = 1 };
        var own = new Customer { Name = "ربط — زبونه الحالي" };
        var a = new Customer { Name = "ربط — مطعم أ" };
        var b = new Customer { Name = "ربط — مقهى ب" };
        var agent = new Customer { Name = "ربط — وكيل", CustomerType = CustomerType.Agent };
        db.AddRange(rep, own, a, b, agent);
        await db.SaveChangesAsync();
        db.RepCustomerAssignments.Add(new RepCustomerAssignment { EmployeeId = rep.Id, CustomerId = own.Id });
        await db.SaveChangesAsync();
        var app = new RepAppService(db);
        var (_, key) = await app.RegisterDeviceAsync(rep.Id, "هاتف الربط", _f.AdminId);

        // نسخة العمل: زبونه فقط في قائمته، وغير المربوطين (المباشرون) للاختيار — بلا الوكيل
        var snap = (await new CloudSyncService(db).BuildSnapshotAsync(rep.Id))!;
        Assert.Equal(new[] { own.Id }, snap.Customers.Select(c => c.Id));
        Assert.Contains(snap.OtherCustomers!, c => c.Id == a.Id);
        Assert.DoesNotContain(snap.OtherCustomers!, c => c.Id == own.Id || c.Id == agent.Id);

        // الطلب بانتظار الموافقة، ولا ربط قبلها
        var sent = await app.ReceiveAsync(key!, Env(RepRequestKind.CustomerLink, new CustomerLinkPayload(new() { a.Id, b.Id, own.Id })));
        Assert.Equal(RepRequestStatus.Pending, sent.Status);
        var request = await db.RepRequests.AsNoTracking().FirstAsync(r => r.Id == sent.RequestId);
        Assert.Contains("مطعم أ", request.Summary);
        Assert.NotNull(request.Warning);                      // زبونه الحالي مربوط أصلًا
        Assert.False(await db.RepCustomerAssignments.AnyAsync(x => x.EmployeeId == rep.Id && x.CustomerId == a.Id));
        Assert.Single((await new CloudSyncService(db).BuildSnapshotAsync(rep.Id))!.Customers);

        Assert.True((await app.ApproveAsync(request.Id, _f.AdminId)).Success);
        var after = (await new CloudSyncService(db).BuildSnapshotAsync(rep.Id))!;
        Assert.Equal(new[] { own.Id, a.Id, b.Id }.OrderBy(x => x), after.Customers.Select(c => c.Id).OrderBy(x => x));
        Assert.DoesNotContain(after.OtherCustomers!, c => c.Id == a.Id);

        // طلب آخر مرفوض: لا ربط
        var c = new Customer { Name = "ربط — محل ج" };
        db.Add(c);
        await db.SaveChangesAsync();
        var second = await app.ReceiveAsync(key!, Env(RepRequestKind.CustomerLink, new CustomerLinkPayload(new() { c.Id })));
        Assert.True((await app.RejectAsync(second.RequestId!.Value, "ليس من منطقته", _f.AdminId)).Success);
        Assert.False(await db.RepCustomerAssignments.AnyAsync(x => x.EmployeeId == rep.Id && x.CustomerId == c.Id));
        // طلب بلا زبون صالح يتعذّر
        Assert.Equal(RepRequestStatus.Failed, (await app.ReceiveAsync(key!, Env(RepRequestKind.CustomerLink, new CustomerLinkPayload(new() { agent.Id })))).Status);
    }
}
