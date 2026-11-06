using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>طلب إجازة: يومية (من تاريخ إلى تاريخ) أو زمنية (يوم واحد من ساعة إلى ساعة — النوع اعتيادية دائمًا).</summary>
public record LeaveInput(int EmployeeId, int LeaveTypeId, LeaveKind Kind, DateTime FromDate, DateTime ToDate,
                         TimeSpan? FromTime = null, TimeSpan? ToTime = null, string? Note = null);

/// <summary>ما ستُحسب به الإجازة قبل تسجيلها: أيام العمل، والمدفوع من الرصيد، والزائد بدون راتب، والتنبيه.</summary>
public record LeaveQuote(decimal WorkDays, decimal PaidDays, decimal UnpaidDays, string Summary, string? Warning);

/// <summary>رصيد نوع إجازة لموظف في فترته (الشهر أو السنة).</summary>
public record LeaveBalanceRow(int LeaveTypeId, string TypeName, string PeriodText, decimal? Allowed, decimal Used, decimal? Remaining, bool IsPaid)
{
    public string AllowedText => Allowed is null ? "بلا حد" : $"{Allowed:0.##} يوم";
    public string RemainingText => Remaining is null ? "—" : $"{Remaining:0.##} يوم";
}

/// <summary>سطر إجازة في القائمة.</summary>
public record LeaveRow(int Id, string LeaveNumber, int EmployeeId, string EmployeeName, string TypeName, LeaveKind Kind,
                       DateTime FromDate, DateTime ToDate, TimeSpan? FromTime, TimeSpan? ToTime,
                       decimal WorkDays, decimal PaidDays, decimal UnpaidDays, string? Note, bool IsVoided, string? VoidReason)
{
    public string KindText => Kind == LeaveKind.Hourly ? "زمنية" : "يومية";
    public string PeriodText => Kind == LeaveKind.Hourly
        ? $"{FromDate:yyyy/MM/dd} من {FromTime:hh\\:mm} إلى {ToTime:hh\\:mm}"
        : FromDate == ToDate ? $"{FromDate:yyyy/MM/dd}" : $"{FromDate:yyyy/MM/dd} — {ToDate:yyyy/MM/dd}";
    public string ChargeText => Kind == LeaveKind.Hourly && PaidDays == 0 && UnpaidDays == 0 ? "ضمن المسموح شهريًا"
        : UnpaidDays > 0 && PaidDays > 0 ? $"{PaidDays:0.##} من الرصيد + {UnpaidDays:0.##} بدون راتب"
        : UnpaidDays > 0 ? $"{UnpaidDays:0.##} بدون راتب" : $"{PaidDays:0.##} من الرصيد";
    public string StatusText => IsVoided ? $"ملغاة: {VoidReason}" : "سارية";
}

/// <summary>أيام العمل والعطل: العطلة الأسبوعية من «استثناءات أيام العمل» (الافتراضي الجمعة) + العطل الرسمية.</summary>
public static class HrCalendar
{
    public static bool IsWeeklyOff(DateTime day, Employee e, IReadOnlyCollection<WorkDayException> rules)
    {
        var name = day.DayOfWeek.ToString();
        if (rules.Count == 0) return day.DayOfWeek == DayOfWeek.Friday;
        return rules.Any(r => r.IsActive && string.Equals(r.ExceptionDay, name, StringComparison.OrdinalIgnoreCase)
                              && (r.DepartmentId == null || r.DepartmentId == e.DepartmentId)
                              && r.StartDate.Date <= day && (r.EndDate == null || r.EndDate.Value.Date >= day));
    }

    public static bool IsWorkDay(DateTime day, Employee e, IReadOnlyCollection<WorkDayException> rules, ISet<DateTime> holidays) =>
        !holidays.Contains(day.Date) && !IsWeeklyOff(day, e, rules);

    /// <summary>
    /// شفت اليوم مع الإجازة الزمنية: إن بدأت الإجازة عند بداية الشفت (ضمن السماح) فالتأخير يُحسب من نهايتها.
    /// مثال: الشفت 08:00 والإجازة 08:00–10:00 والدخول 10:05 ← 5 دقائق (ضمن السماح) = حاضر.
    /// </summary>
    public static Shift? EffectiveShift(Shift? shift, IEnumerable<(TimeSpan from, TimeSpan to)> hourly)
    {
        if (shift is null) return null;
        var cover = hourly.Where(h => h.from <= shift.CheckInTime + TimeSpan.FromMinutes(shift.CheckInGraceMinutes) && h.to > shift.CheckInTime)
                          .Select(h => (TimeSpan?)h.to).Max();
        if (cover is null) return shift;
        return new Shift
        {
            Id = shift.Id, Name = shift.Name, CheckInTime = cover.Value, CheckInGraceMinutes = shift.CheckInGraceMinutes,
            CheckOutTime = shift.CheckOutTime, CheckOutGraceMinutes = shift.CheckOutGraceMinutes, IsEntryOnly = shift.IsEntryOnly,
            OvertimeStartsAfter = shift.OvertimeStartsAfter
        };
    }
}

