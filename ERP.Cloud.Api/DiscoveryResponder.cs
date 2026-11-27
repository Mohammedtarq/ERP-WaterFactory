using System.Net.Sockets;
using System.Text;
using ERP.Cloud.Contracts;

namespace ERP.Cloud.Api;

/// <summary>
/// يرد على هواتف المعمل التي تبحث عن خادمها المحلي بعد تغيّر عنوان الحاسبة (Relay:DiscoveryPort، يعمل في الخادم المحلي فقط).
/// الرد: منفذ الخادم وبصمة المعمل؛ الهاتف لا يقبل إلا بصمة معمله، ويبقى مفتاح الجهاز هو ما يعرّفه.
/// </summary>
public class DiscoveryResponder(IConfiguration config, AgentKeyProvider keys, ILogger<DiscoveryResponder> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = config.GetValue("Relay:DiscoveryPort", 0);
        var httpPort = config.GetValue("Relay:HttpPort", 5080);
        if (port <= 0) return;
        UdpClient udp;
        try { udp = new UdpClient(port); }
        catch (SocketException ex)
        {
            logger.LogWarning("تعذّر فتح منفذ الاكتشاف {Port}: {Message}", port, ex.Message);
            return;
        }
        using (udp)
        {
            logger.LogInformation("الاكتشاف على المنفذ {Port}", port);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var r = await udp.ReceiveAsync(stoppingToken);
                    if (Encoding.ASCII.GetString(r.Buffer) != LocalDiscovery.Probe || keys.FactoryId is not { } fid) continue;
                    await udp.SendAsync(LocalDiscovery.ReplyBytes(httpPort, fid), r.RemoteEndPoint, stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { /* هاتف أغلق قبل الرد: يُتجاهل */ }
            }
        }
    }
}
