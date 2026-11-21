using System.Text.Json;
using ERP.Data.Services;

namespace ERP.SyncAgent;

/// <summary>
/// إعداد الخدمة (يكتبه المثبّت، لا يُعدَّل يدويًا) في %ProgramData%\ERP-WaterFactory\sync-agent.json:
/// - SqlServer: سيرفر SQL على هذه الحاسبة؛ الخدمة تجد قواعد التحكم عليه بنفسها (كل مشروع يُنشأ لاحقًا يدخل تلقائيًا)؛
///   أو ControlDbConnectionString لقاعدة تحكم بعينها (الإعداد القديم).
/// - LocalRelay: الخادم المحلي المثبّت على الحاسبة نفسها (عنوانه الداخلي ومنفذه وملف المفتاح).
/// </summary>
public record AgentConfig(string? ControlDbConnectionString, string? SqlServer = null, LocalRelayOptions? LocalRelay = null)
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ERP-WaterFactory");
    public static string MachinePath => Path.Combine(Folder, "sync-agent.json");
    public static string LocalServerFolder => Path.Combine(Folder, "LocalServer");
    public const int LocalPort = 5080;

    public static LocalRelayOptions DefaultLocalRelay =>
        new($"http://127.0.0.1:{LocalPort}", LocalPort, Path.Combine(LocalServerFolder, "agent-key.txt"));

    private static string BesideExe => Path.Combine(AppContext.BaseDirectory, "sync-agent.json");
    /// <summary>إعداد البرنامج المكتبي لهذا المستخدم: للتشغيل في نافذة أثناء التجربة دون تثبيت الخدمة.</summary>
    private static string DesktopSettings => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ERP-WaterFactory", "appsettings.json");

    public bool IsUsable => !string.IsNullOrWhiteSpace(ControlDbConnectionString) || !string.IsNullOrWhiteSpace(SqlServer);

    public static AgentConfig? Load()
    {
        foreach (var path in new[] { MachinePath, BesideExe })
        {
            if (!File.Exists(path)) continue;
            var c = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path));
            if (c?.IsUsable == true) return c;
        }
        if (File.Exists(DesktopSettings))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(DesktopSettings));
            if (doc.RootElement.TryGetProperty("ControlDbConnectionString", out var cs) && !string.IsNullOrWhiteSpace(cs.GetString()))
                return new AgentConfig(cs.GetString()!);
        }
        return null;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MachinePath)!);
        File.WriteAllText(MachinePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>قواعد التحكم التي تخدمها هذه الدورة.</summary>
    public async Task<List<string>> ControlDatabasesAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(ControlDbConnectionString)) return new() { ControlDbConnectionString };
        return await CloudSyncRunner.DiscoverControlDatabasesAsync(SqlServer!, ct);
    }
}