/// <summary>
/// الإجازات (قرارات المدير): لكل نوع رصيد شهري أو سنوي قابل للتعديل (الاعتيادية 3 أيام في الشهر تسقط بنهايته).
/// اليومية تُحسب بأيام العمل فقط (بلا العطلة الأسبوعية والعطل الرسمية) وتُقسم على الأشهر إن عبرت شهرًا.
/// الزمنية: المسموح في الشهر (افتراضيًا زمنيتان) بلا خصم، ومن بعدها كل زمنية يوم من رصيد الاعتيادية، وإن لم يتوفر فيوم بدون راتب.
/// ما يتجاوز رصيد النوع المدفوع يُسجَّل بدون راتب مع تنبيه، ويُخصم في الرواتب بالأجر اليومي.
/// الحضور يتبع الإجازة: أيامها «إجازة معتمدة» لا غياب، والزمنية تُعفي من التأخير خلالها — مهما كان ترتيب رفع البصمة.
/// </summary>
public class HrLeaveService
{
    private readonly ProjectDbContext _db;
    public HrLeaveService(ProjectDbContext db) => _db = db;

    // ============================ الأنواع والسياسة والعطل ============================

    public Task<List<LeaveType>> GetTypesAsync(bool activeOnly = false) =>
        _db.LeaveTypes.AsNoTracking().Where(t => !activeOnly || t.IsActive).OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync();

    public async Task<HrPolicy> GetPolicyAsync() =>
        await _db.HrPolicies.AsNoTracking().FirstOrDefaultAsync() ?? new HrPolicy();

