using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

public record LateTierInput(int FromMinutes, int? ToMinutes, LateDeductionKind Kind, decimal Value);

public record LateSettings(bool Enabled, DateTime? From, IReadOnlyList<LateDeductionTier> Tiers)
{
    /// <summary>هل يسري خصم التأخير على هذا الشهر؟ (مفعّل وفي/بعد شهر السريان)</summary>
    public bool AppliesTo(int month, int year) =>
        Enabled && (From is null || new DateTime(year, month, 1) >= new DateTime(From.Value.Year, From.Value.Month, 1));
}

/// <summary>نتيجة شهر موظف: الدقائق الصافية بعد كل المسامحات وخصمها (بعملة الراتب).</summary>
public record LateMonthResult(int NetMinutes, int DaysCharged, decimal Deduction);

/// <summary>
/// خصم التأخير بالشرائح (يُضبط مرة واحدة ويسري على كل الأشهر من شهر التفعيل):
/// - لكل يوم: دقائق التأخير (من بداية الشفت بعد تجاوز السماح) − المسامحة اليومية − ما يُستهلك من رصيد المسامحة الشهري (بترتيب الأيام).
/// - الشريحة التي تقع فيها دقائق اليوم الصافية تحدد الخصم: جزء من أجر اليوم (الأساسي ÷ 30) أو مبلغ ثابت بالدينار، ولا يتجاوز أجر يوم واحد.
/// - المعفى من خصم التأخير لا يُخصم، والسماح الخاص يُعفي الأيام التي لا تتجاوزه.
/// المسامحة والإعفاء يحتاجان صلاحية «مسامحة التأخير» ولا تُقبلان بعد اعتماد رواتب الشهر.
/// </summary>
public class LateDeductionService
{
    private readonly ProjectDbContext _db;
    public LateDeductionService(ProjectDbContext db) => _db = db;

    // ============================ الحساب (دالة نقية للاختبار) ============================

    public static LateMonthResult ComputeMonth(
        IEnumerable<(DateTime Date, int Late, int Forgiven)> days, int monthlyForgiveMinutes,
        IReadOnlyList<LateDeductionTier> tiers, decimal dailyWage, Func<decimal, decimal> fx, bool exempt, int? graceOverride)
    {
        var pool = Math.Max(0, monthlyForgiveMinutes);
        var net = 0; var charged = 0; decimal total = 0;
        foreach (var d in days.Where(d => d.Late > 0).OrderBy(d => d.Date))
        {
            var minutes = Math.Max(0, d.Late - Math.Max(0, d.Forgiven));
            var take = Math.Min(pool, minutes);
            minutes -= take; pool -= take;
            if (minutes <= 0) continue;
            net += minutes;
            if (exempt || (graceOverride is int g && minutes <= g)) continue;
            var tier = tiers.OrderBy(t => t.FromMinutes).FirstOrDefault(t => minutes >= t.FromMinutes && (t.ToMinutes is null || minutes <= t.ToMinutes));
            if (tier is null || tier.Kind == LateDeductionKind.None) continue;
            var amount = tier.Kind == LateDeductionKind.Fraction ? dailyWage * tier.Value : fx(tier.Value);
            if (dailyWage > 0) amount = Math.Min(amount, dailyWage);
            if (amount <= 0) continue;
            total += amount; charged++;
        }
        return new LateMonthResult(net, charged, Math.Round(total, 2));
    }

    // ============================ الإعدادات ============================

    public async Task<LateSettings> GetSettingsAsync()
    {
        var policy = await _db.HrPolicies.AsNoTracking().FirstOrDefaultAsync();
        var tiers = await _db.LateDeductionTiers.AsNoTracking().OrderBy(t => t.FromMinutes).ToListAsync();
        return new LateSettings(policy?.LateDeductionEnabled ?? false, policy?.LateDeductionFrom, tiers);
    }

