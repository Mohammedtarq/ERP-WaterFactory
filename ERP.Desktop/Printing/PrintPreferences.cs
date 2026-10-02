using System.IO;
using System.Text.Json;

namespace ERP.Desktop.Printing;

public enum PrinterKind { A4, Receipt80 }

/// <summary>تفضيلات نوع مستند واحد: آخر نوع طابعة، الأعمدة المخفية (بالاسم)، وإخفاء الصفوف الصفرية.</summary>
public class ReportPreference
{
    public PrinterKind Printer { get; set; } = PrinterKind.A4;
    public List<string> HiddenColumns { get; set; } = new();
    public bool HideZeroRows { get; set; }
}

/// <summary>
/// تُحفظ على هذا الجهاز (%AppData%\ERP-WaterFactory\print-settings.json): فواتير المبيعات تبقى على الكاشير
/// إن اختير، والتقارير على A4، والأعمدة المخفية لكل تقرير — حتى لا يُسأل المستخدم كل مرة.
/// </summary>
public static class PrintPreferences
{
    private static readonly object Gate = new();
    private static Dictionary<string, ReportPreference>? _cache;

    public static string FilePath =>
        Environment.GetEnvironmentVariable("ERP_PRINT_SETTINGS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ERP-WaterFactory", "print-settings.json");

    private static Dictionary<string, ReportPreference> All
    {
        get
        {
            if (_cache is not null) return _cache;
            try
            {
                _cache = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, ReportPreference>>(File.ReadAllText(FilePath)) ?? new()
                    : new();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { _cache = new(); }
            return _cache;
        }
    }

    public static ReportPreference For(string key)
    {
        lock (Gate)
            return All.TryGetValue(key, out var p)
                ? new ReportPreference { Printer = p.Printer, HiddenColumns = p.HiddenColumns.ToList(), HideZeroRows = p.HideZeroRows }
                : new ReportPreference();
    }

    public static void Save(string key, ReportPreference pref)
    {
        lock (Gate)
        {
            All[key] = pref;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(All, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* التفضيل يبقى للجلسة الحالية */ }
        }
    }

    /// <summary>للاختبارات: إعادة القراءة من الملف.</summary>
    public static void Reload() { lock (Gate) _cache = null; }
}
