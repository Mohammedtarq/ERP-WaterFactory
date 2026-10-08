using System.Diagnostics;
using System.Security.Principal;
using ERP.Data.ControlDb;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ERP.SyncAgent;

/// <summary>
/// تثبيت الخدمة على جهاز السيرفر (كمسؤول): حفظ الإعداد بصلاحيات مقيّدة، وإنشاء خدمة Windows بحساب خدمة خاص
/// (NT SERVICE\ERPCloudSync)، ومنحه القراءة والكتابة والتنفيذ في قاعدة التحكم وقواعد المشاريع، ثم التشغيل.
/// </summary>
public static class AgentSetup
{
    public const string ServiceName = "ERPCloudSync";
    public const string ServiceAccount = @"NT SERVICE\" + ServiceName;

    public const string Usage = """
        خدمة المزامنة السحابية — نظام معمل المياه
          ERP.SyncAgent.exe                         تشغيل (كخدمة Windows أو في نافذة للتجربة)
          ERP.SyncAgent.exe --once                  دورة مزامنة واحدة وطباعة النتيجة
          ERP.SyncAgent.exe --install --control "<سلسلة اتصال قاعدة التحكم>"   تثبيت الخدمة (كمسؤول)
          ERP.SyncAgent.exe --uninstall             إزالة الخدمة (كمسؤول)
        الأسهل: شاشة «الإعدادات ← المزامنة السحابية» ← «تثبيت الخدمة على هذا الجهاز».
        """;

    private static bool IsAdmin() =>
        OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static async Task<int> InstallAsync(string? controlCs)
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("التثبيت كخدمة متاح على Windows فقط"); return 1; }
        if (!IsAdmin()) { Console.Error.WriteLine("شغّل التثبيت كمسؤول (Run as administrator)"); return 1; }
        if (string.IsNullOrWhiteSpace(controlCs)) { Console.Error.WriteLine("مرّر سلسلة اتصال قاعدة التحكم بعد --control"); return 1; }
        try
        {
            // 1) الاتصال صحيح قبل أي تغيير
            await using (var conn = new SqlConnection(controlCs)) await conn.OpenAsync();

            // 2) الإعداد: للمسؤولين والنظام والخدمة فقط (قد يحوي كلمة مرور SQL)
            var config = new AgentConfig(controlCs);
            config.Save();

            // 3) الخدمة: تبدأ مع Windows، وتُعاد تلقائيًا إن توقفت
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ERP.SyncAgent.exe");
            if (Run("sc.exe", $"query {ServiceName}") == 0)
            {
                Run("sc.exe", $"stop {ServiceName}");
                Thread.Sleep(2000);
                Run("sc.exe", $"config {ServiceName} binPath= \"\\\"{exe}\\\"\" start= delayed-auto obj= \"{ServiceAccount}\"", required: true);
            }
            else
                Run("sc.exe", $"create {ServiceName} binPath= \"\\\"{exe}\\\"\" start= delayed-auto obj= \"{ServiceAccount}\" DisplayName= \"ERP Cloud Sync\"", required: true);
            Run("sc.exe", $"description {ServiceName} \"Water factory ERP: syncs rep phone requests via the cloud relay (outbound only)\"");
            Run("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/60000");
            Run("icacls.exe", $"\"{AgentConfig.MachinePath}\" /inheritance:r /grant:r *S-1-5-32-544:F *S-1-5-18:F \"{ServiceAccount}\":R", required: true);

            // 4) الوصول للقواعد بمصادقة Windows (مع مستخدم SQL يكفي ما في الإعداد)
            if (new SqlConnectionStringBuilder(controlCs).IntegratedSecurity) await GrantAsync(controlCs);

            Run("sc.exe", $"start {ServiceName}", required: true);
            Console.WriteLine("ثُبّتت خدمة المزامنة السحابية وبدأت العمل. فعّل المزامنة من شاشة «المزامنة السحابية».");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"تعذّر التثبيت: {ex.Message}");
            return 1;
        }
    }

    /// <summary>حساب الخدمة: قراءة وكتابة وتنفيذ في قاعدة التحكم وكل قاعدة مشروع على هذا السيرفر.</summary>
    private static async Task GrantAsync(string controlCs)
    {
        var databases = new List<string> { new SqlConnectionStringBuilder(controlCs).InitialCatalog };
        await using (var control = new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>().UseSqlServer(controlCs).Options))
            databases.AddRange(await control.Projects.Where(p => p.IsActive).Select(p => p.DatabaseName).ToListAsync());

        await using var conn = new SqlConnection(controlCs);
        await conn.OpenAsync();
        await Exec(conn, $"IF SUSER_ID(N'{ServiceAccount}') IS NULL CREATE LOGIN [{ServiceAccount}] FROM WINDOWS;");
        foreach (var db in databases.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var q = db.Replace("]", "]]");
            await Exec(conn, $"""
                USE [{q}];
                IF USER_ID(N'{ServiceAccount}') IS NULL CREATE USER [{ServiceAccount}] FOR LOGIN [{ServiceAccount}];
                ALTER ROLE db_datareader ADD MEMBER [{ServiceAccount}];
                ALTER ROLE db_datawriter ADD MEMBER [{ServiceAccount}];
                GRANT EXECUTE TO [{ServiceAccount}];
                """);
        }
    }

    private static async Task Exec(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public static int Uninstall()
    {
        if (!OperatingSystem.IsWindows() || !IsAdmin()) { Console.Error.WriteLine("شغّل الإزالة كمسؤول على Windows"); return 1; }
        Run("sc.exe", $"stop {ServiceName}");
        Thread.Sleep(2000);
        var code = Run("sc.exe", $"delete {ServiceName}");
        Console.WriteLine(code == 0 ? "أُزيلت خدمة المزامنة السحابية" : "الخدمة غير مثبّتة");
        return 0;
    }

    private static int Run(string file, string arguments, bool required = false)
    {
        using var p = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (required && p.ExitCode != 0) throw new InvalidOperationException($"{file} {arguments.Split(' ')[0]}: {output.Trim()}");
        return p.ExitCode;
    }
}
