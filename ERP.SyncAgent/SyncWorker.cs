using ERP.Data.Services;

namespace ERP.SyncAgent;

/// <summary>الحلقة الدائمة: دورة مزامنة لكل مشروع مفعّل، ثم انتظار الفترة المضبوطة. لا يوقفها خطأ.</summary>
public class SyncWorker(AgentConfig config, ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runner = new CloudSyncRunner(config.ControlDbConnectionString, Environment.MachineName);
        logger.LogInformation("بدأت خدمة المزامنة السحابية على {Machine}", Environment.MachineName);
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = await runner.RunAllAsync(m => logger.LogInformation("{Message}", m), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "خطأ غير متوقع في دورة المزامنة");
                delay = CloudSyncRunner.IdleDelay;
            }
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
