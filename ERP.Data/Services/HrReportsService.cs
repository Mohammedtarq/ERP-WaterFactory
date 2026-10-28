using System.Globalization;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>طلب تقرير موارد بشرية: الفترة، وقسم أو موظف اختياري.</summary>
public record HrReportRequest(DateTime From, DateTime To, int? DepartmentId = null, int? EmployeeId = null);

/// <summary>
/// تقارير الموارد البشرية: تقرير الموظف (غياب، إجازات، تأخر، زمنيات، عقوبات، قطع راتب، سلف، ترقيات، تقييم، حافز شهري)
/// لقسم أو موظف أو للكل، وسجله التفصيلي يومًا بيوم، والحضور (اليوم أو فترة) والمتأخرون بوقت دخولهم ودقائقهم والغائبون.
/// </summary>
public class HrReportsService
{
    private readonly ProjectDbContext _db;
    public HrReportsService(ProjectDbContext db) => _db = db;

    private static readonly CultureInfo En = CultureInfo.InvariantCulture;
    private static string Money(decimal v) => v.ToString("#,0", En);
    private static string Num(decimal v) => v.ToString("#,0.##", En);
    private static string Time(TimeSpan? t) => t is TimeSpan x ? x.ToString(@"hh\:mm", En) : "—";
    private static string Range(HrReportRequest r) =>
        r.From.Date == r.To.Date ? $"يوم {r.From:yyyy/MM/dd}" : $"من {r.From:yyyy/MM/dd} إلى {r.To:yyyy/MM/dd}";

    private sealed record Emp(int Id, string Name, string Department, string? JobTitle, int? DepartmentId);

