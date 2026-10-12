using ERP.Data.ControlDb;
using ERP.Data.ProjectDb;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

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

    public CloudSyncRunner(string controlConnectionString, string machine, Func<string, string, HttpClient>? clientFactory = null)
    {
        _controlCs = controlConnectionString;
        _machine = machine;
        _clientFactory = clientFactory ?? ((url, key) => CloudSyncService.CreateClient(url, key));
    }

    /// <summary>
    /// مفتاح المعمل المولَّد في مشاريع قاعدة التحكم (أول مفتاح لكل مشروع فعّال له مفتاح).
    /// يستعمله ملف إعداد «تجربة المندوبين» ليضع المفتاح في الخادم الوسيط بنفسه فلا ينسخه المستخدم يدويًا.
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
        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var db = new ProjectDbContext(new DbContextOptionsBuilder<ProjectDbContext>()
                    .UseSqlServer(auth.BuildProjectConnectionString(project)).Options);
                var settings = await db.CloudSyncSettings.AsNoTracking().FirstOrDefaultAsync(ct);
                if (settings is not { IsEnabled: true } || string.IsNullOrEmpty(settings.ServerUrl) || string.IsNullOrEmpty(settings.AgentKey)) continue;
                if (!_clients.TryGetValue((settings.ServerUrl, settings.AgentKey), out var http))
                    _clients[(settings.ServerUrl, settings.AgentKey)] = http = _clientFactory(settings.ServerUrl, settings.AgentKey);
                if (!_states.TryGetValue(project.ProjectId, out var state)) _states[project.ProjectId] = state = new CloudSyncState();
                var report = await new CloudSyncService(db).RunOnceAsync(http, _machine, state, ct);
                if (report.Received > 0 || report.Updated > 0 || !report.Ok) log?.Invoke($"{project.ProjectName}: {report.Text}");
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
}
