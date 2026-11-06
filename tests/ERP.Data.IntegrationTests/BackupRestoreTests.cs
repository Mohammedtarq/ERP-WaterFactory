using ERP.Data.Services;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>استرداد نسخة احتياطية على قاعدة تجريبية مستقلة: نسخة أمان أولًا، ورفض نسخة قاعدة أخرى دون مساس.</summary>
public class BackupRestoreTests
{
    private static string Master => Environment.GetEnvironmentVariable("ERP_TEST_MASTER_CONNECTION")
        ?? throw new InvalidOperationException("ERP_TEST_MASTER_CONNECTION غير معيّن");

    private static async Task<object?> ScalarAsync(string cs, string sql)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    [Fact]
    public async Task Restore_takes_a_safety_backup_then_returns_the_database_to_the_backup_point()
    {
        var name = $"ERP_RestoreT_{Guid.NewGuid():N}"[..24];
        var other = name + "_O";
        var db = new SqlConnectionStringBuilder(Master) { InitialCatalog = name }.ConnectionString;
        await ScalarAsync(Master, $"CREATE DATABASE [{name}]; CREATE DATABASE [{other}]");
        try
        {
            await ScalarAsync(db, "CREATE TABLE T (Id INT); INSERT INTO T VALUES (1), (2)");
            var svc = new BackupService(db);
            var folder = await svc.GetDefaultFolderAsync() ?? "/var/opt/mssql/data";
            var backup = await svc.BackupAsync(name, folder);
            var otherBackup = await new BackupService(new SqlConnectionStringBuilder(Master) { InitialCatalog = other }.ConnectionString).BackupAsync(other, folder);

            await ScalarAsync(db, "INSERT INTO T VALUES (3), (4), (5)");
            Assert.Equal(5, await ScalarAsync(db, "SELECT COUNT(*) FROM T"));

            // نسخة قاعدة أخرى: رفض واضح، والقاعدة كما هي
            var wrong = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RestoreAsync(name, otherBackup, folder));
            Assert.Contains("ليست", wrong.Message);
            Assert.Equal(5, await ScalarAsync(db, "SELECT COUNT(*) FROM T"));

            // الاسترداد: نسخة أمان من الوضع الحالي، ثم العودة لنقطة النسخة
            var safety = await svc.RestoreAsync(name, backup, folder);
            Assert.NotEqual(backup, safety);
            Assert.Equal(2, await ScalarAsync(db, "SELECT COUNT(*) FROM T"));
            Assert.Equal("MULTI_USER", await ScalarAsync(Master, $"SELECT user_access_desc FROM sys.databases WHERE name = '{name}'"));

            // ونسخة الأمان تعيد ما كان قبل الاسترداد
            await svc.RestoreAsync(name, safety, folder);
            Assert.Equal(5, await ScalarAsync(db, "SELECT COUNT(*) FROM T"));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await ScalarAsync(Master, $"""
                IF DB_ID('{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END
                IF DB_ID('{other}') IS NOT NULL BEGIN ALTER DATABASE [{other}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{other}]; END
                """);
        }
    }
}