    private async Task<List<Emp>> EmployeesAsync(HrReportRequest r)
    {
        var q = _db.Employees.AsNoTracking().Where(e => e.IsActive);
        if (r.DepartmentId is int d) q = q.Where(e => e.DepartmentId == d);
        if (r.EmployeeId is int id) q = _db.Employees.AsNoTracking().Where(e => e.Id == id);
        var depts = await _db.Departments.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name);
        return (await q.Select(e => new { e.Id, e.FullName, e.DepartmentId, e.JobTitle }).ToListAsync())
            .Select(e => new Emp(e.Id, e.FullName, e.DepartmentId is int dep ? depts.GetValueOrDefault(dep, "—") : "بلا قسم", e.JobTitle, e.DepartmentId))
            .OrderBy(e => e.Department).ThenBy(e => e.Name).ToList();
    }

    private static int MonthKey(int year, int month) => year * 12 + month;

    /// <summary>تقرير الموظف للفترة: سطر لكل موظف (أو للموظف المختار) بكل ما يخصه.</summary>
    public async Task<ReportTable> EmployeeSummaryAsync(HrReportRequest r)
    {
        var emps = await EmployeesAsync(r);
        var ids = emps.Select(e => e.Id).ToList();
        var (from, to) = (r.From.Date, r.To.Date);
        var (mFrom, mTo) = (MonthKey(from.Year, from.Month), MonthKey(to.Year, to.Month));

        var att = await _db.AttendanceRecords.AsNoTracking().Where(a => ids.Contains(a.EmployeeId) && a.AttendanceDate >= from && a.AttendanceDate <= to)
            .GroupBy(a => a.EmployeeId)
            .Select(g => new { g.Key, Absent = g.Count(a => a.Status == AttendanceStatus.Absent),
                               Late = g.Count(a => a.LateMinutes > 0), LateMin = g.Sum(a => a.LateMinutes) })
            .ToDictionaryAsync(x => x.Key);
        var leaves = (await _db.EmployeeLeaves.AsNoTracking().Where(l => ids.Contains(l.EmployeeId) && !l.IsVoided && l.FromDate <= to && l.ToDate >= from)
                .Select(l => new { l.EmployeeId, l.Kind, l.WorkDays, l.UnpaidDays, l.FromTime, l.ToTime }).ToListAsync())
            .GroupBy(l => l.EmployeeId).ToDictionary(g => g.Key, g => new
            {
                Days = g.Where(l => l.Kind == LeaveKind.Day).Sum(l => l.WorkDays),
                Hourly = g.Count(l => l.Kind == LeaveKind.Hourly),
                Hours = g.Where(l => l.Kind == LeaveKind.Hourly && l.FromTime != null && l.ToTime != null).Sum(l => (decimal)(l.ToTime!.Value - l.FromTime!.Value).TotalHours),
                Unpaid = g.Sum(l => l.UnpaidDays)
            });
        var deductions = (await _db.EmployeeDeductions.AsNoTracking().Where(d => ids.Contains(d.EmployeeId) && !d.IsVoided && d.EntryDate >= from && d.EntryDate <= to)
                .Select(d => new { d.EmployeeId, d.Kind, d.Amount }).ToListAsync())
            .GroupBy(d => d.EmployeeId).ToDictionary(g => g.Key, g => new
            {
                Penalties = g.Count(d => d.Kind == EmployeeDeductionKind.Penalty), PenaltyAmount = g.Where(d => d.Kind == EmployeeDeductionKind.Penalty).Sum(d => d.Amount),
                Advances = g.Where(d => d.Kind != EmployeeDeductionKind.Penalty).Sum(d => d.Amount)
            });
        var cuts = (await _db.PayrollLines.AsNoTracking().Where(l => ids.Contains(l.EmployeeId)
                        && l.PayrollRun.PeriodYear * 12 + l.PayrollRun.PeriodMonth >= mFrom && l.PayrollRun.PeriodYear * 12 + l.PayrollRun.PeriodMonth <= mTo)
                .Select(l => new { l.EmployeeId, Cut = l.AbsenceDeduction + l.UnpaidLeaveDeduction }).ToListAsync())
            .GroupBy(l => l.EmployeeId).ToDictionary(g => g.Key, g => g.Sum(x => x.Cut));
        var promotions = (await _db.PromotionsAndRaises.AsNoTracking().Where(p => ids.Contains(p.EmployeeId) && p.EffectiveDate >= from && p.EffectiveDate <= to)
                .Select(p => p.EmployeeId).ToListAsync())
            .GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var evals = (await _db.MonthlyIncentiveEvaluations.AsNoTracking().Where(e => ids.Contains(e.EmployeeId)
                        && e.PeriodYear * 12 + e.PeriodMonth >= mFrom && e.PeriodYear * 12 + e.PeriodMonth <= mTo)
                .Select(e => new { e.EmployeeId, e.TotalScore, e.IncentiveAmount }).ToListAsync())
            .GroupBy(e => e.EmployeeId).ToDictionary(g => g.Key, g => (Score: g.Average(e => e.TotalScore), Amount: g.Sum(e => e.IncentiveAmount)));

        var t = new ReportTable
        {
            Title = r.EmployeeId is null ? "تقرير الموظفين" : "تقرير الموظف",
            Subtitle = Range(r),
            Notes = "الغياب والتأخر من الحضور، والإجازات والزمنيات من سجل الإجازات (غير الملغاة)، وقطع الراتب = خصم الغياب والإجازات بلا راتب في رواتب أشهر الفترة، والسلف تشمل المسحوبات."
        };
        t.Columns.AddRange(new[] { "الموظف", "القسم", "غياب (يوم)", "إجازات (يوم)", "تأخر (مرة)", "دقائق التأخر", "زمنيات", "ساعات الزمنيات",
                                   "عقوبات", "مبلغ العقوبات", "قطع الراتب", "السلف والمسحوب", "ترقيات", "التقييم", "الحافز الشهري" });
        for (var i = 2; i < t.Columns.Count; i++) t.NumericColumns.Add(i);
        decimal sAbsent = 0, sLate = 0, sPen = 0, sCut = 0, sAdv = 0, sInc = 0;
        foreach (var e in emps)
        {
            var a = att.GetValueOrDefault(e.Id);
            var l = leaves.GetValueOrDefault(e.Id);
            var d = deductions.GetValueOrDefault(e.Id);
            var ev = evals.TryGetValue(e.Id, out var x) ? x : ((decimal Score, decimal Amount)?)null;
            var cut = cuts.GetValueOrDefault(e.Id);
            sAbsent += a?.Absent ?? 0; sLate += a?.Late ?? 0; sPen += d?.PenaltyAmount ?? 0; sCut += cut; sAdv += d?.Advances ?? 0; sInc += ev?.Amount ?? 0;
            t.Rows.Add(new[]
            {
                e.Name, e.Department, Num(a?.Absent ?? 0), Num(l?.Days ?? 0), Num(a?.Late ?? 0), Num(a?.LateMin ?? 0), Num(l?.Hourly ?? 0), Num(Math.Round(l?.Hours ?? 0, 2)),
                Num(d?.Penalties ?? 0), Money(d?.PenaltyAmount ?? 0), Money(cut), Money(d?.Advances ?? 0), Num(promotions.GetValueOrDefault(e.Id)),
                ev is null ? "—" : Num(Math.Round(ev.Value.Score, 1)), Money(ev?.Amount ?? 0)
            });
        }
        t.Totals.Add(("عدد الموظفين", emps.Count.ToString()));
        t.Totals.Add(("أيام الغياب", Num(sAbsent)));
        t.Totals.Add(("مرات التأخر", Num(sLate)));
        t.Totals.Add(("العقوبات", Money(sPen)));
        t.Totals.Add(("قطع الرواتب", Money(sCut)));
        t.Totals.Add(("السلف والمسحوب", Money(sAdv)));
        t.Totals.Add(("الحوافز الشهرية", Money(sInc)));
        return t;
    }

    /// <summary>سجل الموظف التفصيلي: كل حدث بتاريخه (غياب، تأخر بوقت الدخول، إجازة، زمنية، عقوبة، سلفة، ترقية، تقييم وحافز).</summary>
    public async Task<ReportTable> EmployeeDetailAsync(HrReportRequest r)
    {
        if (r.EmployeeId is not int id)
            return new ReportTable { Title = "سجل الموظف التفصيلي", Subtitle = "اختر الموظف", Notes = "هذا التقرير لموظف واحد." };
        var emp = (await EmployeesAsync(r)).SingleOrDefault();
        var (from, to) = (r.From.Date, r.To.Date);
        var (mFrom, mTo) = (MonthKey(from.Year, from.Month), MonthKey(to.Year, to.Month));
        var events = new List<(DateTime Date, string Type, string Detail, string Amount)>();

        foreach (var a in await _db.AttendanceRecords.AsNoTracking().Where(a => a.EmployeeId == id && a.AttendanceDate >= from && a.AttendanceDate <= to
                                                                            && (a.Status == AttendanceStatus.Absent || a.LateMinutes > 0)).ToListAsync())
            events.Add(a.Status == AttendanceStatus.Absent
                ? (a.AttendanceDate, "غياب", "لم يحضر", "")
                : (a.AttendanceDate, "تأخر", $"دخول {Time(a.CheckInTime)} — تأخر {a.LateMinutes} دقيقة", ""));
        foreach (var l in await _db.EmployeeLeaves.AsNoTracking().Include(l => l.LeaveType)
                     .Where(l => l.EmployeeId == id && !l.IsVoided && l.FromDate <= to && l.ToDate >= from).ToListAsync())
            events.Add(l.Kind == LeaveKind.Hourly
                ? (l.FromDate, "إجازة زمنية", $"{l.LeaveType.Name}: {Time(l.FromTime)}–{Time(l.ToTime)}", "")
                : (l.FromDate, "إجازة", $"{l.LeaveType.Name}: {l.FromDate:MM/dd}–{l.ToDate:MM/dd} ({Num(l.WorkDays)} يوم{(l.UnpaidDays > 0 ? $"، منها {Num(l.UnpaidDays)} بدون راتب" : "")})", ""));
        foreach (var d in await _db.EmployeeDeductions.AsNoTracking().Where(d => d.EmployeeId == id && !d.IsVoided && d.EntryDate >= from && d.EntryDate <= to).ToListAsync())
            events.Add((d.EntryDate, d.Kind switch { EmployeeDeductionKind.Penalty => "عقوبة", EmployeeDeductionKind.Loan => "سلفة", _ => "مسحوب" },
                        $"{d.DeductionNumber} {d.Reason}".Trim(), Money(d.Amount)));
        foreach (var p in await _db.PromotionsAndRaises.AsNoTracking().Where(p => p.EmployeeId == id && p.EffectiveDate >= from && p.EffectiveDate <= to).ToListAsync())
            events.Add((p.EffectiveDate, p.MovementType switch { PromotionMovementType.Promotion => "ترقية", PromotionMovementType.AnnualRaise => "علاوة سنوية", _ => "مكافأة سنوية" },
                        p.NewJobTitle is null ? p.Notes ?? "" : $"إلى {p.NewJobTitle}", Money(p.Amount)));
        foreach (var e in await _db.MonthlyIncentiveEvaluations.AsNoTracking().Where(e => e.EmployeeId == id
                     && e.PeriodYear * 12 + e.PeriodMonth >= mFrom && e.PeriodYear * 12 + e.PeriodMonth <= mTo).ToListAsync())
            events.Add((new DateTime(e.PeriodYear, e.PeriodMonth, 1), "تقييم وحافز شهري",
                        $"الحضور {Num(e.AttendanceScoreAuto)} — الأداء {Num(e.PerformanceScoreManual)} — المهارات {Num(e.SkillsScoreManual)} = {Num(e.TotalScore)}", Money(e.IncentiveAmount)));
        foreach (var c in await _db.PayrollLines.AsNoTracking().Where(l => l.EmployeeId == id && l.AbsenceDeduction + l.UnpaidLeaveDeduction > 0
                     && l.PayrollRun.PeriodYear * 12 + l.PayrollRun.PeriodMonth >= mFrom && l.PayrollRun.PeriodYear * 12 + l.PayrollRun.PeriodMonth <= mTo)
                     .Select(l => new { l.PayrollRun.PeriodYear, l.PayrollRun.PeriodMonth, l.AbsenceDeduction, l.UnpaidLeaveDeduction }).ToListAsync())
            events.Add((new DateTime(c.PeriodYear, c.PeriodMonth, 1), "قطع راتب", $"راتب {c.PeriodMonth}/{c.PeriodYear}: غياب {Money(c.AbsenceDeduction)} + إجازات بدون راتب {Money(c.UnpaidLeaveDeduction)}",
                        Money(c.AbsenceDeduction + c.UnpaidLeaveDeduction)));

        var t = new ReportTable
        {
            Title = "سجل الموظف التفصيلي",
            Subtitle = $"{emp?.Name} — {emp?.Department}{(emp?.JobTitle is { } j ? $" — {j}" : "")} — {Range(r)}"
        };
        t.Columns.AddRange(new[] { "التاريخ", "النوع", "التفصيل", "المبلغ" });
        t.NumericColumns.Add(3);
        foreach (var e in events.OrderBy(e => e.Date).ThenBy(e => e.Type))
            t.Rows.Add(new[] { e.Date.ToString("yyyy/MM/dd", En), e.Type, e.Detail, e.Amount });
        foreach (var g in events.GroupBy(e => e.Type).OrderBy(g => g.Key)) t.Totals.Add((g.Key, g.Count().ToString()));
        return t;
    }

    /// <summary>
    /// الحضور: لكل يوم في الفترة ولكل موظف (قسم أو موظف أو الكل): الحالة، ووقت الدخول والخروج، ودقائق التأخر.
    /// onlyStatus: المتأخرون فقط أو الغائبون فقط.
    /// </summary>
    public async Task<ReportTable> AttendanceAsync(HrReportRequest r, AttendanceStatus? onlyStatus = null)
    {
        var emps = (await EmployeesAsync(r)).ToDictionary(e => e.Id);
        var ids = emps.Keys.ToList();
        var (from, to) = (r.From.Date, r.To.Date);
        var records = await _db.AttendanceRecords.AsNoTracking().Where(a => ids.Contains(a.EmployeeId) && a.AttendanceDate >= from && a.AttendanceDate <= to).ToListAsync();
        var onLeave = (await _db.EmployeeLeaves.AsNoTracking().Where(l => ids.Contains(l.EmployeeId) && !l.IsVoided && l.Kind == LeaveKind.Day && l.FromDate <= to && l.ToDate >= from)
                .Select(l => new { l.EmployeeId, l.FromDate, l.ToDate }).ToListAsync());
        bool Leave(int emp, DateTime day) => onLeave.Any(l => l.EmployeeId == emp && l.FromDate.Date <= day && l.ToDate.Date >= day);
        // الإجازات الزمنية (بالساعات) لكل موظف ويوم: تظهر في عمود خاص
        var hourly = (await _db.EmployeeLeaves.AsNoTracking().Where(l => ids.Contains(l.EmployeeId) && !l.IsVoided && l.Kind == LeaveKind.Hourly && l.FromDate >= from && l.FromDate <= to)
                .Select(l => new { l.EmployeeId, l.FromDate, l.FromTime, l.ToTime }).ToListAsync())
            .GroupBy(l => (l.EmployeeId, l.FromDate.Date))
            .ToDictionary(g => g.Key, g => string.Join("، ", g.OrderBy(l => l.FromTime).Select(l => $"{Time(l.FromTime)}←{Time(l.ToTime)}")));

        var late = onlyStatus == AttendanceStatus.Late;
        var absent = onlyStatus == AttendanceStatus.Absent;
        var t = new ReportTable
        {
            Title = late ? "المتأخرون" : absent ? "الغائبون" : from == to ? "حضور اليوم" : "الحضور",
            Subtitle = Range(r) + (r.DepartmentId is null && r.EmployeeId is null ? " — كل الأقسام" : ""),
            Notes = late ? "المتأخر: من دخل بعد موعد شفته ومهلة السماح." : absent ? "الغائب: من سُجّل غيابه (لا تشمل الإجازات المسجّلة)." : null
        };
        t.Columns.AddRange(new[] { "التاريخ", "القسم", "الموظف", "الحالة", "الدخول", "الخروج", "دقائق التأخر", "زمنية" });
        t.NumericColumns.Add(6);
        var counts = new Dictionary<string, int>();
        var lateMinutes = 0;
        var hourlyCount = 0;
        foreach (var day in Enumerable.Range(0, (to - from).Days + 1).Select(i => from.AddDays(i)))
            foreach (var e in emps.Values)
            {
                var rec = records.FirstOrDefault(a => a.EmployeeId == e.Id && a.AttendanceDate.Date == day);
                var status = rec is null ? (Leave(e.Id, day) ? "إجازة" : "لم يُسجَّل")
                    : rec.Status switch
                    {
                        AttendanceStatus.Absent => Leave(e.Id, day) ? "إجازة" : "غائب",
                        AttendanceStatus.ApprovedLeave => "إجازة",
                        _ when rec.LateMinutes > 0 => "متأخر",
                        _ => "حاضر"
                    };
                if (late && status != "متأخر") continue;
                if (absent && status != "غائب") continue;
                counts[status] = counts.GetValueOrDefault(status) + 1;
                lateMinutes += status == "متأخر" ? rec!.LateMinutes : 0;
                var hourlyText = hourly.GetValueOrDefault((e.Id, day)) ?? "";
                if (hourlyText.Length > 0) hourlyCount++;
                t.Rows.Add(new[] { day.ToString("yyyy/MM/dd", En), e.Department, e.Name, status, Time(rec?.CheckInTime), Time(rec?.CheckOutTime),
                                   rec is { LateMinutes: > 0 } ? rec.LateMinutes.ToString(En) : "", hourlyText });
            }
        foreach (var s in new[] { "حاضر", "متأخر", "غائب", "إجازة", "لم يُسجَّل" })
            if (counts.TryGetValue(s, out var n)) t.Totals.Add((s, n.ToString()));
        if (hourlyCount > 0) t.Totals.Add(("زمنية", hourlyCount.ToString()));
        if (lateMinutes > 0) t.Totals.Add(("مجموع دقائق التأخر", lateMinutes.ToString("#,0", En)));
        return t;
    }
}
