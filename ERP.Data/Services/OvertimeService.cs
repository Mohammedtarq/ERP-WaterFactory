using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>سطر عمل إضافي في القائمة.</summary>
public record OvertimeRow(int Id, int EmployeeId, string EmployeeName, DateTime WorkDate, OvertimeKind Kind, decimal Quantity,
                          string? Note, bool IsVoided, string? VoidReason)
{
    public string KindText => Kind == OvertimeKind.Day ? "يوم إضافي" : "ساعات إضافية";
    public string QuantityText => Kind == OvertimeKind.Day ? $"{Quantity:0.##} يوم" : $"{Quantity:0.##} ساعة";
    public string StatusText => IsVoided ? $"ملغى: {VoidReason}" : "ساري";
}

/// <summary>ملخّص الإضافي لموظف في شهر بالأجر: الأيام بالأجر اليومي، والساعات بسعر الساعة (الخاص أو العام).</summary>
public record OvertimeSummaryRow(int EmployeeId, string EmployeeName, decimal Days, decimal Hours, decimal DailyWage, decimal HourlyRate,
                                 bool HasOwnRate, string Currency)
{
    public decimal Amount => Math.Round(Days * DailyWage + Hours * HourlyRate, 2);
    public string RateText => HourlyRate == 0 ? "غير محدد" : $"{HourlyRate:N0}{(HasOwnRate ? " (خاص)" : "")}";
    public bool MissingRate => Hours > 0 && HourlyRate == 0;
}

/// <summary>
/// العمل الإضافي (إدخال يدوي بقرار المدير): أيام إضافية (الجمعة والعطل حين يقتضي الدوام) بالأجر اليومي الاعتيادي
/// (الراتب ÷ 30)، وساعات إضافية بسعر ساعة تضبطه الإدارة للكل أو لموظف بعينه (الخاص يعلو على العام).
/// يدخل الإضافي في كشف الراتب ويُقيَّد مع الرواتب عند الاعتماد.
/// </summary>
public class OvertimeService
{
    private readonly ProjectDbContext _db;
    public OvertimeService(ProjectDbContext db) => _db = db;

    private Task<bool> IsMonthApprovedAsync(DateTime day) =>
        _db.PayrollRuns.AnyAsync(r => r.PeriodMonth == day.Month && r.PeriodYear == day.Year && r.Status == PayrollRunStatus.Approved);

