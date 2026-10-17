using ERP.Data.ControlDb;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>نتيجة حذف مشروع: مسار النسخة الاحتياطية المأخوذة قبل الحذف (إن وُجدت القاعدة).</summary>
public record ProjectDeletionResult(bool Success, string? ErrorMessage, string? BackupFile = null, bool DatabaseDropped = false);

/// <summary>
/// حذف مشروع من شاشة «اختيار المشروع» (ملاحظة التجربة 16 — قرار المدير: حذف نهائي للقاعدة):
/// للمدير فقط (صلاحية تعديل إعدادات النظام داخل ذلك المشروع)، بكتابة اسم المشروع للتأكيد،
/// وبعد نسخة احتياطية تلقائية؛ ثم تُحذف القاعدة نهائيًا ويُزال المشروع وصلاحيات الدخول إليه من قاعدة التحكم.
/// إن كان سجل آخر يشير للقاعدة نفسها (مشروع مكرر) يُزال السجل وحده وتبقى القاعدة.
/// </summary>
public class ProjectDeletionService
{
    private readonly AuthService _auth;
    public ProjectDeletionService(AuthService auth) => _auth = auth;

    public async Task<ProjectDeletionResult> DeleteAsync(int globalUserId, ProjectOption project, string typedName, string? backupFolder = null)
    {
        if (!string.Equals(typedName?.Trim(), project.ProjectName.Trim(), StringComparison.Ordinal))
            return new(false, $"اكتب اسم المشروع كما هو للتأكيد: «{project.ProjectName}»");
        if (!System.Text.RegularExpressions.Regex.IsMatch(project.DatabaseName, "^[A-Za-z_][A-Za-z0-9_]{0,100}$"))
            return new(false, "اسم قاعدة بيانات المشروع غير صالح");

        await using var control = new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>().UseSqlServer(_auth.ControlConnectionString).Options);
        var record = await control.Projects.FirstOrDefaultAsync(p => p.Id == project.ProjectId);
        if (record is null) return new(false, "المشروع غير موجود");
        var myProjects = await control.UserProjectAccesses.AsNoTracking().Where(a => a.GlobalUserId == globalUserId && a.Project.IsActive)
            .Select(a => new ProjectOption(a.ProjectId, a.Project.ProjectName, a.Project.DatabaseName, a.Project.ServerAddress, a.LocalUserIdInProject))
            .ToListAsync();
        if (myProjects.All(p => p.ProjectId != project.ProjectId)) return new(false, "لا صلاحية لك على هذا المشروع");
        if (await control.Projects.CountAsync(p => p.IsActive) <= 1) return new(false, "لا يُحذف المشروع الوحيد — أنشئ مشروعًا آخر أولًا");

        var cs = _auth.BuildProjectConnectionString(project);
        var exists = await DatabaseExistsAsync(cs, project.DatabaseName);
        // المدير فقط: داخل هذا المشروع إن وُجدت قاعدته، وإلا في مشروع آخر من مشاريعه
        var adminSomewhere = exists ? await IsAdminAsync(cs, project.LocalUserId)
            : await AnyAsync(myProjects.Where(p => p.ProjectId != project.ProjectId), p => IsAdminAsync(_auth.BuildProjectConnectionString(p), p.LocalUserId));
        if (!adminSomewhere) return new(false, "حذف المشاريع للمدير فقط (صلاحية تعديل إعدادات النظام)");

        // سجل آخر يشير للقاعدة نفسها: يُزال هذا السجل فقط
        var shared = await control.Projects.AnyAsync(p => p.Id != record.Id && p.DatabaseName == record.DatabaseName && p.ServerAddress == record.ServerAddress);
        string? backup = null;
        var dropped = false;
        if (exists && !shared)
        {
            try
            {
                var backups = new BackupService(cs);
                var folder = string.IsNullOrWhiteSpace(backupFolder) ? await backups.GetDefaultFolderAsync() : backupFolder;
                if (string.IsNullOrWhiteSpace(folder)) return new(false, "تعذّر تحديد مجلد النسخ الاحتياطي على السيرفر — لم يُحذف شيء");
                backup = await backups.BackupAsync(project.DatabaseName, folder, "before-delete");
            }
            catch (SqlException ex)
            {
                return new(false, $"تعذّرت النسخة الاحتياطية قبل الحذف، فلم يُحذف شيء: {ex.Message}");
            }
            await DropAsync(cs, project.DatabaseName);
            dropped = true;
        }

        control.UserProjectAccesses.RemoveRange(control.UserProjectAccesses.Where(a => a.ProjectId == record.Id));
        control.Projects.Remove(record);
        await control.SaveChangesAsync();
        return new(true, null, backup, dropped);
    }

    private static async Task<bool> AnyAsync<T>(IEnumerable<T> items, Func<T, Task<bool>> predicate)
    {
        foreach (var item in items)
            if (await predicate(item)) return true;
        return false;
    }

    private static string Master(string cs) => new SqlConnectionStringBuilder(cs) { InitialCatalog = "master", Pooling = false }.ConnectionString;

    private static async Task<bool> DatabaseExistsAsync(string cs, string databaseName)
    {
        await using var conn = new SqlConnection(Master(cs));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT DB_ID(@db)", conn);
        cmd.Parameters.AddWithValue("@db", databaseName);
        return await cmd.ExecuteScalarAsync() is not (null or DBNull);
    }

    private static async Task<bool> IsAdminAsync(string cs, int localUserId)
    {
        try
        {
            await using var db = new ProjectDbContext(new DbContextOptionsBuilder<ProjectDbContext>().UseSqlServer(cs).Options);
            return await db.Users.AnyAsync(u => u.Id == localUserId && u.IsActive
                                                && u.Role.Permissions.Any(p => p.ModuleCode == ModuleCode.SystemSettings && p.CanEdit));
        }
        catch (SqlException)
        {
            return false;
        }
    }

    private static async Task DropAsync(string cs, string databaseName)
    {
        SqlConnection.ClearAllPools();
        await using var conn = new SqlConnection(Master(cs));
        await conn.OpenAsync();
        await using (var single = new SqlCommand($"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE", conn) { CommandTimeout = 0 })
            await single.ExecuteNonQueryAsync();
        await using (var drop = new SqlCommand($"DROP DATABASE [{databaseName}]", conn) { CommandTimeout = 0 })
            await drop.ExecuteNonQueryAsync();
    }
}
