using ERP.Data.Services;
using ERP.SyncAgent;

// ERP.SyncAgent.exe            ← يعمل كخدمة Windows (أو في نافذة للتجربة)
// ERP.SyncAgent.exe --once     ← دورة واحدة ويطبع النتيجة
// ERP.SyncAgent.exe --install --control "<سلسلة اتصال قاعدة التحكم>"   (كمسؤول) ← يثبّت الخدمة ويمنحها الوصول للقواعد
// ERP.SyncAgent.exe --uninstall  (كمسؤول)
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(AgentSetup.Usage);
    return 0;
}
if (args.Contains("--install"))
{
    var i = Array.IndexOf(args, "--control");
    return await AgentSetup.InstallAsync(i >= 0 && i + 1 < args.Length ? args[i + 1] : null);
}
if (args.Contains("--uninstall")) return AgentSetup.Uninstall();

var config = AgentConfig.Load();
if (config is null)
{
    Console.Error.WriteLine($"لا يوجد إعداد للخدمة ({AgentConfig.MachinePath}). ثبّتها من شاشة «المزامنة السحابية» أو بـ --install.");
    return 2;
}
if (args.Contains("--once"))
{
    await new CloudSyncRunner(config.ControlDbConnectionString, Environment.MachineName).RunAllAsync(Console.WriteLine);
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = AgentSetup.ServiceName);
builder.Services.AddSingleton(config);
builder.Services.AddHostedService<SyncWorker>();
await builder.Build().RunAsync();
return 0;