    /// <summary>حفظ أنواع الإجازات كلها مرة واحدة (اسم فريد، أيام غير سالبة، نوع اعتيادية واحد).</summary>
    public async Task<FinanceOperationResult> SaveTypesAsync(IReadOnlyList<LeaveType> types, int freeHourlyPerMonth, int userId)
    {
        if (freeHourlyPerMonth < 0) return FinanceOperationResult.Fail("عدد الزمنيات المسموحة شهريًا لا يكون سالبًا");
        foreach (var t in types)
        {
            t.Name = t.Name.Trim();
            if (t.Name.Length == 0) return FinanceOperationResult.Fail("اسم نوع الإجازة مطلوب");
            if (t.AllowedDays < 0) return FinanceOperationResult.Fail($"أيام «{t.Name}» لا تكون سالبة");
        }
        if (types.GroupBy(t => t.Name).FirstOrDefault(g => g.Count() > 1) is { } dup) return FinanceOperationResult.Fail($"نوع الإجازة «{dup.Key}» مكرر");
        if (types.Count(t => t.IsRegular && t.IsActive) != 1) return FinanceOperationResult.Fail("حدّد نوعًا واحدًا فعّالًا «اعتيادية» (تُخصم منه الزمنية)");

        var existing = await _db.LeaveTypes.ToDictionaryAsync(t => t.Id);
        var order = 1;
        foreach (var t in types)
        {
            if (t.Id != 0 && existing.TryGetValue(t.Id, out var row))
            {
                (row.Name, row.AllowedDays, row.AllowancePeriod, row.IsPaid, row.IsRegular, row.IsActive, row.SortOrder) =
                    (t.Name, t.AllowedDays, t.AllowancePeriod, t.IsPaid, t.IsRegular, t.IsActive, order++);
            }
            else
            {
                _db.LeaveTypes.Add(new LeaveType { Name = t.Name, AllowedDays = t.AllowedDays, AllowancePeriod = t.AllowancePeriod, IsPaid = t.IsPaid,
                                                   IsRegular = t.IsRegular, IsActive = t.IsActive, SortOrder = order++ });
            }
        }
        var policy = await _db.HrPolicies.FirstOrDefaultAsync();
        if (policy is null) _db.HrPolicies.Add(policy = new HrPolicy());
        policy.FreeHourlyLeavesPerMonth = freeHourlyPerMonth;
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Update", "LeaveTypes", null,
            $"أنواع الإجازات: {string.Join("، ", types.Select(t => $"{t.Name} {(t.AllowedDays is null ? "بلا حد" : $"{t.AllowedDays:0.##}/{(t.AllowancePeriod == LeaveAllowancePeriod.Month ? "شهر" : "سنة")}")}"))} — الزمنيات المسموحة {freeHourlyPerMonth} في الشهر");
        return FinanceOperationResult.Ok();
    }

    /// <summary>حذف نوع إجازة لم يُستعمل في أي إجازة؛ المستعمل يبقى (يُوقف بدل الحذف حتى لا يضيع أثره).</summary>
    public async Task<FinanceOperationResult> DeleteTypeAsync(int typeId, int userId)
    {
        var t = await _db.LeaveTypes.FindAsync(typeId);
        if (t is null) return FinanceOperationResult.Fail("نوع الإجازة غير موجود");
        if (await _db.EmployeeLeaves.AnyAsync(l => l.LeaveTypeId == typeId))
            return FinanceOperationResult.Fail($"«{t.Name}» مستعمل في إجازات مسجّلة — لا يُحذف؛ أوقفه (أزل علامة «فعّال») بدل الحذف");
        if (t.IsRegular && t.IsActive) return FinanceOperationResult.Fail($"«{t.Name}» هو نوع «الاعتيادية» الذي تُخصم منه الزمنية — حدّد غيره اعتيادية أولًا");
        _db.LeaveTypes.Remove(t);
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Delete", "LeaveTypes", typeId, $"حذف نوع الإجازة غير المستعمل «{t.Name}»");
        return FinanceOperationResult.Ok();
    }

    public Task<List<PublicHoliday>> GetHolidaysAsync(int year) =>
        _db.PublicHolidays.AsNoTracking().Where(h => h.HolidayDate.Year == year).OrderBy(h => h.HolidayDate).ToListAsync();

    public async Task<FinanceOperationResult> AddHolidayAsync(DateTime date, string name, int userId)
    {
        name = name.Trim();
        if (name.Length == 0) return FinanceOperationResult.Fail("اكتب اسم العطلة (مثل: عيد الفطر)");
        if (await _db.PublicHolidays.AnyAsync(h => h.HolidayDate == date.Date)) return FinanceOperationResult.Fail($"{date:yyyy/MM/dd} مسجّل عطلة رسمية مسبقًا");
        _db.PublicHolidays.Add(new PublicHoliday { HolidayDate = date.Date, Name = name });
        // غياب مسجّل في يوم صار عطلة (من رفع بصمة سابق) يُزال: العطلة لا غياب فيها
        _db.AttendanceRecords.RemoveRange(_db.AttendanceRecords.Where(a => a.AttendanceDate == date.Date && a.Status == AttendanceStatus.Absent));
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Insert", "PublicHolidays", null, $"عطلة رسمية {date:yyyy/MM/dd}: {name}");
        return FinanceOperationResult.Ok();
    }

    /// <summary>تعديل اسم العطلة أو تاريخها (بدل حذفها وإعادة إضافتها)؛ لا يمسّ شهرًا رواتبه معتمدة.</summary>
    public async Task<FinanceOperationResult> UpdateHolidayAsync(int id, DateTime date, string name, int userId)
    {
        name = name.Trim();
        if (name.Length == 0) return FinanceOperationResult.Fail("اكتب اسم العطلة");
        var h = await _db.PublicHolidays.FindAsync(id);
        if (h is null) return FinanceOperationResult.Fail("العطلة غير موجودة");
        if (h.HolidayDate.Date != date.Date)
        {
            if (await _db.PublicHolidays.AnyAsync(x => x.Id != id && x.HolidayDate == date.Date)) return FinanceOperationResult.Fail($"{date:yyyy/MM/dd} مسجّل عطلة رسمية مسبقًا");
            foreach (var day in new[] { h.HolidayDate, date })
                if (await ClosedMonthAsync(day, day) is { } closed) return FinanceOperationResult.Fail(closed);
        }
        var before = $"{h.HolidayDate:yyyy/MM/dd} {h.Name}";
        (h.HolidayDate, h.Name) = (date.Date, name);
        // التاريخ الجديد صار عطلة: غياب مسجّل فيه يُزال كما عند الإضافة
        _db.AttendanceRecords.RemoveRange(_db.AttendanceRecords.Where(a => a.AttendanceDate == date.Date && a.Status == AttendanceStatus.Absent));
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Update", "PublicHolidays", id, $"تعديل العطلة الرسمية: {before} ← {date:yyyy/MM/dd} {name}");
        return FinanceOperationResult.Ok();
    }

    public async Task<FinanceOperationResult> DeleteHolidayAsync(int id, int userId)
    {
        var h = await _db.PublicHolidays.FindAsync(id);
        if (h is null) return FinanceOperationResult.Fail("العطلة غير موجودة");
        _db.PublicHolidays.Remove(h);
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Delete", "PublicHolidays", id, $"حذف العطلة الرسمية {h.HolidayDate:yyyy/MM/dd}: {h.Name}");
        return FinanceOperationResult.Ok();
    }

    // ============================ الحساب ============================

    private static (DateTime from, DateTime to) PeriodOf(LeaveType t, DateTime day) => t.AllowancePeriod == LeaveAllowancePeriod.Month
        ? (new DateTime(day.Year, day.Month, 1), new DateTime(day.Year, day.Month, 1).AddMonths(1).AddDays(-1))
        : (new DateTime(day.Year, 1, 1), new DateTime(day.Year, 12, 31));

    private static string PeriodName(LeaveType t, DateTime day) => t.AllowancePeriod == LeaveAllowancePeriod.Month ? $"شهر {day:MM/yyyy}" : $"سنة {day.Year}";

    /// <summary>المأخوذ من رصيد النوع في فترته (المدفوع فقط؛ تشمل زمنيات الاعتيادية الزائدة).</summary>
    private async Task<decimal> UsedAsync(int employeeId, LeaveType t, DateTime day, int? excludingId = null)
    {
        var (from, to) = PeriodOf(t, day);
        return await _db.EmployeeLeaves.Where(l => l.EmployeeId == employeeId && l.LeaveTypeId == t.Id && !l.IsVoided && l.Id != excludingId
                                                   && l.FromDate >= from && l.FromDate <= to)
                        .SumAsync(l => (decimal?)l.PaidDays) ?? 0;
    }

    private async Task<HashSet<DateTime>> HolidaysAsync(DateTime from, DateTime to) =>
        (await _db.PublicHolidays.AsNoTracking().Where(h => h.HolidayDate >= from && h.HolidayDate <= to).Select(h => h.HolidayDate).ToListAsync())
        .Select(d => d.Date).ToHashSet();

    /// <summary>الإجازة اليومية مقسومة على الأشهر: (من، إلى) لكل شهر تمسّه.</summary>
    private static IEnumerable<(DateTime from, DateTime to)> SplitByMonth(DateTime from, DateTime to)
    {
        for (var start = from; start <= to;)
        {
            var monthEnd = new DateTime(start.Year, start.Month, 1).AddMonths(1).AddDays(-1);
            var end = monthEnd < to ? monthEnd : to;
            yield return (start, end);
            start = end.AddDays(1);
        }
    }

    private sealed record Part(DateTime From, DateTime To, decimal WorkDays, decimal Paid, decimal Unpaid);

    /// <summary>يحسب الإجازة دون حفظها: أجزاء الأشهر، والمدفوع والزائد، ورسالة التنبيه.</summary>
    private async Task<(FinanceOperationResult result, LeaveType? type, Employee? employee, List<Part> parts, string? warning)> ComputeAsync(LeaveInput input, int? excludingId = null)
    {
        var none = new List<Part>();
        var e = await _db.Employees.AsNoTracking().Include(x => x.Shift).FirstOrDefaultAsync(x => x.Id == input.EmployeeId);
        if (e is null || !e.IsActive) return (FinanceOperationResult.Fail("اختر موظفًا فعّالًا"), null, null, none, null);
        if (e.IsTemporary) return (FinanceOperationResult.Fail($"{e.FullName} عامل وقتي — الإجازات للموظفين الدائمين"), null, null, none, null);
        var policy = await GetPolicyAsync();
        var types = await GetTypesAsync(activeOnly: true);
        var regular = types.FirstOrDefault(t => t.IsRegular);
        var from = input.FromDate.Date;
        var to = input.Kind == LeaveKind.Hourly ? from : input.ToDate.Date;
        if (to < from) return (FinanceOperationResult.Fail("تاريخ النهاية قبل تاريخ البداية"), null, e, none, null);
        if ((to - from).TotalDays > 366) return (FinanceOperationResult.Fail("الإجازة أطول من سنة — قسّمها"), null, e, none, null);

        // التداخل مع إجازة أخرى للموظف نفسه
        var overlap = await _db.EmployeeLeaves.AsNoTracking().Include(l => l.LeaveType)
            .Where(l => l.EmployeeId == e.Id && !l.IsVoided && l.Id != excludingId && l.FromDate <= to && l.ToDate >= from).ToListAsync();

        if (input.Kind == LeaveKind.Hourly)
        {
            if (regular is null) return (FinanceOperationResult.Fail("لا يوجد نوع «اعتيادية» فعّال — حدّده من أنواع الإجازات"), null, e, none, null);
            if (input.FromTime is not { } ft || input.ToTime is not { } tt || tt <= ft)
                return (FinanceOperationResult.Fail("حدّد وقت البداية والنهاية للإجازة الزمنية (النهاية بعد البداية)"), null, e, none, null);
            if (overlap.FirstOrDefault(l => l.Kind == LeaveKind.Day) is { } day)
                return (FinanceOperationResult.Fail($"{e.FullName} في إجازة {day.LeaveType.Name} يوم {from:yyyy/MM/dd}"), null, e, none, null);
            if (overlap.Any(l => l.Kind == LeaveKind.Hourly && l.FromTime < tt && l.ToTime > ft))
                return (FinanceOperationResult.Fail($"تتداخل مع زمنية أخرى لـ {e.FullName} في اليوم نفسه"), null, e, none, null);

            var monthStart = new DateTime(from.Year, from.Month, 1);
            var monthEnd = monthStart.AddMonths(1).AddDays(-1);
            var taken = await _db.EmployeeLeaves.CountAsync(l => l.EmployeeId == e.Id && l.Kind == LeaveKind.Hourly && !l.IsVoided && l.Id != excludingId
                                                                 && l.FromDate >= monthStart && l.FromDate <= monthEnd);
            if (taken < policy.FreeHourlyLeavesPerMonth)
                return (FinanceOperationResult.Ok(), regular, e, new List<Part> { new(from, to, 0, 0, 0) }, null);
            var available = (regular.AllowedDays ?? decimal.MaxValue) - await UsedAsync(e.Id, regular, from, excludingId);
            var nth = taken + 1;
            if (available >= 1)
                return (FinanceOperationResult.Ok(), regular, e, new List<Part> { new(from, to, 0, 1, 0) },
                        $"هذه الزمنية رقم {nth} في الشهر (المسموح {policy.FreeHourlyLeavesPerMonth}): تُحسب يومًا من رصيد «{regular.Name}»");
            return (FinanceOperationResult.Ok(), regular, e, new List<Part> { new(from, to, 0, 0, 1) },
                    $"هذه الزمنية رقم {nth} في الشهر ولا رصيد «{regular.Name}» متبقٍ: تُحسب يومًا بدون راتب (يُخصم من الراتب)");
        }

        var type = types.FirstOrDefault(t => t.Id == input.LeaveTypeId);
        if (type is null) return (FinanceOperationResult.Fail("اختر نوع الإجازة"), null, e, none, null);
        if (overlap.Count > 0)
            return (FinanceOperationResult.Fail($"تتداخل مع إجازة {overlap[0].LeaveType.Name} مسجّلة لـ {e.FullName} ({overlap[0].FromDate:yyyy/MM/dd})"), null, e, none, null);

        var rules = await _db.WorkDayExceptions.AsNoTracking().ToListAsync();
        var holidays = await HolidaysAsync(from, to);
        var parts = new List<Part>();
        var warnings = new List<string>();
        var usedInPeriod = new Dictionary<DateTime, decimal>();   // رصيد مستعمل داخل الطلب نفسه (إجازة تمسّ أشهرًا من سنة واحدة)
        foreach (var (pFrom, pTo) in SplitByMonth(from, to))
        {
            decimal work = 0;
            for (var d = pFrom; d <= pTo; d = d.AddDays(1)) if (HrCalendar.IsWorkDay(d, e, rules, holidays)) work++;
            decimal paid, unpaid;
            if (!type.IsPaid) (paid, unpaid) = (0, work);
            else if (type.AllowedDays is null) (paid, unpaid) = (work, 0);
            else
            {
                var key = PeriodOf(type, pFrom).from;
                var used = await UsedAsync(e.Id, type, pFrom, excludingId) + usedInPeriod.GetValueOrDefault(key);
                var available = Math.Max(0, type.AllowedDays.Value - used);
                paid = Math.Min(work, available);
                unpaid = work - paid;
                usedInPeriod[key] = usedInPeriod.GetValueOrDefault(key) + paid;
                if (unpaid > 0)
                    warnings.Add($"رصيد «{type.Name}» في {PeriodName(type, pFrom)}: المتبقي {available:0.##} يوم، فيُسجَّل {unpaid:0.##} يوم بدون راتب (يُخصم من الراتب)");
            }
            parts.Add(new Part(pFrom, pTo, work, paid, unpaid));
        }
        if (parts.Sum(p => p.WorkDays) == 0)
            return (FinanceOperationResult.Fail("الفترة كلها عطلة أسبوعية أو عطل رسمية — لا أيام عمل لتُحسب إجازة"), type, e, parts, null);
        return (FinanceOperationResult.Ok(), type, e, parts, warnings.Count == 0 ? null : string.Join(" — ", warnings));
    }

    /// <summary>معاينة قبل التسجيل (للشاشة): كيف ستُحسب الإجازة.</summary>
    public async Task<(FinanceOperationResult result, LeaveQuote? quote)> QuoteAsync(LeaveInput input)
    {
        var (r, type, _, parts, warning) = await ComputeAsync(input);
        if (!r.Success) return (r, null);
        var (work, paid, unpaid) = (parts.Sum(p => p.WorkDays), parts.Sum(p => p.Paid), parts.Sum(p => p.Unpaid));
        var summary = input.Kind == LeaveKind.Hourly
            ? paid == 0 && unpaid == 0 ? "زمنية ضمن المسموح شهريًا — لا تُخصم من الرصيد" : paid > 0 ? $"تُحسب يومًا من رصيد «{type!.Name}»" : "تُحسب يومًا بدون راتب"
            : $"{work:0.##} يوم عمل: {paid:0.##} من رصيد «{type!.Name}»" + (unpaid > 0 ? $" و{unpaid:0.##} بدون راتب" : "");
        return (r, new LeaveQuote(work, paid, unpaid, summary, warning));
    }

    private async Task<string?> ClosedMonthAsync(DateTime from, DateTime to)
    {
        for (var m = new DateTime(from.Year, from.Month, 1); m <= to; m = m.AddMonths(1))
        {
            var month = m;
            if (await _db.PayrollRuns.AnyAsync(r => r.PeriodMonth == month.Month && r.PeriodYear == month.Year && r.Status == PayrollRunStatus.Approved))
                return $"رواتب {month:MM/yyyy} معتمدة — لا يمكن تسجيل أو إلغاء إجازة فيها";
        }
        // القفل الساري = آخر صف في سجل الإغلاق
        var lockedThrough = await _db.PeriodLocks.OrderByDescending(p => p.Id).Select(p => p.LockedThrough).FirstOrDefaultAsync();
        if (lockedThrough is { } locked && from.Date <= locked.Date)
            return $"الفترة مقفلة حتى {locked:yyyy/MM/dd} — افتحها أولًا من المالية ← إغلاق الفترة";
        return null;
    }

    /// <summary>يسجّل الإجازة (سجل لكل شهر تمسّه) ويحدّث الحضور فورًا.</summary>
    public async Task<(FinanceOperationResult result, List<EmployeeLeave> leaves, string? warning)> RegisterAsync(LeaveInput input, int userId)
    {
        var (r, type, e, parts, warning) = await ComputeAsync(input);
        if (!r.Success) return (r, new(), null);
        if (await ClosedMonthAsync(parts[0].From, parts[^1].To) is { } closed) return (FinanceOperationResult.Fail(closed), new(), null);

        await using var tx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        var created = new List<EmployeeLeave>();
        var seq = await _db.EmployeeLeaves.CountAsync() + 1;
        foreach (var p in parts)
        {
            var leave = new EmployeeLeave
            {
                LeaveNumber = $"LV-{p.From:yyMM}-{seq++:D4}", EmployeeId = e!.Id, LeaveTypeId = type!.Id, Kind = input.Kind,
                FromDate = p.From, ToDate = p.To, FromTime = input.Kind == LeaveKind.Hourly ? input.FromTime : null,
                ToTime = input.Kind == LeaveKind.Hourly ? input.ToTime : null, WorkDays = p.WorkDays, PaidDays = p.Paid, UnpaidDays = p.Unpaid,
                Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim(), CreatedByUserId = userId
            };
            _db.EmployeeLeaves.Add(leave);
            created.Add(leave);
        }
        await _db.SaveChangesAsync();
        foreach (var l in created) await SyncAttendanceAsync(l);
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Insert", "EmployeeLeaves", created[0].Id,
            $"إجازة {(input.Kind == LeaveKind.Hourly ? "زمنية" : type!.Name)} لـ {e!.FullName}: {string.Join("، ", created.Select(l => l.LeaveNumber))}");
        if (tx is not null) await tx.CommitAsync();
        return (FinanceOperationResult.Ok(), created, warning);
    }

    /// <summary>إلغاء إجازة (بسبب): يعود الحضور كما يقتضيه يومه (أيام العمل الماضية بلا بصمة = غياب حتى يُعاد رفع البصمة).</summary>
    public async Task<FinanceOperationResult> VoidAsync(int leaveId, string reason, int userId)
    {
        if (string.IsNullOrWhiteSpace(reason)) return FinanceOperationResult.Fail("سبب الإلغاء إلزامي");
        var leave = await _db.EmployeeLeaves.Include(l => l.Employee).ThenInclude(e => e.Shift).FirstOrDefaultAsync(l => l.Id == leaveId);
        if (leave is null) return FinanceOperationResult.Fail("الإجازة غير موجودة");
        if (leave.IsVoided) return FinanceOperationResult.Fail("الإجازة ملغاة مسبقًا");
        if (await ClosedMonthAsync(leave.FromDate, leave.ToDate) is { } closed) return FinanceOperationResult.Fail(closed);
        leave.IsVoided = true;
        leave.VoidReason = reason.Trim();
        await _db.SaveChangesAsync();
        if (leave.Kind == LeaveKind.Day)
        {
            var rules = await _db.WorkDayExceptions.AsNoTracking().ToListAsync();
            var holidays = await HolidaysAsync(leave.FromDate, leave.ToDate);
            var records = await _db.AttendanceRecords.Where(a => a.EmployeeId == leave.EmployeeId && a.AttendanceDate >= leave.FromDate
                                                                 && a.AttendanceDate <= leave.ToDate && a.Status == AttendanceStatus.ApprovedLeave).ToListAsync();
            foreach (var rec in records)
            {
                if (rec.AttendanceDate.Date > DateTime.Today || !HrCalendar.IsWorkDay(rec.AttendanceDate, leave.Employee, rules, holidays)) _db.AttendanceRecords.Remove(rec);
                else (rec.Status, rec.LateMinutes) = (AttendanceStatus.Absent, 0);
            }
        }
        else await ReclassifyDayAsync(leave.EmployeeId, leave.FromDate);
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Void", "EmployeeLeaves", leave.Id, $"إلغاء الإجازة {leave.LeaveNumber} لـ {leave.Employee.FullName}: {leave.VoidReason}");
        return FinanceOperationResult.Ok();
    }

    /// <summary>
    /// تعديل إجازة = إلغاء القديمة بسبب وتسجيل الجديدة في عملية واحدة (يبقى الأثر في السجل)؛ إن تعذّر التسجيل لا يُلغى شيء.
    /// </summary>
    public async Task<(FinanceOperationResult result, List<EmployeeLeave> leaves, string? warning)> ReplaceAsync(int oldLeaveId, LeaveInput input, string reason, int userId)
    {
        if (string.IsNullOrWhiteSpace(reason)) return (FinanceOperationResult.Fail("اكتب سبب التعديل"), new(), null);
        await using var tx = await _db.Database.BeginTransactionAsync();
        var voided = await VoidAsync(oldLeaveId, "تعديل: " + reason.Trim(), userId);
        if (!voided.Success) return (voided, new(), null);
        var (r, leaves, warning) = await RegisterAsync(input, userId);
        if (!r.Success) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return (r, new(), null); }
        await tx.CommitAsync();
        return (r, leaves, warning);
    }

    /// <summary>يُطبّق الإجازة على الحضور: أيام العمل «إجازة معتمدة»، والزمنية تُعيد حساب تأخير يومها.</summary>
    private async Task SyncAttendanceAsync(EmployeeLeave leave)
    {
        if (leave.Kind == LeaveKind.Hourly) { await ReclassifyDayAsync(leave.EmployeeId, leave.FromDate); return; }
        var e = await _db.Employees.AsNoTracking().FirstAsync(x => x.Id == leave.EmployeeId);
        var rules = await _db.WorkDayExceptions.AsNoTracking().ToListAsync();
        var holidays = await HolidaysAsync(leave.FromDate, leave.ToDate);
        var existing = await _db.AttendanceRecords.Where(a => a.EmployeeId == leave.EmployeeId && a.AttendanceDate >= leave.FromDate && a.AttendanceDate <= leave.ToDate)
                                .ToDictionaryAsync(a => a.AttendanceDate.Date);
        for (var d = leave.FromDate.Date; d <= leave.ToDate.Date; d = d.AddDays(1))
        {
            if (!HrCalendar.IsWorkDay(d, e, rules, holidays)) continue;
            if (!existing.TryGetValue(d, out var rec))
                _db.AttendanceRecords.Add(rec = new AttendanceRecord { EmployeeId = leave.EmployeeId, AttendanceDate = d });
            (rec.Status, rec.LateMinutes, rec.CheckInTime, rec.CheckOutTime) = (AttendanceStatus.ApprovedLeave, 0, null, null);
        }
    }

    /// <summary>
    /// بعد إضافة أو تعديل استثناء شفت: يعيد حساب التأخير لأيام الموظف المسجّلة في المدة
    /// (عدا أشهر رواتبها معتمدة). يعيد عدد الأيام التي أُعيد حسابها.
    /// </summary>
    public async Task<int> ReclassifyRangeAsync(int employeeId, DateTime from, DateTime to)
    {
        var days = await _db.AttendanceRecords.AsNoTracking()
            .Where(a => a.EmployeeId == employeeId && a.AttendanceDate >= from.Date && a.AttendanceDate <= to.Date && a.CheckInTime != null)
            .Select(a => a.AttendanceDate).ToListAsync();
        var approved = (await _db.PayrollRuns.AsNoTracking().Where(r => r.Status == PayrollRunStatus.Approved).Select(r => new { r.PeriodMonth, r.PeriodYear }).ToListAsync())
            .Select(r => (r.PeriodMonth, r.PeriodYear)).ToHashSet();
        var n = 0;
        foreach (var d in days.Where(d => !approved.Contains((d.Month, d.Year))))
        {
            await ReclassifyDayAsync(employeeId, d);
            n++;
        }
        await _db.SaveChangesAsync();
        return n;
    }

    /// <summary>يعيد حساب حالة يوم موظف (حاضر/متأخر) من وقت دخوله وشفته بعد زمنياته السارية.</summary>
    private async Task ReclassifyDayAsync(int employeeId, DateTime day)
    {
        var rec = await _db.AttendanceRecords.FirstOrDefaultAsync(a => a.EmployeeId == employeeId && a.AttendanceDate == day.Date);
        if (rec is null || rec.CheckInTime is null || rec.Status is AttendanceStatus.ApprovedLeave or AttendanceStatus.Absent) return;
        var own = (await _db.Employees.AsNoTracking().Include(e => e.Shift).FirstAsync(e => e.Id == employeeId)).Shift;
        var shift = (await ShiftResolver.LoadAsync(_db, new[] { employeeId }, day, day)).For(employeeId, own, day);
        var hourly = await HourlyOnAsync(day.Date, new[] { employeeId });
        var (status, late) = HrService.Classify(HrCalendar.EffectiveShift(shift, hourly.GetValueOrDefault(employeeId) ?? new()), null, rec.CheckInTime);
        (rec.Status, rec.LateMinutes) = (status, late);
    }

    /// <summary>الزمنيات السارية في يوم لموظفين محددين.</summary>
    public async Task<Dictionary<int, List<(TimeSpan from, TimeSpan to)>>> HourlyOnAsync(DateTime day, IReadOnlyCollection<int> employeeIds) =>
        (await _db.EmployeeLeaves.AsNoTracking()
            .Where(l => l.Kind == LeaveKind.Hourly && !l.IsVoided && l.FromDate == day.Date && employeeIds.Contains(l.EmployeeId))
            .Select(l => new { l.EmployeeId, From = l.FromTime!.Value, To = l.ToTime!.Value }).ToListAsync())
        .GroupBy(x => x.EmployeeId).ToDictionary(g => g.Key, g => g.Select(x => (x.From, x.To)).ToList());

    /// <summary>الموظفون في إجازة يومية في هذا اليوم (لتسجيلهم «إجازة معتمدة» بدل الغياب).</summary>
    public async Task<HashSet<int>> OnDayLeaveAsync(DateTime day, IReadOnlyCollection<int> employeeIds) =>
        (await _db.EmployeeLeaves.AsNoTracking()
            .Where(l => l.Kind == LeaveKind.Day && !l.IsVoided && l.FromDate <= day.Date && l.ToDate >= day.Date && employeeIds.Contains(l.EmployeeId))
            .Select(l => l.EmployeeId).ToListAsync()).ToHashSet();

    /// <summary>نص الإجازة لكل موظف في يوم (يظهر في الحضور بدل «غائب/متأخر»).</summary>
    public async Task<Dictionary<int, string>> LeaveTextsAsync(DateTime day)
    {
        var rows = await _db.EmployeeLeaves.AsNoTracking().Where(l => !l.IsVoided && l.FromDate <= day.Date && l.ToDate >= day.Date)
            .Select(l => new { l.EmployeeId, l.Kind, Type = l.LeaveType.Name, l.FromTime, l.ToTime }).ToListAsync();
        return rows.GroupBy(r => r.EmployeeId).ToDictionary(g => g.Key, g => string.Join("، ", g.Select(r =>
            r.Kind == LeaveKind.Hourly ? $"زمنية {r.FromTime:hh\\:mm}–{r.ToTime:hh\\:mm}" : $"إجازة {r.Type}")));
    }

    // ============================ القوائم والأرصدة ============================

    public async Task<List<LeaveRow>> ListAsync(DateTime from, DateTime to, int? employeeId = null) =>
        (await _db.EmployeeLeaves.AsNoTracking()
            .Where(l => l.FromDate <= to && l.ToDate >= from && (employeeId == null || l.EmployeeId == employeeId))
            .OrderByDescending(l => l.FromDate).ThenByDescending(l => l.Id)
            .Select(l => new LeaveRow(l.Id, l.LeaveNumber, l.EmployeeId, l.Employee.FullName, l.LeaveType.Name, l.Kind, l.FromDate, l.ToDate,
                                      l.FromTime, l.ToTime, l.WorkDays, l.PaidDays, l.UnpaidDays, l.Note, l.IsVoided, l.VoidReason))
            .ToListAsync());

    /// <summary>أرصدة الموظف في فترة كل نوع (الشهر للاعتيادية، السنة للبقية) + الزمنيات المأخوذة هذا الشهر.</summary>
    public async Task<(List<LeaveBalanceRow> rows, int hourlyTaken, int hourlyFree)> BalancesAsync(int employeeId, DateTime day)
    {
        var rows = new List<LeaveBalanceRow>();
        foreach (var t in await GetTypesAsync(activeOnly: true))
        {
            var used = await UsedAsync(employeeId, t, day);
            decimal? remaining = t.AllowedDays is null ? null : Math.Max(0, t.AllowedDays.Value - used);
            rows.Add(new LeaveBalanceRow(t.Id, t.Name, PeriodName(t, day), t.AllowedDays, used, remaining, t.IsPaid));
        }
        var monthStart = new DateTime(day.Year, day.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var taken = await _db.EmployeeLeaves.CountAsync(l => l.EmployeeId == employeeId && l.Kind == LeaveKind.Hourly && !l.IsVoided
                                                             && l.FromDate >= monthStart && l.FromDate <= monthEnd);
        return (rows, taken, (await GetPolicyAsync()).FreeHourlyLeavesPerMonth);
    }

    /// <summary>أيام بدون راتب لكل موظف في شهر (للرواتب).</summary>
    public async Task<Dictionary<int, decimal>> UnpaidDaysAsync(int month, int year)
    {
        var start = new DateTime(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        return await _db.EmployeeLeaves.AsNoTracking().Where(l => !l.IsVoided && l.UnpaidDays > 0 && l.FromDate >= start && l.FromDate <= end)
            .GroupBy(l => l.EmployeeId).Select(g => new { g.Key, Days = g.Sum(l => l.UnpaidDays) }).ToDictionaryAsync(x => x.Key, x => x.Days);
    }
}
