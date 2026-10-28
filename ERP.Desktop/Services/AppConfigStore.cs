using System.IO;
using System.Text.Json;
using ERP.Presentation.Services;

namespace ERP.Desktop.Services;

/// <summary>
/// إعداد الاتصال يُحفظ في مجلد بيانات المستخدم (%AppData%\ERP-WaterFactory\appsettings.json)
/// لأن مجلد البرنامج قد يكون للقراءة فقط (Program Files). يُقرأ أيضًا appsettings.json بجانب البرنامج
/// كإعداد افتراضي (للتوزيع المسبق الضبط على الشبكة).
/// </summary>
public class AppConfigStore : IConfigStore
{
    private const string Key = "ControlDbConnectionString";

    public string ConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ERP-WaterFactory", "appsettings.json");

    private static string BesideExe => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    public string? LoadControlConnectionString() => Read(ConfigPath) ?? Read(BesideExe);

    public void SaveControlConnectionString(string connectionString)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new Dictionary<string, string> { [Key] = connectionString },
                                                                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var value = doc.RootElement.TryGetProperty(Key, out var p) ? p.GetString() : null;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
