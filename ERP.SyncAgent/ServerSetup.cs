using System.Diagnostics;
using System.Text.Json;
using ERP.Data.Services;
using Microsoft.Data.SqlClient;

namespace ERP.SyncAgent;

/// <summary>
/// تجهيز حاسبة السيرفر للنظامين معًا — يستدعيه المثبّت (بصلاحية المسؤول التي طلبها أصلًا)، ولا زر له داخل البرنامج:
/// 1) الخادم المحلي (ERP.Cloud.Api) خدمةَ Windows بحساب خاص NT SERVICE\ERPLocalRelay، قاعدته ملف SQLite،
///    على المنفذ 5080 مع اكتشاف الهواتف على 5081، وجدار الحماية مفتوح لشبكة المعمل المحلية فقط؛
/// 2) خدمة المزامنة بحساب NT SERVICE\ERPCloudSync تجد قواعد النظام على سيرفر SQL بنفسها وتضع مفتاح المعمل للخادم المحلي؛
/// 3) دخول الخدمة إلى SQL بقراءة وكتابة فقط، لكل قاعدة موجودة الآن (وكل مشروع جديد يمنحه البرنامج عند إنشائه).
/// آمن للتكرار: إعادة تشغيل المثبّت (تحديث) تعيد الضبط دون فقد شيء.
/// </summary>
public static class ServerSetup
{
    public const string RelayService = "ERPLocalRelay";
    public const string RelayAccount = @"NT SERVICE\" + RelayService;
    private const string TcpRule = "ERP Local Server TCP 5080";
    private const string UdpRule = "ERP Local Server Discovery UDP 5081";

