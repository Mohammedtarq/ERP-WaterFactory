using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace ERP.Data.Setup;

/// <summary>ما نشره السيرفر في <c>version.json</c>: رقم النسخة، وهل التحديث إجباري (تغيّرت قاعدة البيانات أو تعارض مع القديم).</summary>
public record UpdateOffer(Version Version, bool Mandatory);

/// <summary>
/// التحديث التلقائي للبرنامج من السيرفر المحلي (نفس الخادم المحلي لتطبيق المندوبين على المنفذ 5080):
/// يفحص البرنامج عند فتحه رقم النسخة المنشورة، فإن وُجدت أحدث عرض التحديث، وبعد الموافقة يُنزِّل الملفات المتغيرة فقط ويعيد التشغيل.
/// هذه الفئة منطق الفحص فقط؛ التنزيل والاستبدال في سكربت Apply-Update.ps1 (لأن البرنامج لا يستبدل ملفاته وهو يعمل).
/// </summary>
public static class UpdateService
{
    public const int Port = 5080;

    /// <summary>اسم جهاز السيرفر من اتصال قاعدة التحكم (SERVER-PC\SQLEXPRESS ← SERVER-PC). null إن كان تدريبًا على LocalDB.</summary>
    public static string? ServerHost(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        string source;
        try { source = new SqlConnectionStringBuilder(connectionString).DataSource; }
        catch (ArgumentException) { return null; }
        source = source.Trim();
        if (source.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) source = source[4..];
        source = source.Split('\\')[0].Split(',')[0].Trim();
        if (source.Length == 0 || source.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase)) return null;
        return source is "." or "(local)" or "localhost" ? "localhost" : source;
    }

    public static Uri BaseUri(string host) => new($"http://{host}:{Port}/update/");

    public static UpdateOffer? ParseOffer(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("version", out var v) || !Version.TryParse(v.GetString(), out var version)) return null;
            var mandatory = root.TryGetProperty("mandatory", out var m) && m.ValueKind == JsonValueKind.True;
            return new UpdateOffer(version, mandatory);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>يعيد العرض إن كان أحدث من النسخة الحالية، وإلا null (أو عند أي تعذّر اتصال: التحديث لا يمنع العمل أبدًا).</summary>
    public static async Task<UpdateOffer?> CheckAsync(HttpClient http, Uri baseUri, Version current, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            var json = await http.GetStringAsync(new Uri(baseUri, "version.json"), cts.Token);
            return ParseOffer(json) is { } offer && offer.Version > current ? offer : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException) { return null; }
    }
}
