using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ERP.Cloud.Contracts;
using ERP.Data.ControlDb;
using ERP.Data.ProjectDb;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>
/// الخادم المحلي على حاسبة السيرفر نفسها (يثبّته المثبّت): عنوانه من داخل الحاسبة، ومنفذه للهواتف، وملف المفتاح الذي يقرأه.
/// </summary>
public record LocalRelayOptions(string AgentUrl, int Port, string KeyFile);

/// <summary>
/// قلب خدمة المزامنة على جهاز السيرفر: يمر على مشاريع قاعدة التحكم الفعّالة، ويزامن كل مشروع فُعّلت فيه المزامنة السحابية
/// (من شاشة «المزامنة السحابية»)، ويعيد مدة الانتظار حتى الدورة التالية (أقصر فترة بين المشاريع المفعّلة).
/// </summary>
public class CloudSyncRunner
{
    private readonly string _controlCs;
    private readonly string _machine;
    private readonly Func<string, string, HttpClient> _clientFactory;
    private readonly Dictionary<int, CloudSyncState> _states = new();
    private readonly Dictionary<(string url, string key), HttpClient> _clients = new();

    public static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(30);

    private readonly LocalRelayOptions? _local;

    /// <summary>عنوان الحاسبة على شبكة المعمل (يُستبدل في الاختبارات).</summary>
    public Func<string?> LanAddress { get; set; } = DetectLanAddress;

    public CloudSyncRunner(string controlConnectionString, string machine, Func<string, string, HttpClient>? clientFactory = null, LocalRelayOptions? local = null)
    {
        _controlCs = controlConnectionString;
        _machine = machine;
        _clientFactory = clientFactory ?? ((url, key) => CloudSyncService.CreateClient(url, key));
        _local = local;
    }

