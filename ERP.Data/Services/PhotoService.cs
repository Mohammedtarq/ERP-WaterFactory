using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>
/// صور الموظفين والمستخدمين (45_photos.sql): PNG أو JPG حتى 2 ميغابايت (الواجهة تصغّرها قبل الحفظ إلى 256×256).
/// صورة الموظف يضعها مسؤول الموارد البشرية من ملفه، وصورة اليوزر يغيّرها صاحبها أو المدير؛ بدونها يأخذ صورة موظفه.
/// كل تغيير يُسجَّل في سجل التدقيق.
/// </summary>
public class PhotoService
{
    public const int MaxBytes = 2 * 1024 * 1024;
    private readonly ProjectDbContext _db;
    public PhotoService(ProjectDbContext db) => _db = db;

    /// <summary>سبب رفض الصورة، أو null إن كانت مقبولة.</summary>
    public static string? Check(byte[]? photo) =>
        photo is not { Length: > 0 } ? null
        : photo.Length > MaxBytes ? "حجم الصورة أكبر من 2 ميغابايت — صغّر الصورة"
        : !CompanyProfileService.IsImage(photo) ? "الصورة يجب أن تكون PNG أو JPG"
        : null;

    /// <summary>أول حرفين من الاسم (للدائرة الملوّنة حين لا توجد صورة): «سارة المحاسبة» ← «س م».</summary>
    public static string Initials(string? name)
    {
        var parts = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => "؟",
            1 => parts[0][..1],
            _ => $"{parts[0][..1]} {parts[1][..1]}"
        };
    }

    public async Task<byte[]?> GetEmployeeAsync(int employeeId) =>
        await _db.EmployeePhotos.AsNoTracking().Where(p => p.EmployeeId == employeeId).Select(p => p.Photo).FirstOrDefaultAsync();

    public Task<Dictionary<int, byte[]>> AllEmployeesAsync() =>
        _db.EmployeePhotos.AsNoTracking().ToDictionaryAsync(p => p.EmployeeId, p => p.Photo);

    public Task<Dictionary<int, byte[]>> AllUsersAsync() =>
        _db.UserPhotos.AsNoTracking().ToDictionaryAsync(p => p.UserId, p => p.Photo);

    public Task<bool> HasOwnUserPhotoAsync(int userId) => _db.UserPhotos.AnyAsync(p => p.UserId == userId);

    /// <summary>صورة اليوزر الظاهرة: صورته الخاصة، وإلا صورة الموظف المربوط به.</summary>
    public async Task<byte[]?> GetUserDisplayAsync(int userId)
    {
        var own = await _db.UserPhotos.AsNoTracking().Where(p => p.UserId == userId).Select(p => p.Photo).FirstOrDefaultAsync();
        if (own is not null) return own;
        var employeeId = await _db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.EmployeeId).FirstOrDefaultAsync();
        return employeeId is int e ? await GetEmployeeAsync(e) : null;
    }

    /// <summary>وضع صورة الموظف أو إزالتها (photo = null).</summary>
    public async Task<FinanceOperationResult> SetEmployeeAsync(int employeeId, byte[]? photo, int userId)
    {
        if (Check(photo) is { } error) return FinanceOperationResult.Fail(error);
        var name = await _db.Employees.Where(e => e.Id == employeeId).Select(e => e.FullName).FirstOrDefaultAsync();
        if (name is null) return FinanceOperationResult.Fail("الموظف غير موجود");
        var row = await _db.EmployeePhotos.FirstOrDefaultAsync(p => p.EmployeeId == employeeId);
        if (!Apply(_db.EmployeePhotos, row, photo, () => new EmployeePhoto { EmployeeId = employeeId }, (r, b) => (r.Photo, r.UpdatedAt) = (b, DateTime.UtcNow)))
            return FinanceOperationResult.Ok();
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Update", "EmployeePhotos", employeeId,
            photo is null ? $"إزالة صورة الموظف {name}" : $"تغيير صورة الموظف {name}");
        return FinanceOperationResult.Ok();
    }

    /// <summary>وضع صورة اليوزر الخاصة أو إزالتها (فيعود لصورة موظفه).</summary>
    public async Task<FinanceOperationResult> SetUserAsync(int targetUserId, byte[]? photo, int byUserId)
    {
        if (Check(photo) is { } error) return FinanceOperationResult.Fail(error);
        var username = await _db.Users.Where(u => u.Id == targetUserId).Select(u => u.Username).FirstOrDefaultAsync();
        if (username is null) return FinanceOperationResult.Fail("المستخدم غير موجود");
        var row = await _db.UserPhotos.FirstOrDefaultAsync(p => p.UserId == targetUserId);
        if (!Apply(_db.UserPhotos, row, photo, () => new UserPhoto { UserId = targetUserId }, (r, b) => (r.Photo, r.UpdatedAt) = (b, DateTime.UtcNow)))
            return FinanceOperationResult.Ok();
        await _db.SaveChangesAsync();
        var whose = targetUserId == byUserId ? "صورته" : $"صورة المستخدم {username}";
        await new AuditService(_db).LogAsync(byUserId, "Update", "UserPhotos", targetUserId,
            photo is null ? $"إزالة {whose}" : $"تغيير {whose}");
        return FinanceOperationResult.Ok();
    }

    /// <summary>يضيف أو يحدّث أو يحذف السطر؛ false إن لم يتغير شيء (إزالة صورة غير موجودة أو نفس الصورة).</summary>
    private static bool Apply<T>(DbSet<T> set, T? row, byte[]? photo, Func<T> create, Action<T, byte[]> write) where T : class
    {
        if (photo is null)
        {
            if (row is null) return false;
            set.Remove(row);
            return true;
        }
        if (row is null) set.Add(row = create());
        else if (row is EmployeePhoto { Photo: var a } && a.AsSpan().SequenceEqual(photo)) return false;
        else if (row is UserPhoto { Photo: var b } && b.AsSpan().SequenceEqual(photo)) return false;
        write(row, photo);
        return true;
    }
}
