using System.Net;
using System.Net.Http.Json;
using ERP.Cloud.Contracts;
using ERP.Data.Services;
using ERP.Data.Setup;
using ERP.RepApp.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Cloud.Tests;

/// <summary>
/// النظامان معًا: الخادم المحلي (SQLite، مفتاحه من ملف تكتبه خدمة المزامنة) والخادم السحابي (SQL Server)،
/// وهاتف ينتقل بين واي فاي المعمل والإنترنت: يجد نتيجته أينما اتصل، ويتحدّث عنوان المعمل عنده وحده، ويكتشف الخادم المحلي إن تغيّر عنوانه.
/// </summary>
[Collection("cloud")]
public class DualSyncTests : CloudTestBase, IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dual-sync-" + Guid.NewGuid().ToString("N"));
    private const string CloudAddress = "https://cloud.alrahma.test";

    public DualSyncTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>خادم في الذاكرة بإعداد من متغيرات البيئة (كما في الخدمة والاستضافة)؛ يُبنى فورًا ثم تُمسح المتغيرات.</summary>
    private static WebApplicationFactory<Program> Relay(Dictionary<string, string?> env)
    {
        var all = new[] { "ConnectionStrings__Relay", "Relay__AgentKey", "Relay__SqlitePath", "Relay__AgentKeyFile", "Relay__DiscoveryPort", "Relay__HttpPort" };
        foreach (var k in all) Environment.SetEnvironmentVariable(k, env.GetValueOrDefault(k));
        var factory = new WebApplicationFactory<Program>();
        _ = factory.Server;
        foreach (var k in all) Environment.SetEnvironmentVariable(k, null);
        return factory;
    }

    /// <summary>توجيه طلبات الهاتف حسب العنوان: المحلي متاح على واي فاي المعمل فقط.</summary>
    private sealed class Network(HttpMessageHandler local, HttpMessageHandler cloud) : HttpMessageHandler
    {
        public bool AtFactory { get; set; } = true;
        public bool CloudUp { get; set; } = true;
        public string LocalHost { get; set; } = "192.168.50.7";
        private readonly HttpMessageInvoker _local = new(local), _cloud = new(cloud);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var host = request.RequestUri!.Host;
            if (host == "cloud.alrahma.test")
                return CloudUp ? _cloud.SendAsync(request, ct) : throw new HttpRequestException("لا إنترنت");
            if (host == LocalHost && AtFactory) return _local.SendAsync(request, ct);
            throw new HttpRequestException("لا يصل");
        }
    }

    [Fact]
    public void Link_code_v2_carries_both_servers_and_stays_compatible()
    {
        var key = new string('B', 64);
        var fid = FactoryIdentity.Of(new string('C', 64));
        var (code, error) = LinkCode.Parse(new LinkCode(CloudAddress, key, "http://192.168.1.20:5080/", fid).Encode());
        Assert.Null(error);
        Assert.Equal(new LinkCode(CloudAddress, key, "http://192.168.1.20:5080", fid), code);
        Assert.Equal(new[] { "http://192.168.1.20:5080", CloudAddress }, code!.Candidates);   // المحلي أولًا
        Assert.Contains("\"v\":1", new LinkCode(CloudAddress, key).Encode());                  // عنوان واحد: الصيغة القديمة
        Assert.Contains("مشفّرًا", LinkCode.Parse(new LinkCode(CloudAddress, key, "http://example.com").Encode()).error);
        Assert.Equal(16, fid.Length);
        Assert.NotEqual(fid, FactoryIdentity.Of(new string('D', 64)));
    }

    [Fact]
    public async Task Both_relays_together_phone_moves_between_factory_wifi_and_internet()
    {
        var seed = await SeedRepAsync("مزدوج");

        // ---- الإعداد من الشاشة: الخادم المحلي + السحابي، والمفتاح يُولَّد تلقائيًا ----
        await using (var db = NewDb())
            Assert.True((await new CloudSyncService(db).SaveSettingsAsync(CloudAddress, true, 20, AdminId, localEnabled: true)).Success);
        string agentKey;
        await using (var db = NewDb()) agentKey = (await new CloudSyncService(db).SettingsAsync()).AgentKey!;
        string deviceKey;
        await using (var db = NewDb()) deviceKey = (await new RepAppService(db).RegisterDeviceAsync(seed.RepId, "هاتف مزدوج", AdminId)).deviceKey!;

        var keyFile = Path.Combine(_dir, "LocalServer", "agent-key.txt");
        await using var local = Relay(new()
        {
            ["Relay__SqlitePath"] = Path.Combine(_dir, "LocalServer", "relay.db"),
            ["Relay__AgentKeyFile"] = keyFile,
        });
        await using var cloud = Relay(new() { ["ConnectionStrings__Relay"] = RelayCs, ["Relay__AgentKey"] = agentKey });

        // قبل أن تضع خدمة المزامنة المفتاح: الخادم المحلي لا يقبل أحدًا
        using (var early = CloudSyncService.CreateClient("http://127.0.0.1:5080", agentKey, local.Server.CreateHandler()))
            Assert.Contains("لم يصل", (await CloudSyncService.TestAsync(early)).message);

        var lan = "192.168.50.7";
        var runner = new CloudSyncRunner(ControlCs, "SERVER-PC",
            (url, key) => CloudSyncService.CreateClient(url, key, url.Contains("127.0.0.1") ? local.Server.CreateHandler() : cloud.Server.CreateHandler()),
            new LocalRelayOptions("http://127.0.0.1:5080", 5080, keyFile)) { LanAddress = () => lan };
        var log = new List<string>();
        async Task Factory()
        {
            await runner.RunAllAsync(log.Add);
            await using var db = NewDb();
            var s = await new CloudSyncService(db).SettingsAsync();
            Assert.True(s.LocalLastErrorAt is null || s.LocalLastSuccessAt > s.LocalLastErrorAt, s.LocalLastError + string.Join("\n", log));
            Assert.True(s.LastErrorAt is null || s.LastSuccessAt > s.LastErrorAt, s.LastError + string.Join("\n", log));
        }

        // ---- أول دورة: المفتاح في ملف الخادم المحلي، وعنوان الحاسبة مكتوب للهواتف، والخادمان متصلان ----
        await Factory();
        Assert.Equal(agentKey, File.ReadAllText(keyFile).Trim());
        await using (var db = NewDb())
        {
            var s = await new CloudSyncService(db).SettingsAsync();
            Assert.Equal("http://192.168.50.7:5080", s.LocalUrl);
            Assert.NotNull(s.LocalLastSuccessAt);
            Assert.NotNull(s.LastSuccessAt);
            Assert.Equal(CloudSyncLevel.Ok, CloudSyncStatus.Describe(s, DateTime.UtcNow).level);
            Assert.StartsWith("المحلي: متصل", CloudSyncStatus.Describe(s, DateTime.UtcNow).text);
        }

        // ---- الهاتف: رمز الربط بالخادمين وبصمة المعمل ----
        await Factory();                                                            // نسخة العمل بعنوان الحاسبة
        var store = new LocalStore(Path.Combine(_dir, "rep.db"));
        store.SaveLink(new LinkCode(CloudAddress, deviceKey, "http://192.168.50.7:5080", FactoryIdentity.Of(agentKey)));
        var net = new Network(local.Server.CreateHandler(), cloud.Server.CreateHandler());
        var client = new RepCloudClient(() => store.Link!, store.UpdateLocal, net);
        var sync = new SyncEngine(store, () => client);
        var work = new RepWork(store);

        var first = await sync.RunAsync();
        Assert.True(first.Online, first.Error);
        Assert.True(first.ViaLocal);
        Assert.Equal("عبر واي فاي المعمل", first.Channel);
        Assert.Equal(200m, work.VanStock()[seed.WaterId]);

        // 1) بيع على واي فاي المعمل ← الخادم المحلي؛ النتيجة تُنسخ للسحابي
        var atFactory = work.SaveSale("CashSale", seed.CustomerId, new[] { new SaleLine(seed.WaterId, seed.ShrinkId, 1) });
        Assert.Equal(1, (await sync.RunAsync()).Sent);
        await Factory();
        net.AtFactory = false;                                                      // خرج المندوب
        var outside = await sync.RunAsync();
        Assert.True(outside.Online);
        Assert.False(outside.ViaLocal);
        Assert.Equal("Posted", store.Find(atFactory.ClientId)!.Status);             // عرف النتيجة من السحابي

        // 2) بيع في الطريق ← السحابي؛ يعود للمعمل فيعرف النتيجة من المحلي
        var onRoad = work.SaveSale("CashSale", seed.CustomerId, new[] { new SaleLine(seed.WaterId, seed.ShrinkId, 2) });
        Assert.Equal(1, (await sync.RunAsync()).Sent);
        await Factory();
        net.AtFactory = true;
        net.CloudUp = false;                                                        // انقطع الإنترنت في المعمل
        var back = await sync.RunAsync();
        Assert.True(back.Online, back.Error);
        Assert.True(back.ViaLocal);
        Assert.Equal("Posted", store.Find(onRoad.ClientId)!.Status);
        await using (var db = NewDb())
            Assert.Equal(2, await db.RepRequests.CountAsync(r => r.RepEmployeeId == seed.RepId));   // لا تكرار

        // 3) السحابي منقطع عن المعمل: الخادم المحلي يعمل وحده، والنتائج تُنسخ للسحابي حين يعود
        var cut = work.SaveSale("CashSale", seed.CustomerId, new[] { new SaleLine(seed.WaterId, seed.ShrinkId, 1) });
        await sync.RunAsync();
        var downRunner = new CloudSyncRunner(ControlCs, "SERVER-PC",
            (url, key) => url.Contains("127.0.0.1") ? CloudSyncService.CreateClient(url, key, local.Server.CreateHandler())
                                                    : CloudSyncService.CreateClient(url, key, new Network(local.Server.CreateHandler(), cloud.Server.CreateHandler()) { CloudUp = false }),
            new LocalRelayOptions("http://127.0.0.1:5080", 5080, keyFile)) { LanAddress = () => lan };
        await downRunner.RunAllAsync(log.Add);
        await using (var db = NewDb())
        {
            var s = await new CloudSyncService(db).SettingsAsync();
            Assert.True(s.LastErrorAt > (s.LastSuccessAt ?? DateTime.MinValue));       // السحابي متعذّر
            Assert.True(s.LocalLastSuccessAt > (s.LocalLastErrorAt ?? DateTime.MinValue)); // والمحلي يعمل
            Assert.Equal(CloudSyncLevel.Error, CloudSyncStatus.DescribeCloud(s, DateTime.UtcNow).level);
        }
        await sync.RunAsync();
        Assert.Equal("Posted", store.Find(cut.ClientId)!.Status);
        net.CloudUp = true;
        await Factory();                                                            // الدورة الطبيعية تعيد الاتصال
        using (var cloudPhone = new HttpClient(cloud.Server.CreateHandler()) { BaseAddress = cloud.Server.BaseAddress })
        {
            cloudPhone.DefaultRequestHeaders.Add(CloudRoutes.DeviceKeyHeader, deviceKey);
            // حركة السحابي نفسها معروفة فيه (وصلته عبر المحلي ونُسخت نتيجتها)
            var statuses = await (await cloudPhone.PostAsJsonAsync(CloudRoutes.RepStatuses, new PhoneStatusQuery(new() { atFactory.ClientId, onRoad.ClientId })))
                .Content.ReadFromJsonAsync<List<PhoneRequestStatus>>();
            Assert.Equal(2, statuses!.Count(s => s.Status == "Posted"));
        }

        // ---- تغيّر عنوان الحاسبة: يُكتب وحده ويصل الهاتف مع نسخة العمل (عبر السحابي) ----
        lan = "192.168.50.9";
        await Factory();
        await using (var db = NewDb()) Assert.Equal("http://192.168.50.9:5080", (await new CloudSyncService(db).SettingsAsync()).LocalUrl);
        await Factory();                                                            // نسخة العمل بالعنوان الجديد
        net.AtFactory = false;
        net.LocalHost = "192.168.50.9";
        Assert.True((await sync.RunAsync()).SnapshotUpdated);
        Assert.Equal("http://192.168.50.9:5080", store.Link!.LocalUrl);
        Assert.Equal(CloudAddress, store.Link.ServerUrl);
        net.AtFactory = true;
        net.CloudUp = false;
        Assert.True((await sync.RunAsync()).ViaLocal);                              // يصل المحلي بعنوانه الجديد

        // ---- عنوان تغيّر ولم يصل الهاتف: يجده على الشبكة ببصمة المعمل ----
        net.LocalHost = "192.168.50.11";
        client.Discover = (fid, _) => Task.FromResult<string?>(fid == FactoryIdentity.Of(agentKey) ? "http://192.168.50.11:5080" : null);
        var found = await sync.RunAsync();
        Assert.True(found.Online, found.Error);
        Assert.Equal("http://192.168.50.11:5080", store.Link!.LocalUrl);

        // ---- عنوانان يصلان الخادم نفسه (نفق): لا تكرار ولا خطأ ----
        await using (var db = NewDb())
        {
            using var a = CloudSyncService.CreateClient("http://127.0.0.1:5080", agentKey, local.Server.CreateHandler());
            using var b = CloudSyncService.CreateClient("https://tunnel.test", agentKey, local.Server.CreateHandler());
            var report = await new CloudSyncService(db).RunOnceAsync(new[] { new SyncTarget(SyncTargetKind.Local, a), new SyncTarget(SyncTargetKind.Cloud, b) }, "SERVER-PC");
            Assert.True(report.Ok, report.Error);
        }
    }

    [Fact]
    public async Task Local_relay_answers_discovery_only_with_its_factory_identity()
    {
        var agentKey = new string('E', 64);
        var keyFile = Path.Combine(_dir, "key.txt");
        File.WriteAllText(keyFile, agentKey);
        var port = FreeUdpPort();
        await using var local = Relay(new()
        {
            ["Relay__SqlitePath"] = Path.Combine(_dir, "relay.db"),
            ["Relay__AgentKeyFile"] = keyFile,
            ["Relay__DiscoveryPort"] = port.ToString(),
            ["Relay__HttpPort"] = "5080",
        });
        var target = new IPEndPoint(IPAddress.Loopback, port);
        string? url = null;
        for (var i = 0; i < 20 && url is null; i++)   // المستمع يبدأ مع الخادم
            url = await LocalDiscovery.FindAsync(FactoryIdentity.Of(agentKey), TimeSpan.FromMilliseconds(300), target);
        Assert.Equal("http://127.0.0.1:5080", url);
        Assert.Null(await LocalDiscovery.FindAsync(FactoryIdentity.Of(new string('F', 64)), TimeSpan.FromMilliseconds(500), target));
    }

    private static int FreeUdpPort()
    {
        using var u = new System.Net.Sockets.UdpClient(0);
        return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
    }

    [Fact]
    public async Task Cloud_deploy_files_are_ready_without_editing()
    {
        Assert.Contains("فعّل المزامنة", CloudDeployBundle.Validate(CloudAddress, null));
        Assert.Contains("نطاقًا", CloudDeployBundle.Validate("https://203.0.113.5", new string('A', 64)));
        Assert.Contains("بلا منفذ", CloudDeployBundle.Validate("https://rep.alrahma.test:8443", new string('A', 64)));
        var app = Path.Combine(_dir, "CloudServer");
        Directory.CreateDirectory(Path.Combine(app, "runtimes"));
        File.WriteAllText(Path.Combine(app, "ERP.Cloud.Api.dll"), "x");
        File.WriteAllText(Path.Combine(app, "runtimes", "native.so"), "y");
        var folder = Path.Combine(_dir, "deploy");
        var key = new string('9', 64);
        CloudDeployBundle.Write(folder, "https://rep.alrahma.test/", key, app);
        var env = File.ReadAllText(Path.Combine(folder, ".env"));
        Assert.Contains("DOMAIN=rep.alrahma.test\n", env);
        Assert.Contains($"RELAY_AGENT_KEY={key}\n", env);
        Assert.DoesNotContain("\r", File.ReadAllText(Path.Combine(folder, "docker-compose.yml")));
        Assert.Contains("Relay__SqlitePath: /data/relay.db", File.ReadAllText(Path.Combine(folder, "docker-compose.yml")));
        Assert.Contains("{$DOMAIN}", File.ReadAllText(Path.Combine(folder, "Caddyfile")));
        Assert.True(File.Exists(Path.Combine(folder, "app", "runtimes", "native.so")));
        Assert.Contains("https://rep.alrahma.test/api/health", File.ReadAllText(Path.Combine(folder, "اقرأني - نشر الخادم السحابي.md")));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Refused_connection_is_explained_in_arabic_with_the_fix()
    {
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1/"), Timeout = TimeSpan.FromSeconds(3) };
        var (ok, message) = await CloudSyncService.TestAsync(http);
        Assert.False(ok);
        Assert.Contains("تثبيت - السيرفر", message);
        Assert.DoesNotContain("actively refused", message);
    }

    [Fact]
    public async Task Server_pc_enables_local_sync_by_itself_once_and_respects_the_user_afterwards()
    {
        // إعداد كما تركه المستخدم بالخطأ: عنوان الخادم المحلي في خانة السحابي، والمحلي غير مفعّل
        await using (var db = NewDb())
        {
            var s = await db.CloudSyncSettings.SingleAsync();
            (s.ServerUrl, s.IsEnabled, s.LocalEnabled, s.AutoSetupDone, s.AgentKey) = ("http://192.168.0.191:5080", false, false, false, null);
            await db.SaveChangesAsync();
        }
        var keyFile = Path.Combine(_dir, "LocalServer", "agent-key.txt");
        var runner = new CloudSyncRunner(ControlCs, "SERVER-PC", (url, key) => new HttpClient(new HttpClientHandler()) { BaseAddress = new Uri("http://127.0.0.1:1/"), Timeout = TimeSpan.FromMilliseconds(300) },
            new LocalRelayOptions("http://127.0.0.1:5080", 5080, keyFile)) { LanAddress = () => "192.168.0.191" };
        var log = new List<string>();
        await runner.RunAllAsync(log.Add);
        Assert.Contains(log, l => l.Contains("فُعّلت المزامنة المحلية تلقائيًا"));
        await using (var db = NewDb())
        {
            var s = await new CloudSyncService(db).SettingsAsync();
            Assert.Equal((true, true, true, null), (s.IsEnabled, s.LocalEnabled, s.AutoSetupDone, s.ServerUrl));
            Assert.Equal(64, s.AgentKey!.Length);                                 // المفتاح وُلّد وحده
            Assert.Equal(s.AgentKey, File.ReadAllText(keyFile).Trim());            // ووصل الخادم المحلي وحده
            Assert.Equal("http://192.168.0.191:5080", s.LocalUrl);                 // العنوان الذي كُتب بالخطأ صار للمحلي
        }

        // المستخدم يوقف المزامنة: لا تُفعَّل من جديد
        await using (var db = NewDb()) Assert.True((await new CloudSyncService(db).SaveSettingsAsync(null, false, 20, AdminId)).Success);
        await runner.RunAllAsync(log.Add);
        await using (var db = NewDb()) Assert.False((await new CloudSyncService(db).SettingsAsync()).IsEnabled);
    }

    [Fact]
    public async Task Upgrade_moves_wifi_trial_address_to_local_server_and_service_finds_control_db()
    {
        await using (var db = NewDb())
        {
            var s = await db.CloudSyncSettings.SingleAsync();
            (s.ServerUrl, s.IsEnabled, s.LastSuccessAt, s.LocalEnabled) = ("http://192.168.0.191:5080", true, DateTime.UtcNow, false);
            await db.SaveChangesAsync();
            await using var conn = new SqlConnection(db.Database.GetConnectionString());
            await conn.OpenAsync();
            await DatabaseInstaller.ExecuteScriptAsync(conn, DatabaseInstaller.ReadScript("47_sync_dual.sql"));
        }
        await using (var db = NewDb())
        {
            var s = await db.CloudSyncSettings.AsNoTracking().SingleAsync();
            Assert.Equal((null, "http://192.168.0.191:5080", true), (s.ServerUrl, s.LocalUrl, s.LocalEnabled));
            Assert.NotNull(s.LocalLastSuccessAt);
            Assert.Null(s.LastSuccessAt);
        }

        // الخدمة تجد قاعدة التحكم على السيرفر بنفسها (لا اسم يُكتب يدويًا)
        var controls = await CloudSyncRunner.DiscoverControlDatabasesAsync(Master);
        Assert.Contains(controls, cs => new SqlConnectionStringBuilder(cs).InitialCatalog == new SqlConnectionStringBuilder(ControlCs).InitialCatalog);
        Assert.DoesNotContain(controls, cs => new SqlConnectionStringBuilder(cs).InitialCatalog.StartsWith("ERP_Cloud_"));
    }
}