    public async Task<FinanceOperationResult> SaveSettingsAsync(bool enabled, DateTime? from, IReadOnlyList<LateTierInput> tiers, int userId)
    {
        var ordered = tiers.OrderBy(t => t.FromMinutes).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var t = ordered[i];
            if (t.FromMinutes < 1) return FinanceOperationResult.Fail("بداية الشريحة لا تقل عن دقيقة واحدة");
            if (t.ToMinutes is int to && to < t.FromMinutes) return FinanceOperationResult.Fail($"نهاية الشريحة {i + 1} أصغر من بدايتها");
            if (t.Value < 0) return FinanceOperationResult.Fail("قيمة الخصم لا تكون سالبة");
            if (t.Kind == LateDeductionKind.Fraction && t.Value > 1) return FinanceOperationResult.Fail("الجزء من أجر اليوم لا يتجاوز 1 (يوم كامل)");
            if (i > 0 && (ordered[i - 1].ToMinutes is not int prev || t.FromMinutes <= prev))
                return FinanceOperationResult.Fail($"الشريحة {i + 1} تتداخل مع التي قبلها");
        }
        if (enabled && ordered.Count == 0) return FinanceOperationResult.Fail("أضف شريحة واحدة على الأقل قبل التفعيل");

        var policy = await _db.HrPolicies.FirstOrDefaultAsync();
        if (policy is null) _db.HrPolicies.Add(policy = new HrPolicy());
        policy.LateDeductionEnabled = enabled;
        policy.LateDeductionFrom = from is null ? null : new DateTime(from.Value.Year, from.Value.Month, 1);
        _db.LateDeductionTiers.RemoveRange(await _db.LateDeductionTiers.ToListAsync());
        foreach (var t in ordered)
            _db.LateDeductionTiers.Add(new LateDeductionTier { FromMinutes = t.FromMinutes, ToMinutes = t.ToMinutes, Kind = t.Kind, Value = t.Kind == LateDeductionKind.None ? 0 : t.Value });
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Update", "LateDeductionTiers", null,
            $"قواعد التأخير: {(enabled ? "مفعّلة" : "معطّلة")}{(from is null ? "" : $" من {from:MM/yyyy}")} — {ordered.Count} شريحة");
        return FinanceOperationResult.Ok();
    }

    // ============================ المسامحة والاستثناءات ============================

    private async Task<FinanceOperationResult?> GuardAsync(DateTime anyDayInMonth, int userId)
    {
        if (!await SpecialPermission.HasAsync(_db, userId, SpecialPermission.LateForgive) && !await new CashBoxService(_db).IsAdminAsync(userId))
            return FinanceOperationResult.Fail("لا تملك صلاحية «مسامحة التأخير»");
        if (await _db.PayrollRuns.AnyAsync(r => r.PeriodMonth == anyDayInMonth.Month && r.PeriodYear == anyDayInMonth.Year && r.Status == PayrollRunStatus.Approved))
            return FinanceOperationResult.Fail($"رواتب {anyDayInMonth:MM/yyyy} معتمدة؛ لا تُعدَّل مسامحاتها");
        return null;
    }

    /// <summary>مسامحة يوم: minutes بين 0 و دقائق تأخير ذلك اليوم (0 = إلغاء المسامحة). السبب إلزامي عند المسامحة.</summary>
    public async Task<FinanceOperationResult> ForgiveDayAsync(int employeeId, DateTime date, int minutes, string? reason, int userId)
    {
        date = date.Date;
        if (await GuardAsync(date, userId) is { } denied) return denied;
        var rec = await _db.AttendanceRecords.Include(a => a.Employee).FirstOrDefaultAsync(a => a.EmployeeId == employeeId && a.AttendanceDate == date);
        if (rec is null || rec.LateMinutes <= 0) return FinanceOperationResult.Fail("لا تأخير مسجّل لهذا اليوم");
        if (minutes < 0 || minutes > rec.LateMinutes) return FinanceOperationResult.Fail($"الدقائق المسامَحة بين 0 و {rec.LateMinutes}");
        if (minutes > 0 && string.IsNullOrWhiteSpace(reason)) return FinanceOperationResult.Fail("اكتب سبب المسامحة");
        var before = rec.ForgivenMinutes;
        rec.ForgivenMinutes = minutes;
        rec.ForgiveReason = minutes == 0 ? null : reason!.Trim();
        rec.ForgivenByUserId = minutes == 0 ? null : userId;
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Update", "AttendanceRecords", rec.Id,
            minutes == 0 ? $"إلغاء مسامحة تأخير {rec.Employee.FullName} يوم {date:yyyy/MM/dd} (كانت {before} د)"
                         : $"مسامحة تأخير {rec.Employee.FullName} يوم {date:yyyy/MM/dd}: {minutes} من {rec.LateMinutes} د — {reason!.Trim()}");
        return FinanceOperationResult.Ok();
    }

    /// <summary>رصيد مسامحة شهري لموظف (0 = حذفه): يُسقَط من دقائق تأخيره بترتيب الأيام.</summary>
    public async Task<FinanceOperationResult> SetMonthlyForgivenessAsync(int employeeId, int month, int year, int minutes, string? reason, int userId)
    {
        if (month is < 1 or > 12) return FinanceOperationResult.Fail("شهر غير صحيح");
        if (await GuardAsync(new DateTime(year, month, 1), userId) is { } denied) return denied;
        if (minutes < 0) return FinanceOperationResult.Fail("الدقائق لا تكون سالبة");
        var emp = await _db.Employees.FindAsync(employeeId);
        if (emp is null) return FinanceOperationResult.Fail("الموظف غير موجود");
        var row = await _db.LateMonthlyForgivenesses.FirstOrDefaultAsync(f => f.EmployeeId == employeeId && f.PeriodMonth == month && f.PeriodYear == year);
        if (minutes == 0)
        {
            if (row is not null) _db.LateMonthlyForgivenesses.Remove(row);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(reason)) return FinanceOperationResult.Fail("اكتب سبب المسامحة الشهرية");
            if (row is null) _db.LateMonthlyForgivenesses.Add(row = new LateMonthlyForgiveness { EmployeeId = employeeId, PeriodMonth = month, PeriodYear = year });
            row.Minutes = minutes; row.Reason = reason.Trim(); row.CreatedByUserId = userId; row.CreatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Update", "LateMonthlyForgiveness", row?.Id,
            minutes == 0 ? $"إلغاء المسامحة الشهرية لـ {emp.FullName} {month:00}/{year}" : $"مسامحة شهرية {minutes} د لـ {emp.FullName} {month:00}/{year} — {reason!.Trim()}");
        return FinanceOperationResult.Ok();
    }

    /// <summary>استثناء موظف من خصم التأخير (إعفاء كامل) أو سماح خاص بالدقائق لهذا الخصم فقط.</summary>
    public async Task<FinanceOperationResult> SetEmployeeExceptionAsync(int employeeId, bool exempt, int? graceOverride, int userId)
    {
        if (!await SpecialPermission.HasAsync(_db, userId, SpecialPermission.LateForgive) && !await new CashBoxService(_db).IsAdminAsync(userId))
            return FinanceOperationResult.Fail("لا تملك صلاحية «مسامحة التأخير»");
        if (graceOverride is < 0) return FinanceOperationResult.Fail("السماح الخاص لا يكون سالبًا");
        var e = await _db.Employees.FindAsync(employeeId);
        if (e is null) return FinanceOperationResult.Fail("الموظف غير موجود");
        e.LateDeductionExempt = exempt;
        e.LateGraceOverride = exempt ? null : graceOverride;
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Update", "Employees", e.Id,
            $"استثناء خصم التأخير لـ {e.FullName}: {(exempt ? "معفى" : graceOverride is int g ? $"سماح خاص {g} د" : "القاعدة العامة")}");
        return FinanceOperationResult.Ok();
    }

    // ============================ للرواتب ============================

    /// <summary>يحمّل ما يلزم لحساب خصم التأخير لشهر لكل الموظفين (استعلام واحد لكل جدول).</summary>
    public async Task<Func<Employee, decimal, Func<decimal, decimal>, LateMonthResult>> LoadMonthAsync(int month, int year)
    {
        var settings = await GetSettingsAsync();
        if (!settings.AppliesTo(month, year)) return (_, _, _) => new LateMonthResult(0, 0, 0);
        var start = new DateTime(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        var days = (await _db.AttendanceRecords.AsNoTracking().Where(a => a.AttendanceDate >= start && a.AttendanceDate <= end && a.LateMinutes > 0)
                             .Select(a => new { a.EmployeeId, a.AttendanceDate, a.LateMinutes, a.ForgivenMinutes }).ToListAsync())
                   .GroupBy(a => a.EmployeeId).ToDictionary(g => g.Key, g => g.Select(a => (a.AttendanceDate, a.LateMinutes, a.ForgivenMinutes)).ToList());
        var pools = await _db.LateMonthlyForgivenesses.AsNoTracking().Where(f => f.PeriodMonth == month && f.PeriodYear == year)
                             .ToDictionaryAsync(f => f.EmployeeId, f => f.Minutes);
        return (e, dailyWage, fx) => ComputeMonth(days.GetValueOrDefault(e.Id) ?? new(), pools.GetValueOrDefault(e.Id), settings.Tiers, dailyWage, fx,
                                                  e.LateDeductionExempt, e.LateGraceOverride);
    }
}