    /// <summary>
    /// عنوان IPv4 الحاسبة على الشبكة التي لها بوابة (الراوتر)، مع تفضيل العناوين الداخلية؛ null بلا شبكة.
    /// </summary>
    public static string? DetectLanAddress()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(n => n.GetIPProperties())
                .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                .SelectMany(p => p.UnicastAddresses.Select(a => a.Address))
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString())
                .ToList();
            return candidates.FirstOrDefault(CloudUrl.IsPrivateLan) ?? candidates.FirstOrDefault();
        }
        catch (NetworkInformationException) { return null; }
    }

    /// <summary>
    /// قواعد التحكم على سيرفر SQL التي تصلها هذه الخدمة (فيها جدولا المشاريع والمستخدمين): لا حاجة لكتابة اسم القاعدة يدويًا.
    /// </summary>
    public static async Task<List<string>> DiscoverControlDatabasesAsync(string serverConnectionString, CancellationToken ct = default)
    {
        var found = new List<string>();
        var master = new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = "master" };
        await using var conn = new SqlConnection(master.ConnectionString);
        await conn.OpenAsync(ct);
        var names = new List<string>();
        await using (var cmd = new SqlCommand("SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 AND HAS_DBACCESS(name) = 1 ORDER BY name", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) names.Add(r.GetString(0));
        foreach (var name in names)
        {
            var q = name.Replace("]", "]]");
            await using var check = new SqlCommand(
                $"SELECT CASE WHEN OBJECT_ID(N'[{q}].dbo.Projects', 'U') IS NOT NULL AND OBJECT_ID(N'[{q}].dbo.GlobalUsers', 'U') IS NOT NULL THEN 1 ELSE 0 END", conn);
            try
            {
                if (Convert.ToInt32(await check.ExecuteScalarAsync(ct)) == 1)
                    found.Add(new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = name }.ConnectionString);
            }
            catch (SqlException) { /* قاعدة لا تُقرأ: تُتخطى */ }
        }
        return found;
    }

    /// <summary>
    /// مفتاح المعمل المولَّد في مشاريع قاعدة التحكم (أول مفتاح لكل مشروع فعّال له مفتاح).
    /// للتشخيص (ERP.SyncAgent.exe --print-key)؛ الخادم المحلي يأخذه تلقائيًا من خدمة المزامنة.
    /// </summary>
    public static async Task<List<(string Project, string Key)>> ReadAgentKeysAsync(string controlConnectionString, CancellationToken ct = default)
    {
        var keys = new List<(string, string)>();
        await using var control = new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>().UseSqlServer(controlConnectionString).Options);
        var projects = await control.Projects.AsNoTracking().Where(p => p.IsActive)
            .Select(p => new ProjectOption(p.Id, p.ProjectName, p.DatabaseName, p.ServerAddress, 0)).ToListAsync(ct);
        var auth = new AuthService(controlConnectionString);
        foreach (var project in projects)
        {
            try
            {
                await using var db = new ProjectDbContext(new DbContextOptionsBuilder<ProjectDbContext>()
                    .UseSqlServer(auth.BuildProjectConnectionString(project)).Options);
                var key = await db.CloudSyncSettings.AsNoTracking().Select(x => x.AgentKey).FirstOrDefaultAsync(ct);
                if (!string.IsNullOrEmpty(key)) keys.Add((project.ProjectName, key));
            }
            catch (SqlException) { /* مشروع لم يُرقَّ بعد أو غير متاح: يُتخطى */ }
        }
        return keys;
    }

    public async Task<TimeSpan> RunAllAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        List<ProjectOption> projects;
        try
        {
            await using var control = new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>().UseSqlServer(_controlCs).Options);
            projects = await control.Projects.AsNoTracking().Where(p => p.IsActive)
                .Select(p => new ProjectOption(p.Id, p.ProjectName, p.DatabaseName, p.ServerAddress, 0)).ToListAsync(ct);
        }
        catch (SqlException ex)
        {
            log?.Invoke($"تعذّر الاتصال بقاعدة التحكم: {ex.Message}");
            return IdleDelay;
        }

        var auth = new AuthService(_controlCs);
        var delay = IdleDelay;
        var localTaken = false;
        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var db = new ProjectDbContext(new DbContextOptionsBuilder<ProjectDbContext>()
                    .UseSqlServer(auth.BuildProjectConnectionString(project)).Options);
                var settings = await db.CloudSyncSettings.AsNoTracking().FirstOrDefaultAsync(ct);
                if (settings is not { IsEnabled: true } || string.IsNullOrEmpty(settings.AgentKey)) continue;

                var targets = new List<SyncTarget>();
                var local = settings.LocalEnabled && !localTaken ? LocalTarget(settings) : null;
                if (settings.LocalEnabled && localTaken && _local is not null)
                    log?.Invoke($"{project.ProjectName}: الخادم المحلي مخصص لمشروع آخر على هذه الحاسبة — يُكتفى بالسحابي");
                if (local is not null) { targets.Add(new SyncTarget(SyncTargetKind.Local, Client(local, settings.AgentKey))); localTaken = _local is not null; }
                if (!string.IsNullOrEmpty(settings.ServerUrl)) targets.Add(new SyncTarget(SyncTargetKind.Cloud, Client(settings.ServerUrl, settings.AgentKey)));
                if (targets.Count == 0) continue;

                // الخادم المحلي يأخذ مفتاح المعمل من هنا (لا نسخ يدوي)
                if (local is not null && _local is not null) WriteKeyFile(settings.AgentKey, log);

                if (!_states.TryGetValue(project.ProjectId, out var state)) _states[project.ProjectId] = state = new CloudSyncState();
                var report = await new CloudSyncService(db).RunOnceAsync(targets, _machine, state, ct);
                if (report.Received > 0 || report.Updated > 0 || !report.Ok) log?.Invoke($"{project.ProjectName}: {report.Text}");

                // عنوان الخادم المحلي للهواتف يتبع عنوان الحاسبة الحالي (ويصلها مع نسخة العمل)
                if (local is not null && _local is not null && report.Targets?.GetValueOrDefault(SyncTargetKind.Local) is null
                    && LanAddress() is string ip)
                {
                    var url = $"http://{ip}:{_local.Port}";
                    if (!string.Equals(settings.LocalUrl, url, StringComparison.OrdinalIgnoreCase))
                    {
                        var row = await db.CloudSyncSettings.FirstAsync(ct);
                        row.LocalUrl = url;
                        await db.SaveChangesAsync(ct);
                        state.LastSnapshotCheckUtc = null;   // العنوان الجديد يصل الهواتف في الدورة التالية
                        log?.Invoke($"{project.ProjectName}: عنوان الخادم المحلي الآن {url}");
                    }
                }
                var interval = TimeSpan.FromSeconds(settings.IntervalSeconds);
                if (interval < delay) delay = interval;
            }
            catch (SqlException ex)
            {
                // مشروع لم يُرقَّ بعد (بلا جدول المزامنة) أو قاعدته غير متاحة: لا يوقف بقية المشاريع
                log?.Invoke($"{project.ProjectName}: تعذّر فتح قاعدة المشروع — {ex.Message}");
            }
        }
        return delay;
    }

    /// <summary>عنوان الخادم المحلي لهذه الخدمة: من داخل الحاسبة إن كان عليها، وإلا العنوان المسجّل (مزامنة من جهاز آخر).</summary>
    private string? LocalTarget(ERP.Data.ProjectDb.Entities.CloudSyncSetting settings) =>
        _local?.AgentUrl ?? settings.LocalUrl;

    private HttpClient Client(string url, string key)
    {
        if (!_clients.TryGetValue((url, key), out var http)) _clients[(url, key)] = http = _clientFactory(url, key);
        return http;
    }

    private string? _writtenKey;

    private void WriteKeyFile(string key, Action<string>? log)
    {
        if (_local is null || _writtenKey == key) return;
        try
        {
            if (!File.Exists(_local.KeyFile) || File.ReadAllText(_local.KeyFile).Trim() != key)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_local.KeyFile)!);
                File.WriteAllText(_local.KeyFile, key);
                log?.Invoke("وُضع مفتاح المعمل في الخادم المحلي");
            }
            _writtenKey = key;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"تعذّر وضع مفتاح المعمل في الخادم المحلي: {ex.Message}");
        }
    }
}
