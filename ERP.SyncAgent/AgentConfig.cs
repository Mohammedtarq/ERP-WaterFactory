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

    public static AgentConfig? Load()
    {
        foreach (var path in new[] { MachinePath, BesideExe })
        {
            if (!File.Exists(path)) continue;
            var c = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path));
            if (!string.IsNullOrWhiteSpace(c?.ControlDbConnectionString)) return c;
        }
        return null;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MachinePath)!);
        File.WriteAllText(MachinePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
