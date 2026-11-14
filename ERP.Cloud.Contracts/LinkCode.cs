using System.Net;
using System.Text.Json;

namespace ERP.Cloud.Contracts;

/// <summary>
/// قاعدة عنوان الخادم: https دائمًا، و http فقط لجهاز محلي أو عنوان داخل شبكة المعمل
/// (192.168.x.x، 10.x.x.x، 172.16–31.x.x) للتجربة عبر الواي فاي قبل الخادم السحابي.
/// </summary>
public static class CloudUrl
{
    public static string? Validate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "اكتب عنوان الخادم السحابي (مثل https://api.alrahma-water.com)";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
            return "عنوان الخادم غير صحيح";
        if (u.Scheme == Uri.UriSchemeHttp && !u.IsLoopback && !IsPrivateLan(u.Host))
            return "الاتصال يجب أن يكون مشفّرًا (https://) — إلا داخل شبكة المعمل (مثل http://192.168.1.10:5080)";
        return null;
    }

    public static bool IsPrivateLan(string host)
    {
        if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }
}

/// <summary>
/// رمز ربط الهاتف (يُعرض QR في «أجهزة التطبيق» ويصوّره المندوب): عنوان الخادم ومفتاح الجهاز،
/// ومع النظامين معًا: عنوان الخادم المحلي (واي فاي المعمل) وبصمة المعمل التي يتعرّف بها الهاتف على خادمه المحلي إن تغيّر عنوانه.
/// ServerUrl هو الخادم السحابي، أو الخادم الوحيد إن لم يكن غيره. الإصدار 1 (عنوان واحد) يبقى مقروءًا.
/// </summary>
public record LinkCode(string ServerUrl, string DeviceKey, string? LocalUrl = null, string? FactoryId = null)
{
    private record Wire(int V, string U, string K, string? L = null, string? F = null);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public string Encode() => LocalUrl is null && FactoryId is null
        ? JsonSerializer.Serialize(new Wire(1, ServerUrl, DeviceKey), Json)
        : JsonSerializer.Serialize(new Wire(2, ServerUrl, DeviceKey, LocalUrl, FactoryId), Json);

    /// <summary>العناوين بترتيب المحاولة: المحلي أولًا (أسرع وبلا إنترنت) ثم السحابي.</summary>
    public IReadOnlyList<string> Candidates =>
        new[] { LocalUrl, ServerUrl }.Where(u => !string.IsNullOrEmpty(u)).Select(u => u!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <returns>الرمز، أو رسالة خطأ عربية.</returns>
    public static (LinkCode? code, string? error) Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, "صوّر رمز الربط من شاشة «أجهزة التطبيق» في المعمل");
        Wire? w;
        try { w = JsonSerializer.Deserialize<Wire>(text.Trim(), Json); }
        catch (JsonException) { return (null, "هذا ليس رمز ربط تطبيق المندوب"); }
        if (w is null || w.V is not (1 or 2) || string.IsNullOrEmpty(w.K)) return (null, "هذا ليس رمز ربط تطبيق المندوب");
        if (w.K.Length != 64 || !w.K.All(Uri.IsHexDigit)) return (null, "مفتاح الجهاز في الرمز غير صحيح");
        if (CloudUrl.Validate(w.U) is string bad) return (null, bad);
        string? local = null;
        if (!string.IsNullOrWhiteSpace(w.L))
        {
            if (CloudUrl.Validate(w.L) is string badLocal) return (null, badLocal);
            local = w.L.Trim().TrimEnd('/');
        }
        var fid = w.F is { Length: FactoryIdentity.Length } f && f.All(Uri.IsHexDigit) ? f.ToUpperInvariant() : null;
        return (new LinkCode(w.U.Trim().TrimEnd('/'), w.K.ToUpperInvariant(), local, fid), null);
    }
}

/// <summary>
/// بصمة المعمل: أول 16 حرفًا من SHA-256 لمفتاح المعمل. يعلنها الخادم المحلي في ردّ الاكتشاف ويحملها رمز الربط،
/// فلا يتصل الهاتف بخادم معمل آخر على الشبكة نفسها. لا تكشف المفتاح.
/// </summary>
public static class FactoryIdentity
{
    public const int Length = 16;
    public static string Of(string agentKey) => CloudHash.Of("factory:" + agentKey)[..Length];
}
