using System.Text.Json;

namespace ERP.Presentation.Services;

/// <summary>
/// تفضيلات العرض لكل جهاز (مثل إظهار/إخفاء "القوائم الفرعية" في رئيسية كل وحدة) —
/// ملف JSON في %AppData%\ERP-WaterFactory\ui-settings.json (المتغير ERP_UI_SETTINGS يغيّر المسار).
/// أي خطأ قراءة/كتابة يُتجاهل بصمت: التفضيل يعود لقيمته الافتراضية.
/// </summary>
public static class UiPreferences
{
    private static readonly object Gate = new();
    private static Dictionary<string, bool>? _cache;

    public static string FilePath => Environment.GetEnvironmentVariable("ERP_UI_SETTINGS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ERP-WaterFactory", "ui-settings.json");

    private static Dictionary<string, bool> Load()
    {
        if (_cache is not null) return _cache;
        try
        {
            _cache = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _cache = new();
        }
        return _cache;
    }

    public static bool Get(string key, bool defaultValue)
    {
        lock (Gate) return Load().TryGetValue(key, out var v) ? v : defaultValue;
    }

    public static void Set(string key, bool value)
    {
        lock (Gate)
        {
            Load()[key] = value;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_cache));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>للاختبارات: يُعاد قراءة الملف عند الطلب التالي.</summary>
    public static void Reset()
    {
        lock (Gate) _cache = null;
    }
}
