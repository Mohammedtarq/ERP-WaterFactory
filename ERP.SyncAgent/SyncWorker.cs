using ERP.Data.Services;

namespace ERP.SyncAgent;

/// <summary>
/// الحلقة الدائمة: دورة مزامنة لكل مشروع مفعّل في كل قاعدة تحكم، ثم انتظار الفترة المضبوطة. لا يوقفها خطأ.
/// قواعد التحكم تُكتشف من جديد كل دقيقة (مشروع أو نظام جديد يدخل دون إعادة تشغيل).
/// </summary>
public class SyncWorker(AgentConfig config, ILogger<SyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Rediscover = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runners = new Dictionary<string, CloudSyncRunner>(StringComparer.OrdinalIgnoreCase);
        var controls = new List<string>();
        var lastDiscovery = DateTime.MinValue;
        logger.LogInformation("بدأت خدمة المزامنة على {Machine}", Environment.MachineName);
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = CloudSyncRunner.IdleDelay;
            try
            {
                if (DateTime.UtcNow - lastDiscovery > Rediscover || controls.Count == 0)
                {
                    try
                    {
                        controls = await config.ControlDatabasesAsync(stoppingToken);
                        lastDiscovery = DateTime.UtcNow;
                        if (controls.Count == 0) logger.LogInformation("لا نظام مثبّت على هذا السيرفر بعد — تبدأ المزامنة حين يُنشأ");
                    }
                    catch (Microsoft.Data.SqlClient.SqlException ex) { logger.LogWarning("تعذّر الاتصال بسيرفر SQL: {Message}", ex.Message); }
                }
                foreach (var cs in controls)
                {
                    if (!runners.TryGetValue(cs, out var runner))
                        runners[cs] = runner = new CloudSyncRunner(cs, Environment.MachineName, local: config.LocalRelay);
                    var d = await runner.RunAllAsync(m => logger.LogInformation("{Message}", m), stoppingToken);
                    if (d < delay) delay = d;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "خطأ غير متوقع في دورة المزامنة");
            }
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
