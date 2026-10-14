using ERP.Cloud.Contracts;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.RepApp.Core;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Cloud.Tests;

/// <summary>
/// تطبيق المندوب (منطق الهاتف بلا واجهة) من طرف إلى طرف: ربط برمز QR، ونسخة عمل محفوظة، وبيع وتحصيل بلا إنترنت
/// بأرصدة حيّة، ثم مزامنة تصل المعمل وتعود نتائجها — بلا تكرار ولا طرح مزدوج بعد وصول نسخة العمل الجديدة.
/// </summary>
[Collection("cloud")]
public class PhoneAppTests : CloudTestBase, IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rep-app-" + Guid.NewGuid().ToString("N"));

    public PhoneAppTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Expense_draft_needs_the_factory_copy_a_kind_and_an_amount()
    {
        var work = new RepWork(new LocalStore(Path.Combine(_dir, "expense.db")));
        Assert.Contains("لم تصل نسخة العمل", work.CheckExpense("وقود", 1_000).Error);        // قبل أن تصل نسخة العمل لا حفظ
        Assert.Throws<InvalidOperationException>(() => work.SaveExpense("وقود", 1_000));
        Assert.Contains("وقود", RepWork.ExpenseKinds);
        Assert.Equal("أخرى", RepWork.ExpenseKinds[^1]);
    }

    [Fact]
    public void Link_code_round_trip_and_url_rules()
    {
        var key = new string('A', 64);
        var (code, error) = LinkCode.Parse(new LinkCode("https://api.example.com/", key).Encode());
        Assert.Null(error);
        Assert.Equal(("https://api.example.com/", key), (code!.ServerUrl + "/", code.DeviceKey));
        Assert.Null(LinkCode.Parse(new LinkCode("http://192.168.1.10:5080", key).Encode()).error);            // واي فاي المعمل
        Assert.Contains("مشفّرًا", LinkCode.Parse(new LinkCode("http://example.com", key).Encode()).error);
        Assert.Contains("مفتاح الجهاز", LinkCode.Parse(new LinkCode("https://a.com", "123").Encode()).error);
        Assert.Contains("ليس رمز ربط", LinkCode.Parse("مرحبا").error);
        Assert.Equal("12 شرنك و5 قطعة", Formats.Packs(245, new List<SnapshotLevel> { new(1, "قطعة", 1), new(2, "شرنك", 20) }));
    }

    [Fact]
    public async Task Rep_day_offline_then_sync_with_live_balances_and_no_double_counting()
    {
        var seed = await SeedRepAsync("هاتف", creditLimit: 30_000);
        const string baseUrl = "http://localhost/";                                   // عنوان الخادم في الذاكرة
        var agentKey = await EnableSyncAsync(baseUrl);
        await using var cloudWithKey = Cloud(agentKey);
        using var agent = CloudSyncService.CreateClient(baseUrl, agentKey, cloudWithKey.Server.CreateHandler());
        var agentState = new CloudSyncState();
        async Task Factory()
        {
            await using var db = NewDb();
            var r = await new CloudSyncService(db).RunOnceAsync(agent, "SERVER-PC", agentState);
            Assert.True(r.Ok, r.Error);
        }

        // ---- الربط: الرمز كما يعرضه المعمل ----
        string deviceKey;
        await using (var db = NewDb()) deviceKey = (await new RepAppService(db).RegisterDeviceAsync(seed.RepId, "هاتف الاختبار", AdminId)).deviceKey!;
        var (link, linkError) = LinkCode.Parse(new LinkCode(baseUrl, deviceKey).Encode());
        Assert.Null(linkError);
        var store = new LocalStore(Path.Combine(_dir, "rep.db"));
        store.SaveLink(link!);
        var online = true;
        var sync = new SyncEngine(store, () => new RepCloudClient(store.Link!, online ? cloudWithKey.Server.CreateHandler() : new OfflineHandler()));
        var work = new RepWork(store);

        // هوية المعمل (الاسم والشعار) تصل الهاتف مع نسخة العمل
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        await using (var db = NewDb())
            Assert.True((await new CompanyProfileService(db).SaveAsync(new CompanyProfile { NameAr = "معمل الرحمة للمياه", BrandColor = "#1D4ED8", Logo = png })).Success);

        // قبل أن يرى الخادم الجهاز: مرفوض برسالة واضحة
        Assert.Contains("غير مسجّل", (await sync.RunAsync()).Error);
        await Factory();
        var first = await sync.RunAsync();
        Assert.True(first.Online, first.Error);
        Assert.True(first.SnapshotUpdated);
        Assert.NotNull(first.FactoryLastSeenUtc);
        Assert.Equal(200m, work.VanStock()[seed.WaterId]);
        Assert.Equal("زبون هاتف", Assert.Single(work.Customers()).Name);
        Assert.Equal("معمل الرحمة للمياه", work.CompanyName);
        Assert.Equal(png, work.Snapshot!.CompanyLogo);

        // قائمة البيع: ما في السيارة فقط بكل عبوة (الأكبر أولًا)
        var options = work.LoadedOptions();
        Assert.Equal(new[] { "شرنك 20 — ماء هاتف شرنك", "قطعة — ماء هاتف شرنك" }, options.Select(o => o.Text));   // الاسم الموحد (الحجم غير مضبوط)
        Assert.Equal(new[] { 10m, 200m }, options.Select(o => o.Available));

        // ---- يوم بلا إنترنت: كل شيء يُحفظ في الهاتف والأرصدة تتحرك ----
        online = false;
        Assert.Contains("رصيد السيارة", work.CheckSale("CashSale", seed.CustomerId, new[] { new SaleLine(seed.WaterId, seed.ShrinkId, 11) }).Error);
        var cash = work.SaveSale("CashSale", seed.CustomerId, new[] { new SaleLine(seed.WaterId, seed.ShrinkId, 2) });
        Assert.Equal(10_000m, cash.Amount);                                         // 250 × 20 × 2
        Assert.Equal(1, cash.ReceiptNo);
        Assert.Equal(new ReceiptLine("شرنك 20 — ماء هاتف شرنك", "شرنك", 2, 5_000, 10_000), Assert.Single(cash.Lines!));
        // الوصل: الشعار ورقم الوصل والمواد والمندوب والشكر
        var receipt = work.ReceiptFor(cash)!;
        Assert.Equal(($"{seed.RepId}-1", "بيع نقدي", "زبون هاتف", "مندوب هاتف", 10_000m), (receipt.Number, receipt.Kind, receipt.Customer, receipt.Rep, receipt.Total));
        Assert.Equal(png, receipt.Logo);
        Assert.Equal(new[]
        {
            "معمل الرحمة للمياه", $"وصل بيع نقدي رقم {seed.RepId}-1", cash.OccurredAt.ToString("yyyy/MM/dd HH:mm"), "الزبون: زبون هاتف",
            "شرنك 20 — ماء هاتف شرنك", "2 × 5,000 د.ع = 10,000 د.ع", "الإجمالي: 10,000 د.ع", "المندوب: مندوب هاتف", "شكرا لتعاملكم معنا"
        }, receipt.TextLines());
        var credit = work.CheckSale("CreditSale", seed.CustomerId, new[] { new SaleLine(seed.WaterId, seed.ShrinkId, 4) });
        Assert.Null(credit.Warning);
        work.SaveSale("CreditSale", seed.CustomerId, new[] { new SaleLine(seed.WaterId, seed.ShrinkId, 4) });
        var over = work.CheckSale("CreditSale", seed.CustomerId, new[] { new SaleLine(seed.WaterId, seed.ShrinkId, 3) });
        Assert.True(over.Ok);                                                       // مسموح بتنبيه (إعداد المعمل)
        Assert.Contains("يتجاوز حد دينه", over.Warning);
        Assert.Contains("سببًا", work.CheckSale("Free", seed.CustomerId, new[] { new SaleLine(seed.WaterId, seed.ShrinkId, 1) }).Error);
        Assert.Null(work.ReceiptFor(work.SaveCollection(seed.CustomerId, 5_000)));    // التحصيل بلا وصل بيع
        // «حركاتي»: الوصل التالي برقم 2، والتحصيل بلا وصل بيع، ومجموع مواد اليوم
        Assert.Equal(new int?[] { null, 2, 1 }, store.Outbox().Select(i => i.ReceiptNo));
        Assert.Equal(4m, store.Outbox()[1].Lines!.Single().Quantity);
        Assert.Equal(new DayItemTotal("شرنك 20 — ماء هاتف شرنك", "شرنك", 6), Assert.Single(work.TodayItems()));
        Assert.Equal(new[] { 4m, 80m }, work.LoadedOptions().Select(o => o.Available));

        Assert.Equal(80m, work.VanStock()[seed.WaterId]);                           // 200 − 6 شرنك
        Assert.Equal(15_000m, work.Wallet());                                       // 10,000 نقدي + 5,000 تحصيل
        Assert.Equal(15_000m, work.Customer(seed.CustomerId)!.Balance);             // 20,000 آجل − 5,000
        Assert.Equal(30_000m, work.TodaySales());
        Assert.Equal(3, work.WaitingCount());
        var offline = await sync.RunAsync();
        Assert.False(offline.Online);
        Assert.Null(offline.Error);                                                 // لا شبكة ليس خطأً
        Assert.All(store.Outbox(), i => Assert.Equal("بانتظار الإرسال", i.StateText));

        // ---- عادت الشبكة: الإرسال، ثم المعمل يرحّل، ثم النتائج ونسخة العمل ----
        online = true;
        var sent = await sync.RunAsync();
        Assert.Equal(3, sent.Sent);
        Assert.All(store.Outbox(), i => Assert.Equal(OutboxState.Sent, i.State));
        Assert.Equal(80m, work.VanStock()[seed.WaterId]);                           // ما زالت محسوبة
        await Factory();
        var back = await sync.RunAsync();
        Assert.Equal(3, back.Updated);
        Assert.True(back.SnapshotUpdated);
        Assert.All(store.Outbox(), i => Assert.Equal("رُحّلت", i.StateText));
        // نسخة العمل الجديدة فيها الحركات: لا طرح مزدوج
        Assert.Equal(80m, work.VanStock()[seed.WaterId]);
        Assert.Equal(15_000m, work.Wallet());
        Assert.Equal(15_000m, work.Customer(seed.CustomerId)!.Balance);
        Assert.Equal(0, work.WaitingCount());
        await using (var db = NewDb())
        {
            Assert.Equal(3, await db.RepRequests.CountAsync(r => r.RepEmployeeId == seed.RepId && r.Status == RepRequestStatus.Posted));
            Assert.Equal(80m, await db.StockTransactions.Where(t => t.WarehouseId == seed.VanId).SumAsync(t => t.QuantityBaseUnits));
        }

        // ---- الإرسال المتكرر آمن ----
        Assert.Equal(0, (await sync.RunAsync()).Sent);

        // ---- إعادة فتح التطبيق: كل شيء محفوظ ----
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var reopened = new RepWork(new LocalStore(Path.Combine(_dir, "rep.db")));
        Assert.Equal(80m, reopened.VanStock()[seed.WaterId]);
        Assert.Equal(3, new LocalStore(Path.Combine(_dir, "rep.db")).Outbox().Count);

        // ---- فك الربط يُمنع وحركات لم تصل ----
        online = false;
        work.SaveCollection(seed.CustomerId, 1_000);
        Assert.Contains("لم تصل المعمل", store.Unlink());

        // ---- تغيّرت شبكة المعمل: تغيير العنوان فقط، والمفتاح والحركات باقية ----
        Assert.Contains("مشفّرًا", store.ChangeServer("http://example.com"));
        Assert.Null(store.ChangeServer("http://192.168.0.191:5080/"));
        Assert.Equal(("http://192.168.0.191:5080", deviceKey), (store.Link!.ServerUrl, store.Link.DeviceKey));
        Assert.Single(store.Queued());
        Assert.Equal(4, store.Outbox().Count);
        Assert.Null(store.ChangeServer(baseUrl));

        // ---- جهاز موقوف ----
        online = true;
        await using (var db = NewDb())
        {
            var id = await db.RepDevices.Where(d => d.DeviceKey == deviceKey).Select(d => d.Id).SingleAsync();
            Assert.True((await new RepAppService(db).SetDeviceActiveAsync(id, false, AdminId)).Success);
        }
        await Factory();
        var rejected = await sync.RunAsync();
        Assert.Contains("موقوف", rejected.Error);
        Assert.Equal(OutboxState.Queued, store.Queued().Single().State);            // تبقى محفوظة

        // ---- حذف الجهاز: له حركات ← يُخفى وتبقى حركاته؛ بلا حركات ← يُحذف نهائيًا ----
        await using (var db = NewDb())
        {
            var app = new RepAppService(db);
            var id = await db.RepDevices.Where(d => d.DeviceKey == deviceKey).Select(d => d.Id).SingleAsync();
            Assert.True((await app.DeleteDeviceAsync(id, AdminId)).Success);
            Assert.DoesNotContain(await app.DevicesAsync(), d => d.Id == id);
            Assert.Equal(3, await db.RepRequests.CountAsync(r => r.DeviceId == id));
            Assert.Contains("غير موجود", (await app.DeleteDeviceAsync(id, AdminId)).ErrorMessage);
            Assert.Contains("غير موجود", (await app.SetDeviceActiveAsync(id, true, AdminId)).ErrorMessage);
            var spareKey = (await app.RegisterDeviceAsync(seed.RepId, "هاتف احتياط", AdminId)).deviceKey;
            var spare = await db.RepDevices.Where(d => d.DeviceKey == spareKey).Select(d => d.Id).SingleAsync();
            Assert.True((await app.DeleteDeviceAsync(spare, AdminId)).Success);
            Assert.False(await db.RepDevices.AnyAsync(d => d.Id == spare));
        }
        await Factory();
        Assert.Contains("موقوف", (await sync.RunAsync()).Error);
    }

    [Fact]
    public async Task New_customer_from_phone_sells_offline_then_reaches_factory_with_desktop_fields()
    {
        var seed = await SeedRepAsync("زبون-جديد");
        const string baseUrl = "http://localhost/";
        var agentKey = await EnableSyncAsync(baseUrl);
        await using var cloud = Cloud(agentKey);
        using var agent = CloudSyncService.CreateClient(baseUrl, agentKey, cloud.Server.CreateHandler());
        var agentState = new CloudSyncState();
        async Task Factory()
        {
            await using var db = NewDb();
            Assert.True((await new CloudSyncService(db).RunOnceAsync(agent, "SERVER-PC", agentState)).Ok);
        }
        string deviceKey;
        await using (var db = NewDb()) deviceKey = (await new RepAppService(db).RegisterDeviceAsync(seed.RepId, "هاتف الزبائن", AdminId)).deviceKey!;
        var store = new LocalStore(Path.Combine(_dir, "new-customer.db"));
        store.SaveLink(new LinkCode(baseUrl, deviceKey));
        var online = true;
        var sync = new SyncEngine(store, () => new RepCloudClient(store.Link!, online ? cloud.Server.CreateHandler() : new OfflineHandler()));
        var work = new RepWork(store);
        await Factory();
        Assert.True((await sync.RunAsync()).SnapshotUpdated);

        // ---- بلا إنترنت: زبون جديد بحقول شاشة العملاء، ويُباع له آجلًا فورًا ----
        online = false;
        Assert.Contains("اسم الزبون", work.CheckNewCustomer(new NewCustomerPayload(" ")).Error);
        Assert.Contains("بوكيل", work.CheckNewCustomer(new NewCustomerPayload("فرعي", Type: "SubCustomer")).Error);
        Assert.Contains("سالبًا", work.CheckNewCustomer(new NewCustomerPayload("س", CreditLimit: -1)).Error);
        Assert.Contains("بالاسم نفسه", work.CheckNewCustomer(new NewCustomerPayload("زبون زبون-جديد")).Warning);
        var added = work.SaveNewCustomer(new NewCustomerPayload(" أسواق النخيل ", "07801112222", "العشار", null, "Direct", null, 50_000));
        Assert.True(added.IsNew);
        Assert.True(added.Id < 0);
        Assert.Equal(2, work.Customers().Count);
        Assert.Equal(added.Id, RepWork.PendingId(added.NewClientId!.Value));
        var sale = work.SaveSale("CreditSale", added.Id, new[] { new SaleLine(seed.WaterId, seed.ShrinkId, 2) });
        Assert.Equal(new CustomerRef(NewCustomerClientId: added.NewClientId), System.Text.Json.JsonSerializer.Deserialize<SalePayload>(sale.Payload,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.Customer);
        Assert.Equal(10_000m, work.Customer(added.Id)!.Balance);

        // ---- عادت الشبكة: الزبون ثم البيع يصلان المعمل بالترتيب ----
        online = true;
        Assert.Equal(2, (await sync.RunAsync()).Sent);
        await Factory();
        var back = await sync.RunAsync();
        Assert.Equal(2, back.Updated);
        Assert.All(store.Outbox(), i => Assert.Equal("رُحّلت", i.StateText));
        int customerId;
        await using (var db = NewDb())
        {
            var c = await db.Customers.SingleAsync(x => x.Name == "أسواق النخيل");
            customerId = c.Id;
            Assert.Equal((CustomerType.Direct, "07801112222", "العشار", "البصرة", (decimal?)50_000), (c.CustomerType, c.Phone, c.Address, c.Province, c.CreditLimit));
            Assert.True(await db.RepCustomerAssignments.AnyAsync(a => a.EmployeeId == seed.RepId && a.CustomerId == c.Id));
            var request = await db.RepRequests.SingleAsync(r => r.Kind == RepRequestKind.NewCustomer && r.RepEmployeeId == seed.RepId);
            Assert.True(request.NeedsReview);                                      // الإدارة تراجع الزبائن الجدد
            Assert.Contains("(مباشر)", request.Summary);
            Assert.Contains("حد الدين 50,000", request.Summary);
            Assert.True(await db.RepRequests.AnyAsync(r => r.Kind == RepRequestKind.CreditSale && r.RepEmployeeId == seed.RepId && r.Status == RepRequestStatus.Posted));
        }
        // بعد نسخة العمل الجديدة: الزبون مرة واحدة برقمه في المعمل ورصيده دون طرح مزدوج
        var live = Assert.Single(work.Customers(), c => c.Name == "أسواق النخيل");
        Assert.False(live.IsNew);
        Assert.Equal((customerId, 10_000m), (live.Id, live.Balance));

        // ---- عميل فرعي لوكيل من زبائن المندوب ----
        await using (var db = NewDb())
        {
            var c = await db.Customers.SingleAsync(x => x.Id == customerId);
            c.CustomerType = CustomerType.Agent;
            await db.SaveChangesAsync();
        }
        agentState.LastSnapshotCheckUtc = null;                                    // تعديل من الشاشة: نسخة العمل في الفحص الدوري التالي
        await Factory();
        await sync.RunAsync();
        var agentRow = Assert.Single(work.Agents());
        var sub = work.SaveNewCustomer(new NewCustomerPayload("كشك الجسر", Type: "SubCustomer", ParentAgentId: agentRow.Id));
        Assert.Equal(agentRow.Id, sub.Customer.PriceAgentId);
        await sync.RunAsync();
        await Factory();
        await sync.RunAsync();
        await using (var db = NewDb())
            Assert.Equal((CustomerType.SubCustomer, (int?)customerId), await db.Customers.Where(x => x.Name == "كشك الجسر").Select(x => new ValueTuple<CustomerType, int?>(x.CustomerType, x.ParentAgentId)).SingleAsync());
        Assert.Single(work.Customers(), c => c.Name == "كشك الجسر");
    }

    [Fact]
    public void EscPos_raster_bands_feed_and_cut_with_dithered_logo()
    {
        Assert.Equal((384, 576), (EscPos.DotsFor(58), EscPos.DotsFor(80)));
        // 10×3: الصف الأول أسود كله، والثاني النقطة الأولى والأخيرة، والثالث أبيض
        var black = new bool[30];
        for (var x = 0; x < 10; x++) black[x] = true;
        black[10] = black[19] = true;
        var bytes = EscPos.Raster(black, 10, 3, feedLines: 3, band: 2);
        Assert.Equal(new byte[] { 0x1B, 0x40 }, bytes[..2]);
        Assert.Equal(new byte[] { 0x1D, 0x76, 0x30, 0x00, 2, 0, 2, 0, 0xFF, 0xC0, 0x80, 0x40 }, bytes[2..14]);   // شريحة صفّين
        Assert.Equal(new byte[] { 0x1D, 0x76, 0x30, 0x00, 2, 0, 1, 0, 0x00, 0x00 }, bytes[14..24]);              // الصف الباقي
        Assert.Equal(new byte[] { 0x1B, 0x64, 3, 0x1D, 0x56, 0x42, 0x00 }, bytes[24..]);
        Assert.Throws<ArgumentException>(() => EscPos.Raster(new bool[2], 10, 3));

        // أبيض وشفاف ← أبيض، أسود ← أسود، والرمادي يتوزع نقاطًا (نصفها تقريبًا)
        Assert.Equal(new[] { false, false, true }, EscPos.ToMono(new[] { unchecked((int)0xFFFFFFFF), 0x00000000, unchecked((int)0xFF000000) }, 3, 1));
        var gray = Enumerable.Repeat(unchecked((int)0xFF808080), 100).ToArray();
        Assert.InRange(EscPos.ToMono(gray, 10, 10).Count(b => b), 40, 60);
    }

    [Fact]
    public void Old_phone_database_is_upgraded_without_losing_movements()
    {
        var path = Path.Combine(_dir, "old.db");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE Settings (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
                CREATE TABLE Outbox (
                    ClientId TEXT PRIMARY KEY, Kind TEXT NOT NULL, OccurredAt TEXT NOT NULL, Payload TEXT NOT NULL, Photo BLOB,
                    Summary TEXT NOT NULL, Amount TEXT NOT NULL, Effect TEXT NOT NULL, CreatedAt TEXT NOT NULL,
                    State INTEGER NOT NULL, Status TEXT, Message TEXT, ResultId INTEGER, Warning TEXT, UpdatedAt TEXT NOT NULL);
                """;
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"INSERT INTO Outbox VALUES ('{Guid.NewGuid()}', 'Collection', '{DateTime.Now:O}', '{{}}', NULL, 'تحصيل قديم', '1000', '{{\"stock\":[]}}', '{DateTime.UtcNow:O}', 0, NULL, NULL, NULL, NULL, '{DateTime.UtcNow:O}')";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var store = new LocalStore(path);
        var old = Assert.Single(store.Outbox());
        Assert.Equal(("تحصيل قديم", (int?)null, (List<ReceiptLine>?)null), (old.Summary, old.ReceiptNo, old.Lines));
        var sale = store.Enqueue("CashSale", new { }, "بيع", 5, Effect.None, lines: new() { new("ماء", "كارتون", 1, 5, 5) }, receipt: true);
        Assert.Equal(1, sale.ReceiptNo);
        Assert.Equal(2, store.Enqueue("CashSale", new { }, "بيع", 5, Effect.None, receipt: true).ReceiptNo);
        Assert.Equal("ماء", store.Find(sale.ClientId)!.Lines!.Single().Item);
    }

    /// <summary>هاتف بلا شبكة.</summary>
    private class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("لا شبكة");
    }
}
