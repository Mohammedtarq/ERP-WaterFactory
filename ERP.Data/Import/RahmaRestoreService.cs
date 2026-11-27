using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace ERP.Data.Import;

public class RahmaRestoreResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string DatabaseName { get; init; } = "";
    public double SizeMb { get; init; }
}

/// <summary>
/// «استعادة نسخة الرحمة»: يستعيد ملف نسخة احتياطية (.bak) من نظام الرحمة كقاعدة <b>منفصلة جديدة</b> على السيرفر
/// (لا يلمس قاعدة النظام ولا يستبدل شيئًا)، ثم يتحقق أنها قاعدة الرحمة فعلًا وإلا يحذفها ويرفض.
/// SQL Server هو من يقرأ الملف، فالمسار يجب أن يكون على جهاز السيرفر.
/// </summary>
public static class RahmaRestoreService
{
    /// <summary>اسم القاعدة المستعادة من اسم الملف (ALRAHMA2026-11-15-1607.bak ← ALRAHMA_2026_11_15_1607) أو من تاريخ الاستعادة.</summary>
    public static string SuggestName(string backupFile, DateTime now)
    {
        var file = Path.GetFileNameWithoutExtension(backupFile.Replace('\\', '/').Split('/').Last());
        var m = Regex.Match(file, @"(\d{4})-(\d{2})-(\d{2})-(\d{4})");
        return m.Success ? $"ALRAHMA_{m.Groups[1].Value}_{m.Groups[2].Value}_{m.Groups[3].Value}_{m.Groups[4].Value}" : $"ALRAHMA_{now:yyyy_MM_dd_HHmm}";
    }

    public static async Task<RahmaRestoreResult> RestoreAsync(string serverConnectionString, string backupFile)
    {
        backupFile = (backupFile ?? "").Trim().Trim('"');
        if (backupFile.Length == 0) return Fail("اختر ملف النسخة الاحتياطية (.bak)");
        if (!backupFile.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) return Fail("الملف ليس نسخة SQL Server احتياطية (.bak)");

        var master = new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = "master", Pooling = false }.ConnectionString;
        string? created = null;
        try
        {
            await using var conn = new SqlConnection(master);
            await conn.OpenAsync();

            async Task<List<object?[]>> Rows(string sql, params (string, object)[] ps)
            {
                await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
                foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
                var list = new List<object?[]>();
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync()) { var row = new object?[rd.FieldCount]; rd.GetValues(row!); list.Add(row); }
                return list;
            }

            List<object?[]> files;
            try { files = await Rows("RESTORE FILELISTONLY FROM DISK = @p", ("@p", backupFile)); }
            catch (SqlException ex)
            {
                return Fail($"لا يستطيع SQL Server قراءة الملف. تأكد أنه على جهاز السيرفر نفسه وأن المسار صحيح.\n({ex.Message.Split('\n')[0]})");
            }
            if (files.Count == 0) return Fail("الملف لا يحوي قاعدة بيانات");

            var dataDir = (await Rows("SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS NVARCHAR(400))")).FirstOrDefault()?[0] as string;
            var logDir = (await Rows("SELECT CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS NVARCHAR(400))")).FirstOrDefault()?[0] as string ?? dataDir;
            if (string.IsNullOrWhiteSpace(dataDir)) return Fail("تعذّر معرفة مجلد قواعد البيانات على السيرفر");
            var sep = dataDir.Contains('/') && !dataDir.Contains('\\') ? "/" : "\\";

            // اسم جديد غير مستعمل
            var baseName = SuggestName(backupFile, DateTime.Now);
            var name = baseName;
            for (var n = 2; (await Rows("SELECT DB_ID(@n)", ("@n", name)))[0][0] is not (null or DBNull); n++) name = $"{baseName}_{n}";   // DB_ID يعيد NULL إن لم توجد

            var moves = new List<string>();
            for (var i = 0; i < files.Count; i++)
            {
                var logical = (string)files[i][0]!;
                var type = (string)files[i][2]!;           // D = بيانات، L = سجل
                var dir = (type == "L" ? logDir : dataDir)!.TrimEnd('\\', '/');
                var ext = type == "L" ? ".ldf" : i == 0 ? ".mdf" : ".ndf";
                moves.Add($"MOVE N'{logical.Replace("'", "''")}' TO N'{dir}{sep}{name}_{i}{ext}'");
            }
            try
            {
                await using var cmd = new SqlCommand($"RESTORE DATABASE [{name}] FROM DISK = @p WITH {string.Join(", ", moves)}, RECOVERY", conn) { CommandTimeout = 0 };
                cmd.Parameters.AddWithValue("@p", backupFile);
                await cmd.ExecuteNonQueryAsync();
                created = name;
            }
            catch (SqlException ex)
            {
                await DropAsync(conn, name);   // قد تبقى قاعدة نصف مستعادة
                return Fail("تعذّرت الاستعادة: " + ex.Message.Split('\n')[0]);
            }

            // هل هي قاعدة الرحمة فعلًا؟
            var candidates = await RahmaLegacyReader.FindCandidatesAsync(serverConnectionString);
            if (!candidates.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                await DropAsync(conn, name);
                created = null;
                return Fail("الملف ليس نسخة من نظام الرحمة (لا يحوي جداوله)، فحُذفت القاعدة المستعادة ولم يتغير شيء.");
            }
            var sizeRaw = (await Rows($"SELECT SUM(CAST(size AS BIGINT)) * 8.0 / 1024 FROM [{name}].sys.database_files"))[0][0];
            var size = sizeRaw is null or DBNull ? 0 : Convert.ToDouble(sizeRaw);
            created = null;
            return new RahmaRestoreResult { Success = true, DatabaseName = name, SizeMb = Math.Round(size, 1) };
        }
        catch (SqlException ex)
        {
            if (created is not null) { try { await using var c2 = new SqlConnection(master); await c2.OpenAsync(); await DropAsync(c2, created); } catch (SqlException) { } }
            return Fail("تعذّر الاتصال بالسيرفر: " + ex.Message.Split('\n')[0]);
        }
    }

    private static async Task DropAsync(SqlConnection conn, string name)
    {
        try
        {
            await using var cmd = new SqlCommand($"IF DB_ID(@n) IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END", conn);
            cmd.Parameters.AddWithValue("@n", name);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqlException) { }
    }

    private static RahmaRestoreResult Fail(string message) => new() { Success = false, Error = message };
}
