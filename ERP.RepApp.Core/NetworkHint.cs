using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ERP.RepApp.Core;

/// <summary>
/// سبب «لا اتصال» الأكثر شيوعًا داخل المعمل: الهاتف على شبكة غير شبكة الحاسبة (راوتر آخر أو شبكة 5G بأرقام مختلفة)،
/// فلا يراها مهما حاول. يُقارن أول ثلاثة أرقام من عنوان الهاتف بعنوان الخادم المحلي.
/// </summary>
public static class NetworkHint
{
    /// <returns>رسالة واضحة إن كان الهاتف على شبكة محلية مختلفة عن الخادم المحلي، وإلا null.</returns>
    public static string? Mismatch(string? localUrl, IEnumerable<IPAddress> phoneAddresses)
    {
        if (localUrl is null || !Uri.TryCreate(localUrl, UriKind.Absolute, out var u) || !IPAddress.TryParse(u.Host, out var server)
            || server.AddressFamily != AddressFamily.InterNetwork) return null;
        var lan = phoneAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork && Cloud.Contracts.CloudUrl.IsPrivateLan(a.ToString())).ToList();
        if (lan.Count == 0) return null;                                    // بيانات الهاتف فقط: لا مقارنة
        static string Prefix(IPAddress a) => string.Join('.', a.GetAddressBytes().Take(3));
        if (lan.Any(a => Prefix(a) == Prefix(server))) return null;
        return $"هاتفك على شبكة {Prefix(lan[0])}.x والحاسبة على {Prefix(server)}.x — اربط الهاتف بواي فاي الحاسبة نفسه";
    }

    /// <summary>عناوين الهاتف الحالية (IPv4) من بطاقات الشبكة الفعّالة.</summary>
    public static List<IPAddress> PhoneAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)).ToList();
        }
        catch (Exception) { return new(); }                              // بعض الأجهزة تمنع القراءة: لا تلميح
    }
}
