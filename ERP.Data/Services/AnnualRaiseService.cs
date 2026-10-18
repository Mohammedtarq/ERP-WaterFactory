using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>موظف مستحق للعلاوة السنوية (أو يستحقها خلال 30 يومًا).</summary>
public class AnnualRaiseRow
{
    public int EmployeeId { get; init; }
    public string EmployeeName { get; init; } = "";
    public string DepartmentName { get; init; } = "";
    public DateTime LastRaiseDate { get; init; }
    /// <summary>مصدر التاريخ: علاوة/ترقية مسجّلة، أو مكتوب يدويًا، أو التعيين.</summary>
    public string LastRaiseSource { get; init; } = "";
    public DateTime DueDate { get; init; }
    public decimal? SuggestedAmount { get; init; }
    /// <summary>موجب = متأخرة بعدد الأيام، صفر = اليوم، سالب = باقٍ عليها.</summary>
    public int DaysOverdue { get; init; }
    public bool IsDue => DaysOverdue >= 0;
    public string StatusText => DaysOverdue > 0 ? $"متأخرة {DaysOverdue} يومًا" : DaysOverdue == 0 ? "مستحقة اليوم" : $"بعد {-DaysOverdue} يومًا";
    public string LastRaiseText => $"{LastRaiseDate:yyyy/MM/dd}" + (LastRaiseSource.Length == 0 ? "" : $" ({LastRaiseSource})");
}

/// <summary>
/// العلاوة السنوية: سنة كاملة من آخر علاوة أو ترقية (أو التاريخ المكتوب للقدامى، أو التعيين) ← مستحق؛
/// ويظهر قبل 30 يومًا للتخطيط. المبلغ من الموظف وإلا من قسمه. «منح» يسجّل علاوة دائمة بتاريخ اليوم تدخل الرواتب،
/// و«تأجيل» بسبب يخفيه حتى تاريخ يُختار. للمدير ومسؤول الموارد البشرية (صلاحية الإضافة في الموارد البشرية).
/// </summary>
public class AnnualRaiseService
{
    public const int NoticeDays = 30;
    private readonly ProjectDbContext _db;
    public AnnualRaiseService(ProjectDbContext db) => _db = db;

    public async Task<List<AnnualRaiseRow>> UpcomingAsync(DateTime? today = null)
    {
        var day = (today ?? DateTime.Today).Date;
        var employees = await _db.Employees.AsNoTracking().Where(e => e.IsActive && !e.IsTemporary && e.EndOfServiceDate == null)
            .Select(e => new
            {
                e.Id, e.FullName, Department = e.Department != null ? e.Department.Name : "", DeptAmount = e.Department != null ? e.Department.AnnualRaiseAmount : null,
                e.AnnualRaiseAmount, e.LastRaiseDate, e.HireDate, e.RaiseSnoozedUntil
            }).ToListAsync();
        var lastMovements = (await _db.PromotionsAndRaises.AsNoTracking()
                .Where(p => p.MovementType == PromotionMovementType.AnnualRaise || p.MovementType == PromotionMovementType.Promotion)
                .GroupBy(p => p.EmployeeId).Select(g => new { EmployeeId = g.Key, Last = g.Max(p => p.EffectiveDate) }).ToListAsync())
            .ToDictionary(x => x.EmployeeId, x => x.Last);

        var rows = new List<AnnualRaiseRow>();
        foreach (var e in employees)
        {
            if (e.RaiseSnoozedUntil is DateTime snooze && snooze.Date > day) continue;
            var (last, source) = lastMovements.TryGetValue(e.Id, out var m) && (e.LastRaiseDate is null || m.Date >= e.LastRaiseDate.Value.Date) ? (m.Date, "آخر علاوة مسجّلة")
                               : e.LastRaiseDate is DateTime manual ? (manual.Date, "مكتوب")
                               : e.HireDate is DateTime hire ? (hire.Date, "التعيين")
                               : (DateTime.MinValue, "");
            if (last == DateTime.MinValue) continue;
            var due = last.AddYears(1);
            var overdue = (day - due).Days;
            if (overdue < -NoticeDays) continue;
            rows.Add(new AnnualRaiseRow
            {
                EmployeeId = e.Id, EmployeeName = e.FullName, DepartmentName = e.Department, LastRaiseDate = last, LastRaiseSource = source,
                DueDate = due, DaysOverdue = overdue, SuggestedAmount = e.AnnualRaiseAmount ?? e.DeptAmount
            });
        }
        return rows.OrderByDescending(r => r.DaysOverdue).ThenBy(r => r.EmployeeName).ToList();
    }

