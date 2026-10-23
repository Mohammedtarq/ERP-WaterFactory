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
    /// <summary>درجة العلاوة (A / B / C) — فارغ: لم تُحدَّد.</summary>
    public string? GradeName { get; init; }
    /// <summary>المبلغ من «مبلغ علاوة استثنائي» في بطاقة الموظف لا من درجته.</summary>
    public bool IsException { get; init; }
    public string AmountNote => IsException ? "استثناء" : GradeName is null && SuggestedAmount is null ? "حدّد درجته" : "";
    /// <summary>موجب = متأخرة بعدد الأيام، صفر = اليوم، سالب = باقٍ عليها.</summary>
    public int DaysOverdue { get; init; }
    public bool IsDue => DaysOverdue >= 0;
    public string StatusText => DaysOverdue > 0 ? $"متأخرة {DaysOverdue} يومًا" : DaysOverdue == 0 ? "مستحقة اليوم" : $"بعد {-DaysOverdue} يومًا";
    public string LastRaiseText => $"{LastRaiseDate:yyyy/MM/dd}" + (LastRaiseSource.Length == 0 ? "" : $" ({LastRaiseSource})");
}

/// <summary>
/// العلاوة السنوية: سنة كاملة من آخر علاوة أو ترقية (أو التاريخ المكتوب للقدامى، أو التعيين) ← مستحق؛
/// ويظهر قبل 30 يومًا للتخطيط. المبلغ: الاستثناء في بطاقة الموظف وإلا مبلغ درجته (A / B / C). «منح» يسجّل علاوة دائمة بتاريخ اليوم تدخل الرواتب،
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
                e.Id, e.FullName, Department = e.Department != null ? e.Department.Name : "",
                Grade = e.RaiseGrade != null ? e.RaiseGrade.Name : null, GradeAmount = e.RaiseGrade != null ? (decimal?)e.RaiseGrade.Amount : null,
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
                DueDate = due, DaysOverdue = overdue, SuggestedAmount = e.AnnualRaiseAmount ?? e.GradeAmount,
                GradeName = e.Grade, IsException = e.AnnualRaiseAmount is not null
            });
        }
        return rows.OrderByDescending(r => r.DaysOverdue).ThenBy(r => r.EmployeeName).ToList();
    }

    private async Task<bool> CanManageAsync(int userId) =>
        await _db.Users.AnyAsync(u => u.Id == userId && u.IsActive && u.Role.Permissions.Any(p => p.ModuleCode == ModuleCode.HR && p.CanAdd));

    /// <summary>منح العلاوة: علاوة سنوية دائمة بتاريخ اليوم تُضاف للرواتب، ويبدأ عدّ السنة الجديدة.</summary>
    public async Task<(FinanceOperationResult result, int? raiseId)> GrantWithIdAsync(int employeeId, decimal amount, int userId, DateTime? date = null, string? notes = null)
    {
        if (!await CanManageAsync(userId)) return (FinanceOperationResult.Fail("منح العلاوة للمدير ومسؤول الموارد البشرية فقط"), null);
        if (amount <= 0) return (FinanceOperationResult.Fail("اكتب مبلغ العلاوة"), null);
        var e = await _db.Employees.FirstOrDefaultAsync(x => x.Id == employeeId && x.IsActive);
        if (e is null) return (FinanceOperationResult.Fail("الموظف غير موجود"), null);
        var raise = new PromotionAndRaise
        {
            EmployeeId = e.Id, MovementType = PromotionMovementType.AnnualRaise, ApplicationType = PromotionApplicationType.PermanentAddition,
            Amount = amount, EffectiveDate = (date ?? DateTime.Today).Date, CreatedByUserId = userId,
            Notes = string.IsNullOrWhiteSpace(notes) ? "علاوة سنوية (من قائمة المستحقين)" : notes.Trim()
        };
        _db.PromotionsAndRaises.Add(raise);
        e.RaiseSnoozedUntil = null;
        e.RaiseSnoozeReason = null;
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Insert", "PromotionsAndRaises", raise.Id, $"علاوة سنوية لـ {e.FullName}: {amount:N0}");
        return (FinanceOperationResult.Ok(), raise.Id);
    }

    public async Task<FinanceOperationResult> GrantAsync(int employeeId, decimal amount, int userId, DateTime? date = null, string? notes = null)
        => (await GrantWithIdAsync(employeeId, amount, userId, date, notes)).result;

    /// <summary>آخر العلاوات السنوية الممنوحة (للمراجعة والإلغاء)، وهل ما زال إلغاؤها ممكنًا (لم تُعتمد رواتب بها).</summary>
    public async Task<List<GrantedRaiseRow>> RecentGrantsAsync(int take = 40)
    {
        var lastApproved = await _db.PayrollRuns.Where(p => p.Status == PayrollRunStatus.Approved)
                                    .Select(p => (int?)(p.PeriodYear * 12 + p.PeriodMonth)).MaxAsync() ?? 0;
        var rows = await _db.PromotionsAndRaises.AsNoTracking().Where(r => r.MovementType == PromotionMovementType.AnnualRaise)
            .OrderByDescending(r => r.EffectiveDate).ThenByDescending(r => r.Id).Take(take)
            .Select(r => new { r.Id, r.Employee.FullName, r.Amount, r.EffectiveDate, r.Notes }).ToListAsync();
        return rows.Select(r => new GrantedRaiseRow(r.Id, r.FullName, r.Amount, r.EffectiveDate, r.Notes,
                                                    r.EffectiveDate.Year * 12 + r.EffectiveDate.Month > lastApproved)).ToList();
    }

    /// <summary>
    /// إلغاء علاوة سنوية مُنحت بالخطأ (بسبب): تُحذف فيعود الراتب كما كان ويعود الموظف لقائمة المستحقين،
    /// ما لم تُعتمد رواتب شهر سريانها أو ما بعده (حينها أثرها في رواتب معتمدة — لا يُلغى).
    /// </summary>
    public async Task<FinanceOperationResult> UndoAsync(int raiseId, string reason, int userId)
    {
        if (!await CanManageAsync(userId)) return FinanceOperationResult.Fail("إلغاء العلاوة للمدير ومسؤول الموارد البشرية فقط");
        if (string.IsNullOrWhiteSpace(reason)) return FinanceOperationResult.Fail("اكتب سبب الإلغاء");
        var r = await _db.PromotionsAndRaises.Include(x => x.Employee).FirstOrDefaultAsync(x => x.Id == raiseId && x.MovementType == PromotionMovementType.AnnualRaise);
        if (r is null) return FinanceOperationResult.Fail("العلاوة غير موجودة");
        var key = r.EffectiveDate.Year * 12 + r.EffectiveDate.Month;
        if (await _db.PayrollRuns.AnyAsync(p => p.Status == PayrollRunStatus.Approved && p.PeriodYear * 12 + p.PeriodMonth >= key))
            return FinanceOperationResult.Fail($"اعتُمدت رواتب {r.EffectiveDate:MM/yyyy} أو ما بعدها بهذه العلاوة — لا تُلغى");
        _db.PromotionsAndRaises.Remove(r);
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Delete", "PromotionsAndRaises", raiseId,
            $"إلغاء علاوة سنوية لـ {r.Employee.FullName}: {r.Amount:N0} بتاريخ {r.EffectiveDate:yyyy/MM/dd} — {reason.Trim()}");
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

/// <summary>علاوة ممنوحة: تُلغى ما دامت رواتب شهرها وما بعده غير معتمدة.</summary>
public record GrantedRaiseRow(int Id, string EmployeeName, decimal Amount, DateTime EffectiveDate, string? Notes, bool CanUndo)
{
    public string UndoText => CanUndo ? "قابلة للإلغاء" : "في رواتب معتمدة";
}
