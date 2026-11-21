using ERP.Data.Services;
using ERP.SyncAgent;

// ERP.SyncAgent.exe              ← يعمل كخدمة Windows (أو في نافذة للتجربة)
// ERP.SyncAgent.exe --once       ← دورة واحدة ويطبع النتيجة
// ERP.SyncAgent.exe --print-key  ← يطبع مفتاح المعمل المولَّد
// ERP.SyncAgent.exe --install-server [--sql "localhost\SQLEXPRESS"]  (كمسؤول، يستدعيه المثبّت) ← الخادم المحلي + خدمة المزامنة
// ERP.SyncAgent.exe --uninstall-server  (كمسؤول)
// ERP.SyncAgent.exe --install --control "<سلسلة اتصال قاعدة التحكم>"   (كمسؤول، الطريقة القديمة)
// ERP.SyncAgent.exe --uninstall  (كمسؤول)
string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(AgentSetup.Usage);
    return 0;
}
if (args.Contains("--install-server")) return await ServerSetup.InstallAsync(Arg("--sql") ?? @"localhost\SQLEXPRESS", Arg("--relay"));
if (args.Contains("--uninstall-server")) return ServerSetup.Uninstall();
if (args.Contains("--install")) return await AgentSetup.InstallAsync(Arg("--control"));
if (args.Contains("--uninstall")) return AgentSetup.Uninstall();

var config = AgentConfig.Load();
if (config is null)
{
    Console.Error.WriteLine($"لا يوجد إعداد للخدمة ({AgentConfig.MachinePath}). يكتبه المثبّت بوضع «السيرفر».");
    return 2;
}
if (args.Contains("--print-key"))
{
    try
    {
        var keys = new List<(string Project, string Key)>();
        foreach (var cs in await config.ControlDatabasesAsync()) keys.AddRange(await CloudSyncRunner.ReadAgentKeysAsync(cs));
        if (keys.Count == 0)
        {
            Console.Error.WriteLine("لا مفتاح للمعمل بعد: يُولَّد تلقائيًا عند تفعيل المزامنة من «الإعدادات ← المزامنة».");
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
    foreach (var cs in await config.ControlDatabasesAsync())
        await new CloudSyncRunner(cs, Environment.MachineName, local: config.LocalRelay).RunAllAsync(Console.WriteLine);
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = AgentSetup.ServiceName);
builder.Services.AddSingleton(config);
builder.Services.AddHostedService<SyncWorker>();
await builder.Build().RunAsync();
return 0;
