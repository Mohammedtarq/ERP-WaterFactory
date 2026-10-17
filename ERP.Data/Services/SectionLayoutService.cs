using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>توزيع الأقسام كما تراه جلسة مستخدم: المنقول إلى أي وحدة، والمخفي عن دوره.</summary>
public sealed record SectionLayout(IReadOnlyDictionary<string, string> Moves, IReadOnlySet<string> Hidden)
{
    public static readonly SectionLayout Empty = new(new Dictionary<string, string>(), new HashSet<string>());
}

/// <summary>
/// نقل قسم من وحدة إلى أخرى، وإخفاء أقسام عن دور. يُعدَّل من الإعدادات فقط (صلاحية إعدادات النظام)،
/// وكل تعديل في سجل الحركات، ويسري عند الدخول التالي.
/// </summary>
public class SectionLayoutService
{
    private readonly ProjectDbContext _db;
    public SectionLayoutService(ProjectDbContext db) => _db = db;

    public async Task<SectionLayout> ForRoleAsync(int roleId) => new(
        await _db.SectionPlacements.AsNoTracking().ToDictionaryAsync(p => p.SectionKey, p => p.TargetModule),
        (await _db.RoleHiddenSections.AsNoTracking().Where(h => h.RoleId == roleId).Select(h => h.SectionKey).ToListAsync()).ToHashSet());

    public Task<Dictionary<string, string>> PlacementsAsync() =>
        _db.SectionPlacements.AsNoTracking().ToDictionaryAsync(p => p.SectionKey, p => p.TargetModule);

    public async Task<HashSet<string>> HiddenAsync(int roleId) =>
        (await _db.RoleHiddenSections.AsNoTracking().Where(h => h.RoleId == roleId).Select(h => h.SectionKey).ToListAsync()).ToHashSet();

    private async Task<FinanceOperationResult?> RequireSettingsAsync(int userId) =>
        await _db.Users.AnyAsync(u => u.Id == userId && u.IsActive && u.Role.Permissions.Any(p => p.ModuleCode == ModuleCode.SystemSettings && p.CanEdit))
            ? null : FinanceOperationResult.Fail("توزيع الأقسام بقرار الإدارة فقط (صلاحية تعديل إعدادات النظام)");

    /// <summary>نقل القسم إلى وحدة؛ وحدته الأصلية (أو NULL) تعيده لمكانه.</summary>
    public async Task<FinanceOperationResult> MoveAsync(string sectionKey, string homeModule, string? targetModule, int userId, string sectionTitle)
    {
        if (await RequireSettingsAsync(userId) is { } denied) return denied;
        if (string.IsNullOrWhiteSpace(sectionKey)) return FinanceOperationResult.Fail("اختر القسم");
        if (targetModule == ModuleCode.SystemSettings || targetModule == ModuleCode.Dashboard)
            return FinanceOperationResult.Fail("لا تُنقل الأقسام إلى الإعدادات أو لوحة المعلومات");
        var existing = await _db.SectionPlacements.FindAsync(sectionKey);
        if (targetModule is null || targetModule == homeModule)
        {
            if (existing is null) return FinanceOperationResult.Ok();
            _db.SectionPlacements.Remove(existing);
        }
        else if (existing is null) _db.SectionPlacements.Add(new SectionPlacement { SectionKey = sectionKey, TargetModule = targetModule, ChangedByUserId = userId });
        else { existing.TargetModule = targetModule; existing.ChangedByUserId = userId; existing.ChangedAt = DateTime.UtcNow; }
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Edit", "SectionPlacements", sectionKey,
            targetModule is null || targetModule == homeModule ? $"إعادة «{sectionTitle}» إلى وحدته" : $"نقل «{sectionTitle}» من {homeModule} إلى {targetModule}");
        return FinanceOperationResult.Ok();
    }

    /// <summary>إظهار أو إخفاء قسم عن دور.</summary>
    public async Task<FinanceOperationResult> SetHiddenAsync(int roleId, string sectionKey, bool hidden, int userId, string sectionTitle)
    {
        if (await RequireSettingsAsync(userId) is { } denied) return denied;
        var role = await _db.Roles.FindAsync(roleId);
        if (role is null) return FinanceOperationResult.Fail("الدور غير موجود");
        var row = await _db.RoleHiddenSections.FindAsync(roleId, sectionKey);
        if (hidden == (row is not null)) return FinanceOperationResult.Ok();
        if (hidden) _db.RoleHiddenSections.Add(new RoleHiddenSection { RoleId = roleId, SectionKey = sectionKey });
        else _db.RoleHiddenSections.Remove(row!);
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Edit", "RoleHiddenSections", sectionKey,
            $"{(hidden ? "إخفاء" : "إظهار")} «{sectionTitle}» لدور {role.Name}");
        return FinanceOperationResult.Ok();
    }
}