    private async Task<bool> CanManageAsync(int userId) =>
        await _db.Users.AnyAsync(u => u.Id == userId && u.IsActive && u.Role.Permissions.Any(p => p.ModuleCode == ModuleCode.HR && p.CanAdd));

    /// <summary>منح العلاوة: علاوة سنوية دائمة بتاريخ اليوم تُضاف للرواتب، ويبدأ عدّ السنة الجديدة.</summary>
    public async Task<FinanceOperationResult> GrantAsync(int employeeId, decimal amount, int userId, DateTime? date = null, string? notes = null)
    {
        if (!await CanManageAsync(userId)) return FinanceOperationResult.Fail("منح العلاوة للمدير ومسؤول الموارد البشرية فقط");
        if (amount <= 0) return FinanceOperationResult.Fail("اكتب مبلغ العلاوة");
        var e = await _db.Employees.FirstOrDefaultAsync(x => x.Id == employeeId && x.IsActive);
        if (e is null) return FinanceOperationResult.Fail("الموظف غير موجود");
        _db.PromotionsAndRaises.Add(new PromotionAndRaise
        {
            EmployeeId = e.Id, MovementType = PromotionMovementType.AnnualRaise, ApplicationType = PromotionApplicationType.PermanentAddition,
            Amount = amount, EffectiveDate = (date ?? DateTime.Today).Date, CreatedByUserId = userId,
            Notes = string.IsNullOrWhiteSpace(notes) ? "علاوة سنوية (من قائمة المستحقين)" : notes.Trim()
        });
        e.RaiseSnoozedUntil = null;
        e.RaiseSnoozeReason = null;
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Insert", "PromotionsAndRaises", e.Id, $"علاوة سنوية لـ {e.FullName}: {amount:N0}");
        return FinanceOperationResult.Ok();
    }

    /// <summary>تأجيل التنبيه بسبب إلزامي حتى تاريخ يُختار (يعود بعده إلى القائمة والجرس).</summary>
    public async Task<FinanceOperationResult> SnoozeAsync(int employeeId, DateTime until, string reason, int userId)
    {
        if (!await CanManageAsync(userId)) return FinanceOperationResult.Fail("تأجيل العلاوة للمدير ومسؤول الموارد البشرية فقط");
        if (string.IsNullOrWhiteSpace(reason)) return FinanceOperationResult.Fail("اكتب سبب التأجيل");
        if (until.Date <= DateTime.Today) return FinanceOperationResult.Fail("تاريخ التأجيل يجب أن يكون بعد اليوم");
        var e = await _db.Employees.FirstOrDefaultAsync(x => x.Id == employeeId);
        if (e is null) return FinanceOperationResult.Fail("الموظف غير موجود");
        e.RaiseSnoozedUntil = until.Date;
        e.RaiseSnoozeReason = reason.Trim();
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Update", "Employees", e.Id, $"تأجيل علاوة {e.FullName} حتى {until:yyyy/MM/dd}: {reason.Trim()}");
        return FinanceOperationResult.Ok();
    }

    /// <summary>
    /// الشفت المطبَّق: موظفو القسم بلا «شفت خاص» يأخذون الشفت الافتراضي للقسم (يُستدعى بعد حفظ القسم أو الموظف).
    /// قسم بلا شفت افتراضي لا يغيّر شفت أحد.
    /// </summary>
    public static async Task ApplyDepartmentShiftAsync(ProjectDbContext db, int departmentId)
    {
        var shift = await db.Departments.Where(d => d.Id == departmentId).Select(d => d.DefaultShiftId).FirstOrDefaultAsync();
        if (shift is null) return;
        await db.Employees.Where(e => e.DepartmentId == departmentId && !e.HasOwnShift && e.ShiftId != shift)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.ShiftId, shift));
    }
}
