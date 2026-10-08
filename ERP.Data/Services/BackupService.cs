using Microsoft.Data.SqlClient;

namespace ERP.Data.Services;

public class BackupHistoryRow
{
    public string DatabaseName { get; init; } = "";
    public DateTime FinishedAt { get; init; }
    public decimal SizeMb { get; init; }
    public string FilePath { get; init; } = "";
}

/// <summary>
/// نسخ احتياطي كامل (COPY_ONLY: لا يقطع سلسلة أي نسخ مجدولة أخرى) عبر SQL Server نفسه.
/// المسار على جهاز السيرفر وليس جهاز المستخدم — SQL Server هو من يكتب الملف.
/// </summary>
public class BackupService
{
    private readonly string _connectionString;
    public BackupService(string connectionString) => _connectionString = connectionString;

    /// <summary>مجلد النسخ الافتراضي للسيرفر (مثل ...\MSSQL\Backup).</summary>
    public async Task<string?> GetDefaultFolderAsync()
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS NVARCHAR(400))", conn);
        return await cmd.ExecuteScalarAsync() as string;
    }

    /// <returns>المسار الكامل لملف النسخة على السيرفر.</returns>
    /// <param name="tag">لاحقة في اسم الملف (مثل before-restore) تميّز نسخة الأمان.</param>
    public async Task<string> BackupAsync(string databaseName, string folder, string? tag = null)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(databaseName, "^[A-Za-z_][A-Za-z0-9_]{0,100}$"))
            throw new ArgumentException("اسم قاعدة البيانات غير صالح", nameof(databaseName));
        folder = folder.Trim();
        // فاصل المسار حسب نظام السيرفر (Windows عادةً، Linux في Docker)
        var sep = folder.Contains('/') && !folder.Contains('\\') ? "/" : "\\";
        // بالأجزاء من الثانية ولاحقة اختيارية: نسختان متتاليتان لا تستبدل إحداهما الأخرى
        var path = folder.TrimEnd('\\', '/') + sep + $"{databaseName}_{DateTime.Now:yyyyMMdd_HHmmss_fff}{(tag is null ? "" : "_" + tag)}.bak";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            $"BACKUP DATABASE [{databaseName}] TO DISK = @path WITH COPY_ONLY, INIT, CHECKSUM, NAME = @name", conn) { CommandTimeout = 0 };
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@name", $"ERP backup {databaseName}");
        await cmd.ExecuteNonQueryAsync();
        return path;
    }

    /// <summary>
    /// استرداد قاعدة من ملف نسخة (على جهاز السيرفر): يتحقق من الملف ومن أنه نسخة القاعدة نفسها، ويأخذ أولًا
    /// «نسخة أمان» من الوضع الحالي في المجلد نفسه، ثم يستبدل القاعدة (ويقطع اتصالات المستخدمين الآخرين).
    /// الملفات تُعاد إلى مواقع القاعدة الحالية، فتعمل نسخة من جهاز آخر أيضًا.
    /// </summary>
    /// <returns>مسار نسخة الأمان التي أُخذت قبل الاسترداد.</returns>
    public async Task<string> RestoreAsync(string databaseName, string backupFile, string safetyFolder)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(databaseName, "^[A-Za-z_][A-Za-z0-9_]{0,100}$"))
            throw new ArgumentException("اسم قاعدة البيانات غير صالح", nameof(databaseName));
        var master = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master", Pooling = false }.ConnectionString;
        await using var conn = new SqlConnection(master);
        await conn.OpenAsync();

        async Task<List<object?[]>> Rows(string sql)
        {
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
            cmd.Parameters.AddWithValue("@path", backupFile);
            cmd.Parameters.AddWithValue("@db", databaseName);
            var list = new List<object?[]>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                var row = new object?[rd.FieldCount];
                rd.GetValues(row!);
                list.Add(row);
            }
            return list;
        }

        // 1) الملف سليم ويخص هذه القاعدة
        await using (var h = new SqlCommand("RESTORE HEADERONLY FROM DISK = @path", conn) { CommandTimeout = 0 })
        {
            h.Parameters.AddWithValue("@path", backupFile);
            await using var rd = await h.ExecuteReaderAsync();
            if (!await rd.ReadAsync()) throw new InvalidOperationException("الملف لا يحوي نسخة احتياطية");
            var source = rd["DatabaseName"] as string;
            if (!string.Equals(source, databaseName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"هذه نسخة قاعدة «{source}» وليست «{databaseName}». اختر نسخة القاعدة نفسها.");
        }
        await using (var verify = new SqlCommand("RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM", conn) { CommandTimeout = 0 })
        {
            verify.Parameters.AddWithValue("@path", backupFile);
            await verify.ExecuteNonQueryAsync();
        }

        // 2) الملفات إلى مواقع القاعدة الحالية (البيانات إلى ملفات البيانات، والسجل إلى ملف السجل)
        var files = await Rows("RESTORE FILELISTONLY FROM DISK = @path");
        var current = await Rows("SELECT type_desc, physical_name FROM sys.master_files WHERE database_id = DB_ID(@db) ORDER BY file_id");
        var currentData = current.Where(c => (string)c[0]! == "ROWS").Select(c => (string)c[1]!).ToList();
        var currentLog = current.Where(c => (string)c[0]! == "LOG").Select(c => (string)c[1]!).ToList();
        var moves = new List<string>();
        var moveParams = new List<SqlParameter>();
        int dataIndex = 0, logIndex = 0;
        for (var i = 0; i < files.Count; i++)
        {
            var isLog = (string)files[i][2]! == "L";
            var pool = isLog ? currentLog : currentData;
            var index = isLog ? logIndex++ : dataIndex++;
            if (pool.Count == 0) continue;
            var target = index < pool.Count ? pool[index]
                : System.IO.Path.ChangeExtension(pool[0], null) + $"_{index}" + System.IO.Path.GetExtension(pool[0]);
            moves.Add($"MOVE @l{i} TO @p{i}");
            moveParams.Add(new SqlParameter($"@l{i}", (string)files[i][0]!));
            moveParams.Add(new SqlParameter($"@p{i}", target));
        }

        // 3) نسخة أمان من الوضع الحالي، ثم الاسترداد
        var safety = await BackupAsync(databaseName, safetyFolder, "before-restore");
        if (string.Equals(safety, backupFile, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("تعذّر أخذ نسخة أمان باسم مختلف عن ملف الاسترداد");
        SqlConnection.ClearAllPools();
        await using (var single = new SqlCommand($"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE", conn) { CommandTimeout = 0 })
            await single.ExecuteNonQueryAsync();
        try
        {
            await using var restore = new SqlCommand(
                $"RESTORE DATABASE [{databaseName}] FROM DISK = @path WITH REPLACE, RECOVERY{(moves.Count > 0 ? ", " + string.Join(", ", moves) : "")}", conn) { CommandTimeout = 0 };
            restore.Parameters.AddWithValue("@path", backupFile);
            restore.Parameters.AddRange(moveParams.ToArray());
            await restore.ExecuteNonQueryAsync();
        }
        finally
        {
            await using var multi = new SqlCommand($"IF DB_ID(@db) IS NOT NULL ALTER DATABASE [{databaseName}] SET MULTI_USER", conn);
            multi.Parameters.AddWithValue("@db", databaseName);
            await multi.ExecuteNonQueryAsync();
        }
        SqlConnection.ClearAllPools();
        return safety;
    }

    /// <summary>آخر النسخ المسجّلة في msdb لقواعد محددة (من أي أداة، لا من البرنامج فقط).</summary>
    public async Task<List<BackupHistoryRow>> GetHistoryAsync(IEnumerable<string> databaseNames, int take = 20)
    {
        var names = databaseNames.Distinct().ToList();
        var rows = new List<BackupHistoryRow>();
        if (names.Count == 0) return rows;
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        var ps = string.Join(",", names.Select((_, i) => "@d" + i));
        await using var cmd = new SqlCommand($"""
            SELECT TOP (@take) b.database_name, b.backup_finish_date, CAST(b.backup_size / 1048576.0 AS DECIMAL(18,2)), m.physical_device_name
            FROM msdb.dbo.backupset b JOIN msdb.dbo.backupmediafamily m ON m.media_set_id = b.media_set_id
            WHERE b.type = 'D' AND b.database_name IN ({ps})
            ORDER BY b.backup_finish_date DESC
            """, conn);
        cmd.Parameters.AddWithValue("@take", take);
        for (var i = 0; i < names.Count; i++) cmd.Parameters.AddWithValue("@d" + i, names[i]);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            rows.Add(new BackupHistoryRow { DatabaseName = rd.GetString(0), FinishedAt = rd.GetDateTime(1), SizeMb = rd.GetDecimal(2), FilePath = rd.GetString(3) });
        return rows;
    }
}
