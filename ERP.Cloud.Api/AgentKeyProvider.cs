using ERP.Cloud.Contracts;

namespace ERP.Cloud.Api;

/// <summary>
/// مفتاح المعمل الذي تعرّف به خدمة المزامنة نفسها. مصدران:
/// Relay:AgentKey (متغير بيئة، للخادم السحابي من ملفات النشر الجاهزة)، أو Relay:AgentKeyFile
/// (للخادم المحلي: تكتبه خدمة المزامنة على الحاسبة نفسها من قاعدة المعمل، فلا يُنسخ يدويًا ويُقرأ من جديد إن تغيّر).
/// </summary>
public class AgentKeyProvider(IConfiguration config)
{
    private readonly object _lock = new();
    private string? _cached;
    private DateTime _stamp;
    private DateTime _checked;

    public string Current
    {
        get
        {
            var direct = config["Relay:AgentKey"];
            if (!string.IsNullOrWhiteSpace(direct)) return direct.Trim();
            var file = config["Relay:AgentKeyFile"];
            if (string.IsNullOrWhiteSpace(file)) return "";
            lock (_lock)
            {
                // فحص الملف مرة كل ثانيتين على الأكثر (وفي كل طلب ما دام المفتاح لم يصل: يعمل فور كتابته)
                if (_cached is { Length: > 0 } && DateTime.UtcNow - _checked < TimeSpan.FromSeconds(2)) return _cached;
                _checked = DateTime.UtcNow;
                try
                {
                    var stamp = File.GetLastWriteTimeUtc(file);
                    if (string.IsNullOrEmpty(_cached) || stamp != _stamp)
                    {
                        _cached = File.Exists(file)
                            ? File.ReadAllLines(file).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? ""
                            : "";
                        _stamp = stamp;
                    }
                }
                catch (IOException) { _cached ??= ""; }
                catch (UnauthorizedAccessException) { _cached ??= ""; }
                return _cached;
            }
        }
    }

    /// <summary>بصمة المعمل (للاكتشاف على الشبكة) أو null قبل وصول المفتاح.</summary>
    public string? FactoryId => Current is { Length: >= 32 } k ? FactoryIdentity.Of(k) : null;
}
