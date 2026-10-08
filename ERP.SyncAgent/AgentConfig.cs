using System.Text.Json;

namespace ERP.SyncAgent;

/// <summary>
/// إعداد الخدمة: سلسلة اتصال قاعدة التحكم فقط (المشاريع وإعداد المزامنة لكل منها في قواعدها).
/// يُحفظ في %ProgramData%\ERP-WaterFactory\sync-agent.json (أو بجانب البرنامج للتجربة).
/// </summary>
public record AgentConfig(string ControlDbConnectionString)
{
    public static string MachinePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ERP-WaterFactory", "sync-agent.json");
    private static string BesideExe => Path.Combine(AppContext.BaseDirectory, "sync-agent.json");
    /// <summary>إعداد البرنامج المكتبي لهذا المستخدم: للتشغيل في نافذة أثناء التجربة دون تثبيت الخدمة.</summary>
    private static string DesktopSettings => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ERP-WaterFactory", "appsettings.json");

    public static AgentConfig? Load()
    {
        foreach (var path in new[] { MachinePath, BesideExe })
        {
            if (!File.Exists(path)) continue;
            var c = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path));
            if (!string.IsNullOrWhiteSpace(c?.ControlDbConnectionString)) return c;
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
}