    public async Task<FinanceOperationResult> AddAsync(int employeeId, DateTime workDate, OvertimeKind kind, decimal quantity, string? note, int userId)
    {
        var e = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.Id == employeeId);
        if (e is null || !e.IsActive) return FinanceOperationResult.Fail("اختر موظفًا فعّالًا");
        if (e.IsTemporary) return FinanceOperationResult.Fail($"{e.FullName} عامل وقتي — أجره من شاشة العمال الوقتيين");
        if (workDate.Date > DateTime.Today) return FinanceOperationResult.Fail("لا يُسجَّل عمل إضافي ليوم لم يأتِ بعد");
        if (quantity <= 0) return FinanceOperationResult.Fail(kind == OvertimeKind.Day ? "عدد الأيام أكبر من صفر" : "عدد الساعات أكبر من صفر");
        if (kind == OvertimeKind.Day && quantity > 1) return FinanceOperationResult.Fail("اليوم الإضافي يُسجَّل لتاريخه: يوم واحد لكل تاريخ");
        if (kind == OvertimeKind.Hours && quantity > 16) return FinanceOperationResult.Fail("الساعات الإضافية في اليوم لا تتجاوز 16");
        if (await IsMonthApprovedAsync(workDate)) return FinanceOperationResult.Fail($"رواتب {workDate:MM/yyyy} معتمدة — لا يُضاف إليها عمل إضافي");
        if (kind == OvertimeKind.Day && await _db.OvertimeEntries.AnyAsync(o => o.EmployeeId == employeeId && o.WorkDate == workDate.Date && o.Kind == OvertimeKind.Day && !o.IsVoided))
            return FinanceOperationResult.Fail($"يوم {workDate:yyyy/MM/dd} مسجّل يومًا إضافيًا لـ {e.FullName} مسبقًا");
        var entry = new OvertimeEntry { EmployeeId = employeeId, WorkDate = workDate.Date, Kind = kind, Quantity = quantity,
                                        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), CreatedByUserId = userId };
        _db.OvertimeEntries.Add(entry);
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Insert", "OvertimeEntries", entry.Id,
            $"عمل إضافي لـ {e.FullName} يوم {workDate:yyyy/MM/dd}: {(kind == OvertimeKind.Day ? "يوم" : $"{quantity:0.##} ساعة")}");
        return FinanceOperationResult.Ok();
    }

    public async Task<FinanceOperationResult> VoidAsync(int id, string reason, int userId)
    {
        if (string.IsNullOrWhiteSpace(reason)) return FinanceOperationResult.Fail("سبب الإلغاء إلزامي");
        var o = await _db.OvertimeEntries.Include(x => x.Employee).FirstOrDefaultAsync(x => x.Id == id);
        if (o is null) return FinanceOperationResult.Fail("السطر غير موجود");
        if (o.IsVoided) return FinanceOperationResult.Fail("ملغى مسبقًا");
        if (await IsMonthApprovedAsync(o.WorkDate)) return FinanceOperationResult.Fail($"رواتب {o.WorkDate:MM/yyyy} معتمدة — لا يُلغى منها شيء");
        (o.IsVoided, o.VoidReason) = (true, reason.Trim());
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Void", "OvertimeEntries", o.Id, $"إلغاء عمل إضافي لـ {o.Employee.FullName} يوم {o.WorkDate:yyyy/MM/dd}: {o.VoidReason}");
        return FinanceOperationResult.Ok();
    }

    public async Task<List<OvertimeRow>> ListAsync(int month, int year)
    {
        var start = new DateTime(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        return await _db.OvertimeEntries.AsNoTracking().Where(o => o.WorkDate >= start && o.WorkDate <= end)
            .OrderByDescending(o => o.WorkDate).ThenBy(o => o.Employee.FullName)
            .Select(o => new OvertimeRow(o.Id, o.EmployeeId, o.Employee.FullName, o.WorkDate, o.Kind, o.Quantity, o.Note, o.IsVoided, o.VoidReason))
            .ToListAsync();
    }

    public async Task<decimal> GetGlobalRateAsync() => (await _db.HrPolicies.AsNoTracking().FirstOrDefaultAsync())?.OvertimeHourlyRate ?? 0;

    /// <summary>
    /// سعر الساعة الإضافية: للكل (يصبح السعر العام ويُزال كل سعر خاص) أو لموظف واحد (سعر خاص يعلو على العام).
    /// employeeId = null ← للكل. rate = null مع موظف ← إزالة سعره الخاص فيعود للعام.
    /// </summary>
    public async Task<FinanceOperationResult> SetRateAsync(int? employeeId, decimal? rate, int userId)
    {
        if (rate < 0) return FinanceOperationResult.Fail("سعر الساعة لا يكون سالبًا");
        string summary;
        if (employeeId is null)
        {
            if (rate is null) return FinanceOperationResult.Fail("اكتب سعر الساعة");
            var policy = await _db.HrPolicies.FirstOrDefaultAsync();
            if (policy is null) _db.HrPolicies.Add(policy = new HrPolicy());
            policy.OvertimeHourlyRate = rate.Value;
            await _db.Employees.Where(e => e.OvertimeHourlyRate != null).ExecuteUpdateAsync(u => u.SetProperty(e => e.OvertimeHourlyRate, (decimal?)null));
            summary = $"سعر الساعة الإضافية للكل: {rate:N0}";
        }
        else
        {
            var e = await _db.Employees.FindAsync(employeeId.Value);
            if (e is null) return FinanceOperationResult.Fail("الموظف غير موجود");
            e.OvertimeHourlyRate = rate;
            summary = rate is null ? $"إزالة سعر الساعة الخاص بـ {e.FullName} (يعود للسعر العام)" : $"سعر ساعة إضافية خاص بـ {e.FullName}: {rate:N0}";
        }
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Update", "HrPolicies", employeeId, summary);
        return FinanceOperationResult.Ok();
    }

    /// <summary>ملخّص الشهر لكل موظف عنده إضافي (بالأجر اليومي وسعر الساعة الساريين الآن).</summary>
    public async Task<List<OvertimeSummaryRow>> SummaryAsync(int month, int year)
    {
        var start = new DateTime(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        var global = await GetGlobalRateAsync();
        var totals = await _db.OvertimeEntries.AsNoTracking().Where(o => !o.IsVoided && o.WorkDate >= start && o.WorkDate <= end)
            .GroupBy(o => o.EmployeeId)
            .Select(g => new
            {
                g.Key,
                Days = g.Where(o => o.Kind == OvertimeKind.Day).Sum(o => (decimal?)o.Quantity) ?? 0,
                Hours = g.Where(o => o.Kind == OvertimeKind.Hours).Sum(o => (decimal?)o.Quantity) ?? 0
            }).ToListAsync();
        var ids = totals.Select(t => t.Key).ToList();
        var employees = await _db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id);
        return totals.Select(t =>
        {
            var e = employees[t.Key];
            return new OvertimeSummaryRow(e.Id, e.FullName, t.Days, t.Hours, Math.Round(e.BaseSalary / HrRules.DaysPerMonthForDailyWage, 2),
                                          e.OvertimeHourlyRate ?? global, e.OvertimeHourlyRate is not null, e.SalaryCurrency.ToString());
        }).OrderBy(r => r.EmployeeName).ToList();
    }
}