    public static async Task<int> InstallAsync(string sqlServer, string? relayExe)
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("متاح على Windows فقط"); return 1; }
        if (!AgentSetup.IsAdmin()) { Console.Error.WriteLine("يُشغَّل من المثبّت بصلاحية المسؤول"); return 1; }
        try
        {
            var agentExe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ERP.SyncAgent.exe");
            relayExe ??= Path.GetFullPath(Path.Combine(Path.GetDirectoryName(agentExe)!, "..", "LocalServer", "ERP.Cloud.Api.exe"));
            if (!File.Exists(relayExe)) throw new FileNotFoundException($"لم يُعثر على الخادم المحلي: {relayExe}");

            // 1) مجلد الخادم المحلي وإعداده (يقرؤه الخادم بنفسه)
            var folder = AgentConfig.LocalServerFolder;
            Directory.CreateDirectory(folder);
            var local = AgentConfig.DefaultLocalRelay;
            File.WriteAllText(Path.Combine(folder, "relay.json"), JsonSerializer.Serialize(new
            {
                Relay = new
                {
                    SqlitePath = Path.Combine(folder, "relay.db"),
                    AgentKeyFile = local.KeyFile,
                    Listen = $"http://0.0.0.0:{local.Port}",
                    HttpPort = local.Port,
                    DiscoveryPort = ERP.Cloud.Contracts.LocalDiscovery.Port,
                    KeepDays = 30
                }
            }, new JsonSerializerOptions { WriteIndented = true }));
            Ok("إعداد الخادم المحلي");

            // 2) خدمة الخادم المحلي
            StopIfExists(RelayService);
            CreateOrUpdate(RelayService, relayExe, "auto", RelayAccount, "ERP Local Server",
                           "Water factory ERP: local relay for rep phones on the factory Wi-Fi (port 5080)");
            Ok("خدمة الخادم المحلي");

            // 3) خدمة المزامنة وإعدادها: تجد قواعد النظام على سيرفر SQL بنفسها
            var serverCs = new SqlConnectionStringBuilder
            {
                DataSource = sqlServer, IntegratedSecurity = true, TrustServerCertificate = true, InitialCatalog = "master"
            }.ConnectionString;
            var existing = AgentConfig.Load();
            new AgentConfig(existing?.ControlDbConnectionString is { Length: > 0 } cs && File.Exists(AgentConfig.MachinePath) ? cs : null,
                            serverCs, local).Save();
            StopIfExists(AgentSetup.ServiceName);
            CreateOrUpdate(AgentSetup.ServiceName, agentExe, "delayed-auto", AgentSetup.ServiceAccount, "ERP Cloud Sync",
                           "Water factory ERP: syncs rep phone requests via the local and cloud relays");
            Ok("خدمة المزامنة");

            // 4) الصلاحيات على الملفات: الإعداد للمسؤولين والخدمة؛ مجلد الخادم المحلي للخدمتين فقط
            AgentSetup.Run("icacls.exe", $"\"{AgentConfig.MachinePath}\" /inheritance:r /grant:r *S-1-5-32-544:F *S-1-5-18:F \"{AgentSetup.ServiceAccount}\":R", required: true);
            AgentSetup.Run("icacls.exe", $"\"{folder}\" /inheritance:r /grant:r *S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F " +
                                         $"\"{RelayAccount}\":(OI)(CI)M \"{AgentSetup.ServiceAccount}\":(OI)(CI)M /T", required: true);
            Ok("صلاحيات الملفات");

            // 5) جدار الحماية: شبكة المعمل المحلية فقط
            AgentSetup.Run("netsh.exe", $"advfirewall firewall delete rule name=\"{TcpRule}\"");
            AgentSetup.Run("netsh.exe", $"advfirewall firewall delete rule name=\"{UdpRule}\"");
            AgentSetup.Run("netsh.exe", $"advfirewall firewall add rule name=\"{TcpRule}\" dir=in action=allow protocol=TCP localport={local.Port} remoteip=localsubnet profile=any", required: true);
            AgentSetup.Run("netsh.exe", $"advfirewall firewall add rule name=\"{UdpRule}\" dir=in action=allow protocol=UDP localport={ERP.Cloud.Contracts.LocalDiscovery.Port} remoteip=localsubnet profile=any", required: true);
            Ok($"جدار الحماية: المنفذان {local.Port} و{ERP.Cloud.Contracts.LocalDiscovery.Port} لشبكة المعمل فقط");

            // 6) دخول خدمة المزامنة إلى SQL
            await GrantSqlAsync(serverCs);

            AgentSetup.Run("sc.exe", $"start {RelayService}");
            AgentSetup.Run("sc.exe", $"start {AgentSetup.ServiceName}");
            Ok("بدأت الخدمتان، وتبدآن تلقائيًا مع كل تشغيل للحاسبة");

            // 7) فحص ذاتي: الخادم المحلي يرد فعلًا؟ (المثبّت يُظهر النتيجة، فلا يبقى أي شك)
            var health = await WaitHealthAsync(local.Port);
            if (health) Ok($"فحص ذاتي: الخادم المحلي يعمل (http://localhost:{local.Port}/api/health)");
            else
            {
                Warn($"الفحص الذاتي: الخادم المحلي لم يرد بعد على المنفذ {local.Port}. أشخّص السبب وأحاول علاجه وحدي…");
                await DiagnoseAsync(relayExe, folder);

                // علاج: حساب «خدمة محلية» (Local Service) بدل الحساب الافتراضي للخدمة، مع صلاحية التعديل على مجلد الخادم
                Console.WriteLine("  محاولة العلاج: تشغيل الخادم المحلي بحساب «الخدمة المحلية»…");
                StopIfExists(RelayService);
                AgentSetup.Run("icacls.exe", $"\"{folder}\" /grant *S-1-5-19:(OI)(CI)M /T");
                AgentSetup.Run("sc.exe", $"config {RelayService} obj= \"NT AUTHORITY\\LocalService\" password= \"\"");
                AgentSetup.Run("sc.exe", $"start {RelayService}");
                if (await WaitHealthAsync(local.Port)) Ok($"الخادم المحلي يعمل الآن بعد العلاج (http://localhost:{local.Port}/api/health)");
                else
                {
                    Warn("لم يعمل الخادم المحلي رغم العلاج. صوّر هذه النافذة كلها وأرسلها للمطوّر، ففيها السبب.");
                    return 1;
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"تعذّر تجهيز المزامنة: {ex.Message}");
            return 1;
        }
    }

    /// <summary>تشخيص يُطبع في نافذة المثبّت: حساب الخدمة، صلاحيات المجلد، سجل الأخطاء، وتشغيل الخادم مباشرة بصلاحية المسؤول.</summary>
    private static async Task DiagnoseAsync(string relayExe, string folder)
    {
        Console.WriteLine("  ---------- تشخيص الخادم المحلي ----------");
        foreach (var line in RunText("sc.exe", $"qc {RelayService}").Split('\n').Where(l => l.Contains("BINARY_PATH") || l.Contains("SERVICE_START_NAME") || l.Contains("START_TYPE")))
            Console.WriteLine("  " + line.Trim());
        Console.WriteLine("  صلاحيات المجلد:");
        foreach (var line in RunText("icacls.exe", $"\"{folder}\"").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("Successfully")))
            Console.WriteLine("    " + line);
        var log = Path.Combine(folder, "relay-error.log");
        if (File.Exists(log))
        {
            Console.WriteLine("  سجل أخطاء الخادم (آخر الأسطر):");
            foreach (var line in File.ReadAllLines(log).TakeLast(12)) Console.WriteLine("    " + line);
        }
        else Console.WriteLine("  لا سجل أخطاء بعد (التوقف حدث قبل أن يكتب الخادم شيئًا).");

        // تشغيل مباشر بصلاحية المسؤول: يكشف هل العيب في البرنامج أم في صلاحيات حساب الخدمة
        try
        {
            using var p = Process.Start(new ProcessStartInfo(relayExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            var exited = p.WaitForExit(8000);
            if (!exited) { p.Kill(true); p.WaitForExit(); }
            var text = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
            if (!exited) Console.WriteLine("  تشغيل مباشر بصلاحية المسؤول: يعمل ✔ (فالعيب في صلاحيات حساب الخدمة)");
            else Console.WriteLine($"  تشغيل مباشر بصلاحية المسؤول: توقف (رمز {p.ExitCode}):\n    " + string.Join("\n    ", text.Split('\n').Take(8).Select(l => l.TrimEnd())));
        }
        catch (Exception ex) { Console.WriteLine($"  تعذّر التشغيل المباشر: {ex.Message}"); }
        Console.WriteLine("  -----------------------------------------");
        await Task.CompletedTask;
    }

    private static string RunText(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return text;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>ينتظر حتى 30 ثانية أن يرد الخادم المحلي على /api/health.</summary>
    private static async Task<bool> WaitHealthAsync(int port)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        for (var i = 0; i < 15; i++)
        {
            try { if ((await http.GetAsync($"http://127.0.0.1:{port}/api/health")).IsSuccessStatusCode) return true; }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(2000);
        }
        return false;
    }

    private static async Task GrantSqlAsync(string serverCs)
    {
        try
        {
            await using (var conn = new SqlConnection(serverCs))
            {
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(
                    $"IF SUSER_ID(N'{AgentSetup.ServiceAccount}') IS NULL CREATE LOGIN [{AgentSetup.ServiceAccount}] FROM WINDOWS;", conn);
                await cmd.ExecuteNonQueryAsync();
            }
            var controls = await CloudSyncRunner.DiscoverControlDatabasesAsync(serverCs);
            foreach (var control in controls) await AgentSetup.GrantAsync(control);
            Ok(controls.Count == 0
                ? "دخول خدمة المزامنة إلى SQL (المشاريع الجديدة تُمنح تلقائيًا عند إنشائها)"
                : $"دخول خدمة المزامنة إلى SQL ({controls.Count} نظام مثبّت)");
        }
        catch (SqlException ex)
        {
            Warn($"لم يكتمل دخول SQL ({ex.Message}). يُكمله البرنامج عند أول دخول، أو أعد تشغيل المثبّت.");
        }
    }

    public static int Uninstall()
    {
        if (!OperatingSystem.IsWindows() || !AgentSetup.IsAdmin()) { Console.Error.WriteLine("شغّل الإزالة كمسؤول على Windows"); return 1; }
        foreach (var name in new[] { AgentSetup.ServiceName, RelayService })
        {
            StopIfExists(name);
            AgentSetup.Run("sc.exe", $"delete {name}");
        }
        AgentSetup.Run("netsh.exe", $"advfirewall firewall delete rule name=\"{TcpRule}\"");
        AgentSetup.Run("netsh.exe", $"advfirewall firewall delete rule name=\"{UdpRule}\"");
        Console.WriteLine("أُزيل الخادم المحلي وخدمة المزامنة (البيانات في المعمل لم تُمس)");
        return 0;
    }

    private static void StopIfExists(string name)
    {
        if (AgentSetup.Run("sc.exe", $"query {name}") != 0) return;
        AgentSetup.Run("sc.exe", $"stop {name}");
        Thread.Sleep(2000);
    }

    private static void CreateOrUpdate(string name, string exe, string start, string account, string display, string description)
    {
        var bin = $"binPath= \"\\\"{exe}\\\"\" start= {start} obj= \"{account}\"";
        if (AgentSetup.Run("sc.exe", $"query {name}") == 0) AgentSetup.Run("sc.exe", $"config {name} {bin} DisplayName= \"{display}\"", required: true);
        else AgentSetup.Run("sc.exe", $"create {name} {bin} DisplayName= \"{display}\"", required: true);
        AgentSetup.Run("sc.exe", $"description {name} \"{description}\"");
        AgentSetup.Run("sc.exe", $"failure {name} reset= 86400 actions= restart/60000/restart/60000/restart/60000");
    }

    private static void Ok(string m) => Console.WriteLine($"  [تم]  {m}");
    private static void Warn(string m) => Console.WriteLine($"  [!]   {m}");
}
