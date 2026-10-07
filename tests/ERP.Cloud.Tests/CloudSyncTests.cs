using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ERP.Cloud.Contracts;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Data.Setup;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Cloud.Tests;

/// <summary>
/// المرحلة 1 من طرف إلى طرف: خادم سحابي حقيقي (في الذاكرة) بقاعدة وسيطة على SQL Server، وقاعدة معمل حقيقية،
/// و«هاتف» يرسل بمفتاح جهازه. خدمة المزامنة ترحّل بقواعد المعمل وتعيد النتائج ونسخة العمل.
/// </summary>
[Collection("cloud")]
public class CloudSyncTests : CloudTestBase
{
    private const string AgentKeyForTests = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    private static HttpClient Phone(WebApplicationFactory<Program> cloud, string deviceKey)
    {
        var http = cloud.CreateClient();
        http.DefaultRequestHeaders.Add(CloudRoutes.DeviceKeyHeader, deviceKey);
        return http;
    }

    private static PhoneRequest Req(Guid id, string kind, object payload) => new(id, kind, DateTime.Now, JsonSerializer.Serialize(payload));

    private static async Task<PhoneRequestStatus> Send(HttpClient phone, PhoneRequest r)
    {
        var res = await phone.PostAsJsonAsync(CloudRoutes.RepRequests, r);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<PhoneRequestStatus>())!;
    }

    private static async Task<PhoneRequestStatus> StatusOf(HttpClient phone, Guid id)
    {
        var res = await phone.PostAsJsonAsync(CloudRoutes.RepStatuses, new PhoneStatusQuery(new() { id }));
        return Assert.Single((await res.Content.ReadFromJsonAsync<List<PhoneRequestStatus>>())!);
    }

    [Fact]
    public void Phone_kinds_match_the_factory_request_kinds() =>
        Assert.Equal(Enum.GetNames<RepRequestKind>().OrderBy(n => n), RepKinds.All.OrderBy(n => n));

    [Fact]
    public async Task Phone_to_cloud_to_factory_and_back_with_snapshot_approval_retry_and_device_stop()
    {
        // ---- المعمل: مندوب وسيارة محمّلة وزبون آجل ----
        int repId, customerId, waterId, shrinkId, vanId, fgId;
        await using (var db = NewDb())
        {
            var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
            var water = new Item { ItemCode = "CL-S20", ItemName = "ماء السحابة شرنك", SalePrice = 250, CostPrice = 100 };
            var customer = new Customer { Name = "زبون السحابة", CreditLimit = 1_000_000 };
            var rep = new Employee { FullName = "مندوب السحابة", IsSalesRep = true, BaseSalary = 500_000 };
            db.AddRange(water, customer, rep);
            await db.SaveChangesAsync();
            var shrink = new ItemPackagingLevel { ItemId = water.Id, LevelName = "شرنك", EquivalentBaseUnits = 20 };
            db.ItemPackagingLevels.AddRange(new ItemPackagingLevel { ItemId = water.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 }, shrink);
            db.StockTransactions.Add(new StockTransaction { ItemId = water.Id, WarehouseId = fg.Id, QuantityBaseUnits = 1_000, UnitCost = 100,
                                                            TransactionType = StockTransactionType.Receipt, CreatedByUserId = AdminId });
            var van = new Warehouse { BranchId = fg.BranchId, Name = "سيارة السحابة", WarehouseType = WarehouseType.RepVan, OwnerEmployeeId = rep.Id };
            db.Warehouses.Add(van);
            await db.SaveChangesAsync();
            db.RepCustomerAssignments.Add(new RepCustomerAssignment { EmployeeId = rep.Id, CustomerId = customer.Id });
            await db.SaveChangesAsync();
            var ops = new RepOperationsService(db);
            var (_, order) = await ops.CreateLoadOrderAsync(van.Id, fg.Id, DateTime.Today, new[] { new RepLoadLineInput(water.Id, shrink.Id, 10) }, null, AdminId);
            Assert.True((await ops.PrepareLoadOrderAsync(order!.Id, null, AdminId)).result.Success);
            (repId, customerId, waterId, shrinkId, vanId, fgId) = (rep.Id, customer.Id, water.Id, shrink.Id, van.Id, fg.Id);
        }

        // ---- الإعداد: عنوان مشفّر إلزامًا، ومفتاح معمل قبل التفعيل ----
        await using (var db = NewDb())
        {
            var sync = new CloudSyncService(db);
            Assert.Contains("مشفّرًا", (await sync.SaveSettingsAsync("http://api.example.com", true, 20, AdminId)).ErrorMessage);
            Assert.Contains("ولّد مفتاح المعمل", (await sync.SaveSettingsAsync("https://api.example.com", true, 20, AdminId)).ErrorMessage);
        }
        string agentKey;
        await using (var db = NewDb()) agentKey = await new CloudSyncService(db).GenerateKeyAsync(AdminId);
        Assert.Equal(64, agentKey.Length);

        await using var cloud = Cloud(agentKey);
        var baseUrl = cloud.Server.BaseAddress.ToString();
        await using (var db = NewDb()) Assert.True((await new CloudSyncService(db).SaveSettingsAsync(baseUrl, true, 20, AdminId)).Success);
        using var agent = CloudSyncService.CreateClient(baseUrl, agentKey, cloud.Server.CreateHandler());
        using var wrongAgent = CloudSyncService.CreateClient(baseUrl, AgentKeyForTests, cloud.Server.CreateHandler());
        Assert.Equal((false, "مفتاح المعمل غير صحيح"), await CloudSyncService.TestAsync(wrongAgent));
        Assert.True((await CloudSyncService.TestAsync(agent)).ok);
        var state = new CloudSyncState();
        async Task<CloudSyncReport> Run()
        {
            await using var db = NewDb();
            var report = await new CloudSyncService(db).RunOnceAsync(agent, "SERVER-PC", state);
            Assert.True(report.Ok, report.Error);
            return report;
        }

        // ---- الجهاز: مرفوض في الخادم حتى تصله قائمة الأجهزة ----
        string deviceKey;
        await using (var db = NewDb()) deviceKey = (await new RepAppService(db).RegisterDeviceAsync(repId, "هاتف السحابة", AdminId)).deviceKey!;
        using var phone = Phone(cloud, deviceKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync(CloudRoutes.RepPing)).StatusCode);
        var first = await Run();
        Assert.Equal(1, first.Snapshots);
        var ping = await phone.GetFromJsonAsync<PhonePing>(CloudRoutes.RepPing);
        Assert.NotNull(ping!.FactoryLastSeenUtc);

        // ---- نسخة العمل: زبونه وأسعاره ورصيد سيارته ----
        var snapRes = await phone.GetAsync(CloudRoutes.RepSnapshot);
        var etag = snapRes.Headers.ETag!.Tag;
        var snap = (await snapRes.Content.ReadFromJsonAsync<RepSnapshot>(CloudSyncService.Json))!;
        Assert.Equal("مندوب السحابة", snap.RepName);
        Assert.Equal(customerId, Assert.Single(snap.Customers).Id);                         // المسند إليه فقط
        var product = snap.Products.Single(p => p.ItemId == waterId);
        Assert.Equal(("شرنك", 20m), (product.Levels[0].Name, product.Levels[0].Units));      // الأكبر أولًا
        Assert.Equal(200m, snap.VanStock.Single(s => s.ItemId == waterId).Pieces);
        var notModified = new HttpRequestMessage(HttpMethod.Get, CloudRoutes.RepSnapshot);
        notModified.Headers.TryAddWithoutValidation("If-None-Match", etag);
        Assert.Equal(HttpStatusCode.NotModified, (await phone.SendAsync(notModified)).StatusCode);

        // ---- بيع آجل: ينتظر في الخادم، وإعادة الإرسال لا تكرره، ثم يُرحَّل في المعمل ----
        var saleId = Guid.NewGuid();
        var sale = Req(saleId, "CreditSale", new SalePayload(new CustomerRef(customerId), new() { new SaleLinePayload(waterId, shrinkId, 3) }));
        Assert.Equal(RelayState.Waiting, (await Send(phone, sale)).State);
        Assert.Equal(RelayState.Waiting, (await Send(phone, sale)).State);
        var r1 = await Run();
        Assert.Equal((1, 1), (r1.Received, r1.Posted));
        Assert.Equal(1, r1.Snapshots);                                                      // رصيد السيارة تغيّر
        var saleStatus = await StatusOf(phone, saleId);
        Assert.Equal((RelayState.Done, "Posted"), (saleStatus.State, saleStatus.Status));
        await using (var db = NewDb())
        {
            Assert.Equal(1, await db.RepRequests.CountAsync(r => r.ClientId == saleId));
            var invoice = await db.SalesInvoices.AsNoTracking().SingleAsync(i => i.Id == saleStatus.ResultId);
            Assert.Equal((15_000m, vanId), (invoice.TotalAmount, invoice.WarehouseId));
        }
        var refreshed = (await phone.GetFromJsonAsync<RepSnapshot>(CloudRoutes.RepSnapshot, CloudSyncService.Json))!;
        Assert.Equal(140m, refreshed.VanStock.Single(s => s.ItemId == waterId).Pieces);
        Assert.Equal(15_000m, refreshed.Customers.Single().Balance);

        // ---- حركة متعذّرة تُصحَّح وتُعاد بالرقم نفسه ----
        var badId = Guid.NewGuid();
        await Send(phone, Req(badId, "Collection", new CollectionPayload(new CustomerRef(999_999), 5_000)));
        Assert.Equal(1, (await Run()).Failed);
        var bad = await StatusOf(phone, badId);
        Assert.Equal(("Failed", "الزبون غير موجود"), (bad.Status, bad.Message));
        Assert.Equal(RelayState.Waiting, (await Send(phone, Req(badId, "Collection", new CollectionPayload(new CustomerRef(customerId), 5_000)))).State);
        Assert.Equal(1, (await Run()).Posted);
        Assert.Equal("Posted", (await StatusOf(phone, badId)).Status);

        // ---- مرتجع: بانتظار الاعتماد، ثم يعود قرار المعمل للهاتف ----
        var returnId = Guid.NewGuid();
        await Send(phone, Req(returnId, "Return", new ReturnPayload(new CustomerRef(customerId), new() { new ReturnLinePayload(waterId, shrinkId, 1) }, "زائد")));
        Assert.Equal(1, (await Run()).Pending);
        Assert.Equal("Pending", (await StatusOf(phone, returnId)).Status);
        Assert.Equal(0, (await Run()).Updated);
        await using (var db = NewDb())
        {
            var requestId = await db.RepRequests.Where(r => r.ClientId == returnId).Select(r => r.Id).SingleAsync();
            Assert.True((await new RepAppService(db).ApproveAsync(requestId, AdminId)).Success);
        }
        Assert.Equal(1, (await Run()).Updated);
        Assert.Equal("Posted", (await StatusOf(phone, returnId)).Status);

        // ---- رقم حركة جهاز آخر مرفوض، وحدود الحجم ----
        string otherKey;
        await using (var db = NewDb()) otherKey = (await new RepAppService(db).RegisterDeviceAsync(repId, "هاتف ثان", AdminId)).deviceKey!;
        await Run();
        using var other = Phone(cloud, otherKey);
        Assert.Equal(HttpStatusCode.Conflict, (await other.PostAsJsonAsync(CloudRoutes.RepRequests, sale)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PostAsJsonAsync(CloudRoutes.RepRequests, Req(Guid.NewGuid(), "Hack", new { }))).StatusCode);

        // ---- خدمة الجهاز: تمر على مشاريع قاعدة التحكم وتزامن المفعّل منها ----
        var cashId = Guid.NewGuid();
        await Send(phone, Req(cashId, "CashSale", new SalePayload(new CustomerRef(customerId), new() { new SaleLinePayload(waterId, shrinkId, 1) })));
        var logs = new List<string>();
        var runner = new CloudSyncRunner(ControlCs, "SERVER-PC", (url, key) => CloudSyncService.CreateClient(url, key, cloud.Server.CreateHandler()));
        Assert.Equal(TimeSpan.FromSeconds(20), await runner.RunAllAsync(logs.Add));
        Assert.Equal("Posted", (await StatusOf(phone, cashId)).Status);
        Assert.Contains(logs, l => l.Contains("رُحّل 1"));

        // ---- إيقاف الجهاز في المعمل يصل الخادم في الدورة التالية ----
        await using (var db = NewDb())
        {
            var deviceId = await db.RepDevices.Where(d => d.DeviceKey == deviceKey).Select(d => d.Id).SingleAsync();
            Assert.True((await new RepAppService(db).SetDeviceActiveAsync(deviceId, false, AdminId)).Success);
        }
        await Run();
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync(CloudRoutes.RepPing)).StatusCode);

        // ---- الحالة في المعمل، وانقطاع الإنترنت يُسجَّل خطأً بلا ضياع ----
        await using (var db = NewDb())
        {
            var s = await new CloudSyncService(db).SettingsAsync();
            Assert.Equal(("SERVER-PC", 5), (s.AgentMachine, s.ReceivedCount));
            Assert.NotNull(s.LastSuccessAt);
            using var offline = CloudSyncService.CreateClient("http://127.0.0.1:1", agentKey);
            var down = await new CloudSyncService(db).RunOnceAsync(offline, "SERVER-PC");
            Assert.False(down.Ok);
            Assert.Contains("تعذّر الاتصال بالخادم السحابي", down.Error);
        }
        await using (var db = NewDb())
        {
            var s = await new CloudSyncService(db).SettingsAsync();
            Assert.True(s.LastErrorAt >= s.LastSuccessAt);
            Assert.Contains("تعذّر الاتصال", s.LastError);
        }
        _ = fgId;
    }
}
