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
    public async Task<string> BackupAsync(string databaseName, string folder)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(databaseName, "^[A-Za-z_][A-Za-z0-9_]{0,100}$"))
            throw new ArgumentException("اسم قاعدة البيانات غير صالح", nameof(databaseName));
        folder = folder.Trim();
        // فاصل المسار حسب نظام السيرفر (Windows عادةً، Linux في Docker)
        var sep = folder.Contains('/') && !folder.Contains('\\') ? "/" : "\\";
        var path = folder.TrimEnd('\\', '/') + sep + $"{databaseName}_{DateTime.Now:yyyyMMdd_HHmmss}.bak";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            $"BACKUP DATABASE [{databaseName}] TO DISK = @path WITH COPY_ONLY, INIT, CHECKSUM, NAME = @name", conn) { CommandTimeout = 0 };
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@name", $"ERP backup {databaseName}");
        await cmd.ExecuteNonQueryAsync();
        return path;
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
