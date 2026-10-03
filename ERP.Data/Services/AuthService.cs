using ERP.Data.ControlDb;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Security;
using ERP.Data.Setup;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

public record ProjectOption(int ProjectId, string ProjectName, string DatabaseName, string ServerAddress, int LocalUserId);

public record LoginResult(bool Success, string? ErrorMessage, int GlobalUserId, string FullName, IReadOnlyList<ProjectOption> Projects)
{
    public string Username { get; init; } = "";
}

/// <summary>صلاحيات المستخدم داخل مشروع واحد، مفهرسة بكود الوحدة.</summary>
public class UserPermissions
{
    private readonly Dictionary<string, RolePermission> _byModule;

    public UserPermissions(IEnumerable<RolePermission> permissions)
    {
        _byModule = permissions.ToDictionary(p => p.ModuleCode, StringComparer.OrdinalIgnoreCase);
    }

    public bool CanView(string module) => _byModule.TryGetValue(module, out var p) && p.CanView;
    public bool CanAdd(string module) => _byModule.TryGetValue(module, out var p) && p.CanAdd;
    public bool CanEdit(string module) => _byModule.TryGetValue(module, out var p) && p.CanEdit;
    public bool CanDelete(string module) => _byModule.TryGetValue(module, out var p) && p.CanDelete;
    public bool CanPost(string module) => _byModule.TryGetValue(module, out var p) && p.CanPost;
    /// <summary>صلاحية خاصة (SpecialPermission) ممنوحة للدور.</summary>
    public bool Has(string specialCode) => CanView(specialCode);
}

public record ProjectSessionInfo(string ConnectionString, int LocalUserId, string Username, string RoleName, UserPermissions Permissions);

/// <summary>
/// تدفق الدخول الموحّد: التحقق من المستخدم في قاعدة التحكم ← قائمة مشاريعه ←
/// فتح قاعدة المشروع المختار وتحميل صلاحيات مستخدمه المحلي.
/// </summary>
public class AuthService
{
    private readonly string _controlConnectionString;

    public AuthService(string controlConnectionString)
    {
        _controlConnectionString = controlConnectionString;
    }

    public string ControlConnectionString => _controlConnectionString;

    private ControlDbContext NewControlDb() =>
        new(new DbContextOptionsBuilder<ControlDbContext>().UseSqlServer(_controlConnectionString).Options);

    /// <summary>عدد المحاولات الخاطئة المتتالية قبل القفل المؤقت، ومدة القفل.</summary>
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public async Task<LoginResult> LoginAsync(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return new(false, "أدخل اسم المستخدم وكلمة المرور", 0, "", Array.Empty<ProjectOption>());

        await DatabaseInstaller.EnsureControlUpgradesAsync(_controlConnectionString);
        await using var db = NewControlDb();
        var user = await db.GlobalUsers.FirstOrDefaultAsync(u => u.Username == username.Trim());

        if (user?.LockedUntilUtc is DateTime until && until > DateTime.UtcNow)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling((until - DateTime.UtcNow).TotalMinutes));
            return new(false, $"الحساب مقفل مؤقتًا بعد {MaxFailedLogins} محاولات دخول خاطئة. حاول بعد {minutes} دقيقة، أو اطلب من المدير فتحه بتعيين كلمة مرور جديدة.",
                       0, "", Array.Empty<ProjectOption>());
        }

        // نفس الرسالة للحالتين حتى لا نكشف وجود اسم المستخدم
        if (user is null || !PasswordHasher.Verify(password, user.PasswordHash))
        {
            if (user is not null)
            {
                user.FailedLoginCount++;
                if (user.FailedLoginCount >= MaxFailedLogins)
                {
                    user.LockedUntilUtc = DateTime.UtcNow.Add(LockoutDuration);
                    user.FailedLoginCount = 0;
                }
                await db.SaveChangesAsync();
                if (user.LockedUntilUtc > DateTime.UtcNow)
                    return new(false, $"تكررت كلمة المرور الخاطئة {MaxFailedLogins} مرات، فقُفل الحساب {LockoutDuration.TotalMinutes:0} دقيقة.", 0, "", Array.Empty<ProjectOption>());
            }
            return new(false, "اسم المستخدم أو كلمة المرور غير صحيحة", 0, "", Array.Empty<ProjectOption>());
        }
        if (user.FailedLoginCount != 0 || user.LockedUntilUtc is not null)
        {
            user.FailedLoginCount = 0;
            user.LockedUntilUtc = null;
            await db.SaveChangesAsync();
        }
        if (!user.IsActive)
            return new(false, "هذا الحساب موقوف. راجع مدير النظام", 0, "", Array.Empty<ProjectOption>());

        var projects = await db.UserProjectAccesses.AsNoTracking()
            .Where(a => a.GlobalUserId == user.Id && a.Project.IsActive)
            .OrderBy(a => a.Project.ProjectName)
            .Select(a => new ProjectOption(a.ProjectId, a.Project.ProjectName, a.Project.DatabaseName,
                                           a.Project.ServerAddress, a.LocalUserIdInProject))
            .ToListAsync();

        if (projects.Count == 0)
            return new(false, "لا يوجد أي مشروع مرتبط بحسابك بعد", user.Id, user.FullName, projects);

        return new(true, null, user.Id, user.FullName, projects) { Username = user.Username };
    }

    /// <summary>سلسلة اتصال المشروع = نفس بيانات اعتماد قاعدة التحكم، بسيرفر وقاعدة المشروع.</summary>
    public string BuildProjectConnectionString(ProjectOption project)
    {
        var b = new SqlConnectionStringBuilder(_controlConnectionString)
        {
            InitialCatalog = project.DatabaseName
        };
        // "localhost" في سجل المشروع يعني: نفس السيرفر المستخدم لقاعدة التحكم
        if (!string.IsNullOrWhiteSpace(project.ServerAddress) &&
            !project.ServerAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            b.DataSource = project.ServerAddress;
        return b.ConnectionString;
    }

    public async Task<(ProjectSessionInfo? session, string? error)> OpenProjectAsync(ProjectOption project)
    {
        var cs = BuildProjectConnectionString(project);
        try
        {
            // ترقية تلقائية: أي سكربت جديد أو معدّل يصل للقاعدة، وتُكمل الإعدادات الناقصة (قواعد ربط، أدوار)
            await DatabaseInstaller.UpgradeProjectAsync(cs);
            await using (var seedDb = new ProjectDbContext(new DbContextOptionsBuilder<ProjectDbContext>().UseSqlServer(cs).Options))
                await DefaultConfiguration.SeedAsync(seedDb, includeWarehouses: false);

            await using var db = new ProjectDbContext(new DbContextOptionsBuilder<ProjectDbContext>().UseSqlServer(cs).Options);
            var user = await db.Users.AsNoTracking().Include(u => u.Role).ThenInclude(r => r.Permissions)
                .FirstOrDefaultAsync(u => u.Id == project.LocalUserId);

            if (user is null)
                return (null, "المستخدم المحلي المرتبط بحسابك غير موجود في قاعدة هذا المشروع");
            if (!user.IsActive)
                return (null, "حسابك موقوف داخل هذا المشروع");

            return (new ProjectSessionInfo(cs, user.Id, user.Username, user.Role.Name,
                                           new UserPermissions(user.Role.Permissions)), null);
        }
        catch (SqlException ex)
        {
            return (null, $"تعذّر فتح أو تحديث قاعدة بيانات المشروع: {ex.Message}");
        }
    }
}
