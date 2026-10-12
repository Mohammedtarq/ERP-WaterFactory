using ERP.Data.Services;
using ERP.SyncAgent;

// ERP.SyncAgent.exe            ← يعمل كخدمة Windows (أو في نافذة للتجربة)
// ERP.SyncAgent.exe --once     ← دورة واحدة ويطبع النتيجة
// ERP.SyncAgent.exe --print-key ← يطبع مفتاح المعمل المولَّد (لإعداد تجربة المندوبين)
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
if (args.Contains("--print-key"))
{
    // يطبع مفتاح المعمل وحده (لملف إعداد «تجربة المندوبين» الذي يضعه في الخادم الوسيط)
    try
    {
        var keys = await CloudSyncRunner.ReadAgentKeysAsync(config.ControlDbConnectionString);
        if (keys.Count == 0)
        {
            Console.Error.WriteLine("لا مفتاح للمعمل بعد: ولّده من «الإعدادات ← المزامنة السحابية ← توليد مفتاح جديد».");
            return 3;
        }
        if (keys.Count > 1) Console.Error.WriteLine($"تنبيه: {keys.Count} مشاريع لها مفاتيح؛ استُعمل مفتاح «{keys[0].Project}».");
        Console.WriteLine(keys[0].Key);
        return 0;
    }
    catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
    {
        Console.Error.WriteLine($"تعذّر الاتصال بقاعدة التحكم: {ex.Message}");
        return 4;
    }
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
