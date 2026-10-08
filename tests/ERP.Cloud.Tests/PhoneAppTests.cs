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

        // قبل أن يرى الخادم الجهاز: مرفوض برسالة واضحة
        Assert.Contains("غير مسجّل", (await sync.RunAsync()).Error);
        await Factory();
        var first = await sync.RunAsync();
        Assert.True(first.Online, first.Error);
        Assert.True(first.SnapshotUpdated);
        Assert.NotNull(first.FactoryLastSeenUtc);
        Assert.Equal(200m, work.VanStock()[seed.WaterId]);
        Assert.Equal("زبون هاتف", Assert.Single(work.Customers()).Name);

        // ---- يوم بلا إنترنت: كل شيء يُحفظ في الهاتف والأرصدة تتحرك ----
        online = false;
        Assert.Contains("رصيد السيارة", work.CheckSale("CashSale", seed.CustomerId, new[] { new SaleLine(seed.WaterId, seed.ShrinkId, 11) }).Error);
        var cash = work.SaveSale("CashSale", seed.CustomerId, new[] { new SaleLine(seed.WaterId, seed.ShrinkId, 2) });
        Assert.Equal(10_000m, cash.Amount);                                         // 250 × 20 × 2
        var credit = work.CheckSale("CreditSale", seed.CustomerId, new[] { new SaleLine(seed.WaterId, seed.ShrinkId, 4) });
        Assert.Null(credit.Warning);
        work.SaveSale("CreditSale", seed.CustomerId, new[] { new SaleLine(seed.WaterId, seed.ShrinkId, 4) });
        var over = work.CheckSale("CreditSale", seed.CustomerId, new[] { new SaleLine(seed.WaterId, seed.ShrinkId, 3) });
        Assert.True(over.Ok);                                                       // مسموح بتنبيه (إعداد المعمل)
        Assert.Contains("يتجاوز حد دينه", over.Warning);
        Assert.Contains("سببًا", work.CheckSale("Free", seed.CustomerId, new[] { new SaleLine(seed.WaterId, seed.ShrinkId, 1) }).Error);
        work.SaveCollection(seed.CustomerId, 5_000);

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
    }

    /// <summary>هاتف بلا شبكة.</summary>
    private class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("لا شبكة");
    }
}
