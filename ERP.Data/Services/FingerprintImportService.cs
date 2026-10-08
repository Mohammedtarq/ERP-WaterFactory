using System.Globalization;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>بصمة واحدة من ملف الجهاز: رقم الموظف في الجهاز ووقت البصمة.</summary>
public record FingerprintPunch(string Code, DateTime Time);

/// <summary>ما سيُسجَّل لموظف في يوم من البصمات.</summary>
public record FingerprintDay(int EmployeeId, DateTime Date, TimeSpan? CheckIn, TimeSpan? CheckOut, int Punches, bool MissingCheckOut);

public class FingerprintEmployeeSummary
{
    public int EmployeeId { get; init; }
    public string FullName { get; init; } = "";
    public string Code { get; init; } = "";
    public string? ShiftName { get; init; }
    public int PresentDays { get; set; }
    public int LateDays { get; set; }
    public int AbsentDays { get; set; }
    public int MissingCheckOut { get; set; }
    public int KeptLeaves { get; set; }
}

public class FingerprintUnmapped
{
    public string Code { get; init; } = "";
    public int Punches { get; init; }
    public int Days { get; init; }
    public DateTime First { get; init; }
    public DateTime Last { get; init; }
}

/// <summary>نتيجة قراءة الملف ومقارنته بالموظفين (معاينة)، أو نتيجة التطبيق.</summary>
public class FingerprintPreview
{
    public DateTime PeriodFrom { get; init; }
    public DateTime PeriodTo { get; init; }
    public int Punches { get; init; }
    public int Duplicates { get; init; }
    public int SkippedLines { get; init; }
    public List<FingerprintEmployeeSummary> Employees { get; init; } = new();
    public List<FingerprintUnmapped> Unmapped { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    /// <summary>الأيام التي ستُسجَّل (حضور/تأخير بالبصمة، أو غياب ليوم عمل بلا بصمة).</summary>
    public List<(int EmployeeId, DateTime Date, AttendanceInput Input)> Plan { get; init; } = new();
}

/// <summary>
/// استيراد ملف بصمة ZKTeco (attlog.dat): سطر لكل بصمة «الرقم ⇥ التاريخ والوقت ⇥ ...».
/// - البصمات المكررة خلال دقيقتين تُدمج.
/// - أول بصمة في اليوم = الدخول، وآخر بصمة = الخروج (إن ابتعدت 30 دقيقة على الأقل).
/// - الشفت الليلي: بصمات ما بعد منتصف الليل حتى 4 ساعات بعد نهاية الشفت تُحسب ليوم بداية الشفت.
/// - شفت «دخول فقط» (المبيعات): الدخول يكفي.
/// - يوم عمل بلا بصمة = غياب؛ أيام العطلة الأسبوعية لا تُسجَّل؛ الإجازة المعتمدة المسجّلة يدويًا تبقى.
/// - المعفى من البصمة والعامل الوقتي لا يُستورد لهما شيء.
/// الحالة (حاضر/متأخر) تُحسب كما في الإدخال اليدوي، من وقت الدخول وشفت الموظف.
/// </summary>
public class FingerprintImportService
{
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MinShiftForCheckOut = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan OvernightTail = TimeSpan.FromHours(4);

    private readonly ProjectDbContext _db;
    public FingerprintImportService(ProjectDbContext db) => _db = db;

