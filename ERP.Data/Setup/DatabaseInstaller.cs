using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace ERP.Data.Setup;

/// <summary>
/// ينفّذ سكربتات Database/*.sql المضمَّنة في البرنامج ويتتبّعها في جدول SchemaVersions داخل كل قاعدة:
/// - الملفات 01 → 08 (المخطط الأساسي): تُنفَّذ مرة واحدة. قاعدة أُنشئت سابقًا يدويًا من SSMS تُكتشف
///   بوجود جدولها المميّز فتُسجَّل كمنفّذة دون إعادة تنفيذ.
/// - الملفات 09 فما بعد: قابلة لإعادة التنفيذ بأمان، وتُنفَّذ كلما تغيّر محتواها (بصمة SHA-256)؛
///   هكذا تصل أي تحديثات لاحقة لكل القواعد تلقائيًا عند فتح المشروع.
/// </summary>
public static class DatabaseInstaller
{
    private const string ResourcePrefix = "ERP.Database.";
    public const string ControlScript = "00_control_db.sql";

    /// <summary>ملفات المخطط الأساسي وجدول يدل على أنها نُفّذت (للقواعد المنشأة يدويًا قبل المُثبِّت).</summary>
    private static readonly Dictionary<string, string> BaseScriptProbes = new()
    {
        ["01_core_and_security.sql"] = "Branches",
        ["02_finance.sql"] = "ChartOfAccounts",
        ["03_items_warehouses.sql"] = "Items",
        ["04_suppliers_purchasing.sql"] = "Suppliers",
        ["05_sales_customers.sql"] = "Customers",
        ["06_hr_payroll.sql"] = "AttendanceRecords",
        ["07_reps.sql"] = "RepWalletTransactions",
        ["08_production.sql"] = "BillOfMaterials",
    };

    public static IReadOnlyList<string> ProjectScripts =>
        Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix) && n.EndsWith(".sql"))
            .Select(n => n[ResourcePrefix.Length..])
            .Where(n => n != ControlScript)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    public static string ReadScript(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourcePrefix + name)
                           ?? throw new InvalidOperationException($"السكربت {name} غير مضمَّن في البرنامج");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>يقسّم السكربت على أسطر GO (كما يفعل SSMS) وينفّذ كل دفعة.</summary>
    public static async Task ExecuteScriptAsync(SqlConnection conn, string script, SqlTransaction? tx = null)
    {
        foreach (var batch in Regex.Split(script, @"^\s*GO\s*;?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            await using var cmd = new SqlCommand(batch, conn, tx) { CommandTimeout = 300 };
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task<object?> ScalarAsync(SqlConnection conn, string sql, params (string name, object value)[] ps)
    {
        await using var cmd = new SqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return await cmd.ExecuteScalarAsync();
    }

    /// <summary>ينشئ قاعدة بيانات فارغة (بترتيب عربي) إن لم تكن موجودة.</summary>
    public static async Task<bool> EnsureDatabaseAsync(string anyConnectionString, string databaseName)
    {
        if (!Regex.IsMatch(databaseName, @"^[A-Za-z_][A-Za-z0-9_]{0,100}$"))
            throw new ArgumentException("اسم قاعدة البيانات يجب أن يكون بأحرف إنجليزية وأرقام وشرطة سفلية فقط (مثل ERP_Project_WaterFactory)");

        var master = new SqlConnectionStringBuilder(anyConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await using var conn = new SqlConnection(master);
        await conn.OpenAsync();
        if (await ScalarAsync(conn, "SELECT DB_ID(@n)", ("@n", databaseName)) is not DBNull and not null) return false;
        await using var cmd = new SqlCommand($"CREATE DATABASE [{databaseName}] COLLATE Arabic_CI_AS", conn) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync();
        return true;
    }

    /// <summary>قاعدة التحكم: الجداول الثلاثة (المشاريع، المستخدمون العامون، الصلاحيات) إن لم تكن موجودة.</summary>
    public static async Task EnsureControlSchemaAsync(string controlConnectionString)
    {
        var db = new SqlConnectionStringBuilder(controlConnectionString).InitialCatalog;
        await EnsureDatabaseAsync(controlConnectionString, db);
        await using var conn = new SqlConnection(controlConnectionString);
        await conn.OpenAsync();
        if (await ScalarAsync(conn, "SELECT OBJECT_ID('Projects', 'U')") is not DBNull and not null) return;

        // السكربت الأصلي ينشئ القاعدة باسم ثابت؛ هنا القاعدة موجودة مسبقًا بالاسم المختار
        var script = Regex.Replace(ReadScript(ControlScript), @"^\s*(CREATE DATABASE|USE)\s+ERP_ControlDB\s*;?\s*$", "",
                                   RegexOptions.Multiline | RegexOptions.IgnoreCase);
        await ExecuteScriptAsync(conn, script);
    }

    /// <summary>
    /// يرقّي قاعدة مشروع إلى آخر نسخة. آمن للتكرار: يعيد قائمة السكربتات التي نُفّذت فعلًا (فارغة إن كانت محدّثة).
    /// </summary>
    public static async Task<IReadOnlyList<string>> UpgradeProjectAsync(string projectConnectionString)
    {
        var applied = new List<string>();
        await using var conn = new SqlConnection(projectConnectionString);
        await conn.OpenAsync();

        await ExecuteScriptAsync(conn, """
            IF OBJECT_ID('SchemaVersions', 'U') IS NULL
                CREATE TABLE SchemaVersions (
                    ScriptName  NVARCHAR(200)   NOT NULL PRIMARY KEY,
                    ScriptHash  NVARCHAR(64)    NOT NULL,
                    AppliedAt   DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
                );
            """);

        var recorded = new Dictionary<string, string>();
        await using (var cmd = new SqlCommand("SELECT ScriptName, ScriptHash FROM SchemaVersions", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) recorded[r.GetString(0)] = r.GetString(1);

        foreach (var name in ProjectScripts)
        {
            var script = ReadScript(name);
            var hash = Hash(script);
            bool isBase = BaseScriptProbes.TryGetValue(name, out var probeTable);

            if (isBase)
            {
                if (recorded.ContainsKey(name)) continue;                          // المخطط الأساسي لا يُعاد
                bool exists = await ScalarAsync(conn, "SELECT OBJECT_ID(@t, 'U')", ("@t", probeTable!)) is not DBNull and not null;
                if (!exists)
                {
                    await ExecuteScriptAsync(conn, script);
                    applied.Add(name);
                }
            }
            else
            {
                if (recorded.TryGetValue(name, out var h) && h == hash) continue;  // لم يتغيّر
                await ExecuteScriptAsync(conn, script);
                applied.Add(name);
            }

            await using var up = new SqlCommand("""
                MERGE SchemaVersions AS t USING (SELECT @n AS ScriptName) AS s ON t.ScriptName = s.ScriptName
                WHEN MATCHED THEN UPDATE SET ScriptHash = @h, AppliedAt = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN INSERT (ScriptName, ScriptHash) VALUES (@n, @h);
                """, conn);
            up.Parameters.AddWithValue("@n", name);
            up.Parameters.AddWithValue("@h", hash);
            await up.ExecuteNonQueryAsync();
        }
        return applied;
    }
}
