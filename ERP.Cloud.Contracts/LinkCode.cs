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
/// رمز ربط الهاتف (يُعرض QR في «أجهزة التطبيق» ويصوّره المندوب): عنوان الخادم ومفتاح الجهاز.
/// </summary>
public record LinkCode(string ServerUrl, string DeviceKey)
{
    private record Wire(int V, string U, string K);

    public string Encode() => JsonSerializer.Serialize(new Wire(1, ServerUrl, DeviceKey), new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <returns>الرمز، أو رسالة خطأ عربية.</returns>
    public static (LinkCode? code, string? error) Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, "صوّر رمز الربط من شاشة «أجهزة التطبيق» في المعمل");
        Wire? w;
        try { w = JsonSerializer.Deserialize<Wire>(text.Trim(), new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return (null, "هذا ليس رمز ربط تطبيق المندوب"); }
        if (w is null || w.V != 1 || string.IsNullOrEmpty(w.K)) return (null, "هذا ليس رمز ربط تطبيق المندوب");
        if (w.K.Length != 64 || !w.K.All(Uri.IsHexDigit)) return (null, "مفتاح الجهاز في الرمز غير صحيح");
        if (CloudUrl.Validate(w.U) is string bad) return (null, bad);
        return (new LinkCode(w.U.Trim().TrimEnd('/'), w.K.ToUpperInvariant()), null);
    }
}