    /// <summary>يقرأ نص الملف. يقبل الفاصل ⇥ أو المسافات أو الفاصلة، ويتجاهل الأسطر غير المفهومة (ويعدّها).</summary>
    public static (List<FingerprintPunch> punches, int skipped) Parse(string text)
    {
        var list = new List<FingerprintPunch>();
        var skipped = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(new[] { '\t', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToArray();
            if (parts.Length < 2)
            {
                // بعض الأجهزة تفصل بمسافات: الرقم ثم التاريخ ثم الوقت
                var sp = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                parts = sp.Length >= 3 ? new[] { sp[0], sp[1] + " " + sp[2] } : parts;
            }
            if (parts.Length >= 2 && parts[0].Length is > 0 and <= 20
                && DateTime.TryParseExact(parts[1], new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm" },
                                          CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                list.Add(new FingerprintPunch(parts[0], t));
            else
                skipped++;
        }
        return (list, skipped);
    }

    /// <summary>هل اليوم عطلة أسبوعية لهذا الموظف؟ من «استثناءات أيام العمل»، والافتراضي الجمعة إن لم يُعرَّف شيء.</summary>
    private static bool IsOffDay(DateTime day, Employee e, List<WorkDayException> rules)
    {
        var name = day.DayOfWeek.ToString();
        if (rules.Count == 0) return day.DayOfWeek == DayOfWeek.Friday;
        return rules.Any(r => r.IsActive && string.Equals(r.ExceptionDay, name, StringComparison.OrdinalIgnoreCase)
                              && (r.DepartmentId == null || r.DepartmentId == e.DepartmentId)
                              && r.StartDate.Date <= day && (r.EndDate == null || r.EndDate.Value.Date >= day));
    }

    public async Task<FingerprintPreview> PreviewAsync(IReadOnlyList<FingerprintPunch> punches, int skippedLines = 0)
    {
        var warnings = new List<string>();
        if (punches.Count == 0)
            return new FingerprintPreview { Warnings = { "لا توجد بصمات مفهومة في الملف — تأكد أنه ملف الحضور (attlog) من جهاز البصمة" }, SkippedLines = skippedLines };

        // 1) دمج المكرر خلال دقيقتين
        var distinct = new List<FingerprintPunch>();
        var duplicates = 0;
        foreach (var g in punches.GroupBy(p => p.Code))
        {
            DateTime? last = null;
            foreach (var p in g.OrderBy(p => p.Time))
            {
                if (last is { } l && p.Time - l < DuplicateWindow) { duplicates++; continue; }
                distinct.Add(p);
                last = p.Time;
            }
        }

        var employees = await _db.Employees.AsNoTracking().Include(e => e.Shift)
            .Where(e => e.FingerprintCode != null).ToListAsync();
        var byCode = employees.ToDictionary(e => e.FingerprintCode!.Trim(), StringComparer.OrdinalIgnoreCase);
        var rules = await _db.WorkDayExceptions.AsNoTracking().ToListAsync();

        // فترة الملف: من أول يوم إلى آخر يوم (بعد نسب بصمات ما بعد منتصف الليل لأيامها)
        DateTime WorkDate(FingerprintPunch p, Employee? e)
        {
            var shift = e?.Shift;
            if (shift is { IsOvernight: true } && p.Time.TimeOfDay < shift.CheckOutTime + OvernightTail) return p.Time.Date.AddDays(-1);
            return p.Time.Date;
        }

        var unmapped = distinct.Where(p => !byCode.ContainsKey(p.Code.Trim())).GroupBy(p => p.Code.Trim())
            .Select(g => new FingerprintUnmapped { Code = g.Key, Punches = g.Count(), Days = g.Select(p => p.Time.Date).Distinct().Count(), First = g.Min(p => p.Time), Last = g.Max(p => p.Time) })
            .OrderBy(u => u.Code.Length).ThenBy(u => u.Code).ToList();

        var mapped = distinct.Where(p => byCode.ContainsKey(p.Code.Trim()))
            .Select(p => (Punch: p, Emp: byCode[p.Code.Trim()])).ToList();
        var from = distinct.Min(p => p.Time).Date;
        var to = distinct.Max(p => p.Time).Date;
        if (to > DateTime.Today) { warnings.Add($"في الملف بصمات بتاريخ لاحق لليوم ({to:yyyy/MM/dd}) — تحقّق من ساعة الجهاز"); to = DateTime.Today; }

        var existing = await _db.AttendanceRecords.AsNoTracking()
            .Where(a => a.AttendanceDate >= from && a.AttendanceDate <= to).ToListAsync();
        var leaves = existing.Where(a => a.Status == AttendanceStatus.ApprovedLeave).Select(a => (a.EmployeeId, a.AttendanceDate.Date)).ToHashSet();

        var summaries = new List<FingerprintEmployeeSummary>();
        var plan = new List<(int, DateTime, AttendanceInput)>();
        foreach (var e in employees.Where(e => e.IsActive).OrderBy(e => e.FullName))
        {
            var s = new FingerprintEmployeeSummary { EmployeeId = e.Id, FullName = e.FullName, Code = e.FingerprintCode!, ShiftName = e.Shift?.Name };
            if (e.IsTemporary || e.AttendanceExempt)
            {
                if (mapped.Any(m => m.Emp.Id == e.Id))
                    warnings.Add($"{e.FullName}: {(e.IsTemporary ? "عامل وقتي" : "معفى من البصمة")} — بصماته لا تُستورد");
                continue;
            }
            var days = mapped.Where(m => m.Emp.Id == e.Id).GroupBy(m => WorkDate(m.Punch, e))
                             .ToDictionary(g => g.Key, g => g.Select(m => m.Punch.Time).OrderBy(t => t).ToList());
            for (var day = from; day <= to; day = day.AddDays(1))
            {
                if (e.HireDate is { } hire && day < hire.Date) continue;
                if (leaves.Contains((e.Id, day))) { s.KeptLeaves++; continue; }
                if (days.TryGetValue(day, out var times))
                {
                    var checkIn = times[0];
                    var lastPunch = times[^1];
                    TimeSpan? checkOut = null;
                    var entryOnly = e.Shift?.IsEntryOnly == true;
                    if (!entryOnly && lastPunch - checkIn >= MinShiftForCheckOut) checkOut = lastPunch.TimeOfDay;
                    if (!entryOnly && checkOut is null) s.MissingCheckOut++;
                    var (status, _) = HrService.Classify(e.Shift, null, checkIn.TimeOfDay);
                    if (status == AttendanceStatus.Late) s.LateDays++; else s.PresentDays++;
                    plan.Add((e.Id, day, new AttendanceInput(e.Id, null, checkIn.TimeOfDay, checkOut)));
                }
                else if (!IsOffDay(day, e, rules))
                {
                    s.AbsentDays++;
                    plan.Add((e.Id, day, new AttendanceInput(e.Id, null, null, null)));
                }
            }
            summaries.Add(s);
        }
        if (unmapped.Count > 0)
            warnings.Add($"{unmapped.Count} رقم في الجهاز غير مربوط بأي موظف — اربطه من القائمة أو من بطاقة الموظف، وإلا لا يُستورد");
        var noCode = await _db.Employees.CountAsync(e => e.IsActive && !e.IsTemporary && !e.AttendanceExempt && e.FingerprintCode == null);
        if (noCode > 0) warnings.Add($"{noCode} موظف فعّال بلا رقم بصمة في بطاقته — لن يُسجَّل له شيء من الملف");

        return new FingerprintPreview
        {
            PeriodFrom = from, PeriodTo = to, Punches = punches.Count, Duplicates = duplicates, SkippedLines = skippedLines,
            Employees = summaries, Unmapped = unmapped, Warnings = warnings, Plan = plan
        };
    }

    /// <summary>يربط رقمًا من الجهاز بموظف (الرقم فريد).</summary>
    public async Task<FinanceOperationResult> LinkAsync(string code, int employeeId, int userId)
    {
        code = code.Trim();
        if (code.Length == 0) return FinanceOperationResult.Fail("رقم البصمة فارغ");
        if (await _db.Employees.AnyAsync(e => e.FingerprintCode == code && e.Id != employeeId))
            return FinanceOperationResult.Fail($"الرقم {code} مربوط بموظف آخر");
        var e = await _db.Employees.FindAsync(employeeId);
        if (e is null) return FinanceOperationResult.Fail("الموظف غير موجود");
        e.FingerprintCode = code;
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Update", "Employees", e.Id, $"ربط رقم البصمة {code} بـ {e.FullName}");
        return FinanceOperationResult.Ok();
    }

    /// <summary>
    /// يطبّق المعاينة: يسجّل حضور كل يوم بالطريقة نفسها التي يسجّل بها الإدخال اليدوي.
    /// يُرفض إن كان أي شهر في الفترة معتمد الرواتب. كل شيء في معاملة واحدة.
    /// </summary>
    public async Task<(FinanceOperationResult result, int days)> ApplyAsync(FingerprintPreview preview, string fileName, int userId)
    {
        if (preview.Plan.Count == 0) return (FinanceOperationResult.Fail("لا شيء لتسجيله — اربط أرقام البصمة بالموظفين أولًا"), 0);
        for (var m = new DateTime(preview.PeriodFrom.Year, preview.PeriodFrom.Month, 1); m <= preview.PeriodTo; m = m.AddMonths(1))
        {
            var month = m;
            if (await _db.PayrollRuns.AnyAsync(r => r.PeriodMonth == month.Month && r.PeriodYear == month.Year && r.Status == PayrollRunStatus.Approved))
                return (FinanceOperationResult.Fail($"رواتب {month:MM/yyyy} معتمدة — لا يمكن تعديل حضورها"), 0);
        }

        var hr = new HrService(_db);
        await using var tx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        foreach (var day in preview.Plan.GroupBy(p => p.Date).OrderBy(g => g.Key))
        {
            var r = await hr.SaveAttendanceAsync(day.Key, day.Select(p => p.Input).ToList());
            if (!r.Success) return (FinanceOperationResult.Fail($"{day.Key:yyyy/MM/dd}: {r.ErrorMessage}"), 0);
        }
        _db.FingerprintImports.Add(new FingerprintImport
        {
            FileName = fileName.Length > 260 ? fileName[^260..] : fileName, PeriodFrom = preview.PeriodFrom, PeriodTo = preview.PeriodTo,
            Punches = preview.Punches, DaysApplied = preview.Plan.Count, ImportedByUserId = userId,
            UnmappedCodes = Truncate(string.Join("، ", preview.Unmapped.Select(u => u.Code)), 400)
        });
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Post", "FingerprintImports", null,
            $"استيراد بصمة {preview.PeriodFrom:yyyy/MM/dd}–{preview.PeriodTo:yyyy/MM/dd}: {preview.Plan.Count} يوم");
        if (tx is not null) await tx.CommitAsync();
        return (FinanceOperationResult.Ok(), preview.Plan.Count);
    }

    private static string? Truncate(string s, int max) => s.Length == 0 ? null : s.Length > max ? s[..max] : s;
}
