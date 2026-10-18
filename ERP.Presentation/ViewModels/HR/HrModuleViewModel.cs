using System.Collections.ObjectModel;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.HR;

public class HrModuleViewModel : ModuleViewModel
{
    public HrModuleViewModel(AppSession s, IDialogService d)
        : base("الموارد البشرية", Icons.HR, ModuleColors.HR)
    {
        UseDashboard(s, d, ModuleCode.HR, ModuleDashboardViewModel.Hr);
        Attendance = Add(new AttendanceSectionViewModel(s, d));
        Fingerprint = Add(new FingerprintImportSectionViewModel(s, d));
        Leaves = Add(new LeavesSectionViewModel(s, d));
        LeaveSettings = Add(new LeaveSettingsSectionViewModel(s, d));
        Overtime = Add(new OvertimeSectionViewModel(s, d));
        Incentives = Add(new MonthlyIncentiveSectionViewModel(s, d));
        Payroll = Add(new PayrollSectionViewModel(s, d));
        Deductions = Add(new EmployeeDeductionsSectionViewModel(s, d));
        TempWorkers = Add(new TempWorkersSectionViewModel(s, d));
        Add(new PromotionsSectionViewModel(s, d));
        AnnualRaises = Add(new AnnualRaisesSectionViewModel(s, d));
        Add(new EmployeesSectionViewModel(s, d));
        Add(new ShiftsSectionViewModel(s, d));
        Add(new DepartmentsSectionViewModel(s, d));
        Add(new RaiseGradesSectionViewModel(s, d));
        Add(new IncentiveSettingsSectionViewModel(s, d));
        Add(new RepIncentiveRatesSectionViewModel(s, d));
        Add(new ManagerTiersSectionViewModel(s, d));
        Add(new Shell.HrReportsSectionViewModel(s, d));
    }

    public AttendanceSectionViewModel Attendance { get; }
    /// <summary>المستحقون للعلاوة السنوية (يفتحها تنبيه الجرس).</summary>
    public AnnualRaisesSectionViewModel AnnualRaises { get; }
    public FingerprintImportSectionViewModel Fingerprint { get; }
    public LeavesSectionViewModel Leaves { get; }
    public LeaveSettingsSectionViewModel LeaveSettings { get; }
    public OvertimeSectionViewModel Overtime { get; }
    public MonthlyIncentiveSectionViewModel Incentives { get; }
    public PayrollSectionViewModel Payroll { get; }
    public EmployeeDeductionsSectionViewModel Deductions { get; }
    public TempWorkersSectionViewModel TempWorkers { get; }
}

/// <summary>اختيار الشهر/السنة المشترك بين شاشات الحوافز والرواتب.</summary>
public abstract class PeriodSectionViewModel : SectionViewModel
{
    private int _month;
    private int _year;

    protected PeriodSectionViewModel(AppSession s, IDialogService d, string title, string glyph, string color, string description)
        : base(s, d, ModuleCode.HR, title, glyph, color, description)
    {
        var last = DateTime.Today.AddMonths(-1);   // الافتراضي: الشهر المنتهي
        _month = last.Month;
        _year = last.Year;
    }

    public IReadOnlyList<int> Months { get; } = Enumerable.Range(1, 12).ToList();
    public IReadOnlyList<int> Years { get; } = Enumerable.Range(DateTime.Today.Year - 3, 5).ToList();
    public int Month { get => _month; set { if (SetProperty(ref _month, value)) { OnPropertyChanged(nameof(PeriodText)); Background(LoadAsync()); } } }
    public int Year { get => _year; set { if (SetProperty(ref _year, value)) { OnPropertyChanged(nameof(PeriodText)); Background(LoadAsync()); } } }
    public string PeriodText => $"{Month:D2}/{Year}";
}

/// <summary>اختيار القسم في شاشات الموارد البشرية (الكل، قسم بعينه، أو بلا قسم).</summary>
public record DepartmentChoice(int? Id, string Name, bool IsAll = false)
{
    public const string NoDepartment = "بلا قسم";
    public static readonly DepartmentChoice All = new(null, "كل الأقسام", true);

    public bool Matches(int? departmentId) => IsAll || departmentId == Id;

    public static async Task<List<DepartmentChoice>> LoadAsync(ProjectDbContext db) =>
        new[] { All }.Concat((await db.Departments.AsNoTracking().OrderBy(d => d.Name).ToListAsync()).Select(d => new DepartmentChoice(d.Id, d.Name)))
                     .Append(new DepartmentChoice(null, NoDepartment)).ToList();

    /// <summary>يعيد الاختيار السابق بعد إعادة التحميل.</summary>
    public static DepartmentChoice Keep(IEnumerable<DepartmentChoice> options, DepartmentChoice? previous) =>
        options.FirstOrDefault(o => previous is not null && o.IsAll == previous.IsAll && o.Id == previous.Id && o.Name == previous.Name) ?? All;
}

// ============================ الحضور اليومي ============================
public class AttendanceRow : ObservableObject
{
    private Option<AttendanceStatus>? _forced;
    private string _checkIn = "";
    private string _checkOut = "";

    public int EmployeeId { get; init; }
    public string EmployeeName { get; init; } = "";
    public int? DepartmentId { get; init; }
    public string DepartmentName { get; init; } = DepartmentChoice.NoDepartment;
    public string ShiftText { get; init; } = "";
    public Option<AttendanceStatus>? Forced { get => _forced; set { if (SetProperty(ref _forced, value)) OnPropertyChanged(nameof(IsChanged)); } }
    public string CheckIn { get => _checkIn; set { if (SetProperty(ref _checkIn, value ?? "")) OnPropertyChanged(nameof(IsChanged)); } }
    public string CheckOut { get => _checkOut; set { if (SetProperty(ref _checkOut, value ?? "")) OnPropertyChanged(nameof(IsChanged)); } }
    public string SavedStatus { get; set; } = "";
    public int SavedLateMinutes { get; set; }

    // ما حُمّل من القاعدة: يُحفظ ما تغيّر فقط، فلا يُسجَّل غياب لموظف لم يُلمس سطره
    private (AttendanceStatus? Forced, string In, string Out) _original;
    internal void MarkLoaded() => _original = (Forced?.Value, CheckIn, CheckOut);
    public bool IsChanged => (Forced?.Value, CheckIn.Trim(), CheckOut.Trim()) != (_original.Forced, _original.In.Trim(), _original.Out.Trim());
}

public class AttendanceSectionViewModel : SectionViewModel
{
    /// <summary>جدول حضور اليوم يُحرَّر مباشرة: يُحدَّث عند تغيير التاريخ أو الحفظ فقط، حتى لا تضيع أوقات مكتوبة.</summary>
    protected override bool HasPendingInput => true;

    private DateTime _date = DateTime.Today;

    public AttendanceSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.HR, "الحضور اليومي", Icons.Calendar, "#EC4899", "الدخول والخروج والتأخير والغياب لكل موظف")
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        AllPresentCommand = new RelayCommand(() =>
        {
            // الظاهر فقط (القسم/البحث المختار)
            foreach (var r in VisibleRows.Where(r => r.CheckIn.Length == 0 && r.Forced is null)) r.CheckIn = r.ShiftText.Split(' ')[0] is { Length: 5 } t ? t : "08:00";
        });
    }

    /// <summary>"تلقائي" = تُحدَّد الحالة من وقت الدخول والشفت.</summary>
    public IReadOnlyList<Option<AttendanceStatus>?> ForcedOptions { get; } = new Option<AttendanceStatus>?[]
    {
        null,
        new(AttendanceStatus.Absent, ArabicLabels.Of(AttendanceStatus.Absent)),
        new(AttendanceStatus.ApprovedLeave, ArabicLabels.Of(AttendanceStatus.ApprovedLeave)),
    };

    public ObservableCollection<AttendanceRow> Rows { get; } = new();
    public DateTime Date { get => _date; set { if (SetProperty(ref _date, value.Date)) Background(LoadAsync()); } }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand AllPresentCommand { get; }

    // ---- القسم والبحث: إدخال حضور موظف واحد أو قسم واحد دون الباقين ----
    private DepartmentChoice _department = DepartmentChoice.All;
    private string _filter = "";
    public ObservableCollection<DepartmentChoice> Departments { get; } = new();
    public DepartmentChoice Department { get => _department; set { if (SetProperty(ref _department, value ?? DepartmentChoice.All)) OnPropertyChanged(nameof(VisibleRows)); } }
    public string Filter { get => _filter; set { if (SetProperty(ref _filter, value ?? "")) OnPropertyChanged(nameof(VisibleRows)); } }
    /// <summary>قائمة فعلية للجدول القابل للتحرير.</summary>
    public IList<AttendanceRow> VisibleRows => Rows.Where(r => Department.Matches(r.DepartmentId)
        && (Filter.Trim().Length == 0 || r.EmployeeName.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
    public int ChangedCount => Rows.Count(r => r.IsChanged);

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var departments = await DepartmentChoice.LoadAsync(db);
        Departments.Clear();
        foreach (var d in departments) Departments.Add(d);
        _department = DepartmentChoice.Keep(departments, _department);
        OnPropertyChanged(nameof(Department));
        var employees = await db.Employees.AsNoTracking().Include(e => e.Shift).Include(e => e.Department)
            .Where(e => e.IsActive && !e.IsTemporary).OrderBy(e => e.FullName).ToListAsync();
        var records = await db.AttendanceRecords.AsNoTracking().Where(a => a.AttendanceDate == Date).ToDictionaryAsync(a => a.EmployeeId);
        // الإجازة تظهر بدل «غائب/متأخر»: «إجازة مرضية» أو «زمنية 08:00–10:00»
        var leaveTexts = await new HrLeaveService(db).LeaveTextsAsync(Date);
        Rows.Clear();
        foreach (var e in employees)
        {
            records.TryGetValue(e.Id, out var rec);
            var row = new AttendanceRow
            {
                EmployeeId = e.Id,
                EmployeeName = e.FullName,
                DepartmentId = e.DepartmentId, DepartmentName = e.Department?.Name ?? DepartmentChoice.NoDepartment,
                ShiftText = e.Shift is null ? "بلا شفت" : $"{e.Shift.CheckInTime:hh\\:mm} (سماح {e.Shift.CheckInGraceMinutes} د)",
                Forced = rec?.Status is AttendanceStatus.Absent or AttendanceStatus.ApprovedLeave
                    ? ForcedOptions.First(o => o?.Value == rec.Status) : null,
                CheckIn = rec?.CheckInTime?.ToString(@"hh\:mm") ?? "",
                CheckOut = rec?.CheckOutTime?.ToString(@"hh\:mm") ?? "",
                SavedStatus = leaveTexts.TryGetValue(e.Id, out var leave)
                    ? rec is null || rec.Status == AttendanceStatus.ApprovedLeave ? leave : $"{ArabicLabels.Of(rec.Status)} — {leave}"
                    : rec is null ? "غير مسجّل" : ArabicLabels.Of(rec.Status),
                SavedLateMinutes = rec?.LateMinutes ?? 0
            };
            row.MarkLoaded();
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AttendanceRow.IsChanged)) OnPropertyChanged(nameof(ChangedCount)); };
            Rows.Add(row);
        }
        OnPropertyChanged(nameof(VisibleRows));
        OnPropertyChanged(nameof(ChangedCount));
    }

    private static bool TryTime(string text, out TimeSpan? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (TimeSpan.TryParse(text.Trim(), out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1)) { value = t; return true; }
        return false;
    }

    private async Task SaveAsync()
    {
        if (!Require(CanAdd || CanEdit, "تسجيل الحضور")) return;
        var inputs = new List<AttendanceInput>();
        // ما عُدّل فقط: الموظف الذي لم يُلمس سطره لا يُسجَّل له شيء (لا غياب تلقائي)
        foreach (var r in Rows.Where(r => r.IsChanged))
        {
            if (!TryTime(r.CheckIn, out var inT) || !TryTime(r.CheckOut, out var outT))
            {
                Dialogs.Error($"وقت غير صحيح لـ {r.EmployeeName}. اكتب الوقت بصيغة 08:30");
                return;
            }
            inputs.Add(new AttendanceInput(r.EmployeeId, r.Forced?.Value, inT, outT));
        }
        if (inputs.Count == 0) { StatusMessage = "لا تعديلات للحفظ — اكتب وقت الدخول أو اختر حالة لموظف"; return; }
        await using var db = Session.NewDb();
        var message = $"تم حفظ حضور {Date:yyyy/MM/dd} لـ {inputs.Count} موظف";
        if (await RunOperationAsync(() => new HrService(db).SaveAttendanceAsync(Date, inputs), message))
        {
            await LoadAsync();
            StatusMessage = message;
        }
    }
}

// ============================ تقييم الحوافز الشهرية ============================
public class IncentiveRow : ObservableObject
{
    private decimal _performance;
    private decimal _skills;
    private decimal _total;
    private decimal _amount;

    public int EmployeeId { get; init; }
    public string EmployeeName { get; init; } = "";
    public int? DepartmentId { get; init; }
    public string DepartmentName { get; init; } = DepartmentChoice.NoDepartment;
    public decimal AttendanceScore { get; set; }
    public decimal Performance { get => _performance; set => SetProperty(ref _performance, value); }
    public decimal Skills { get => _skills; set => SetProperty(ref _skills, value); }
    public decimal TotalScore { get => _total; set => SetProperty(ref _total, value); }
    public decimal Amount { get => _amount; set => SetProperty(ref _amount, value); }
    public bool IsSaved { get; set; }
}

public class MonthlyIncentiveSectionViewModel : PeriodSectionViewModel
{
    private string _weightsText = "";

    public MonthlyIncentiveSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, "تقييم الحوافز الشهرية", Icons.Star, "#F59E0B", "الانضباط تلقائي من الحضور + الأداء والمهارات من المدير")
    {
        SaveAllCommand = new AsyncRelayCommand(SaveAllAsync);
    }

    protected override bool ReloadOnActivate => true;
    public ObservableCollection<IncentiveRow> Rows { get; } = new();
    public string WeightsText { get => _weightsText; private set => SetProperty(ref _weightsText, value); }
    public decimal TotalAmount => VisibleRows.Sum(r => r.Amount);

    private DepartmentChoice _department = DepartmentChoice.All;
    public ObservableCollection<DepartmentChoice> Departments { get; } = new();
    public DepartmentChoice Department
    {
        get => _department;
        set { if (SetProperty(ref _department, value ?? DepartmentChoice.All)) { OnPropertyChanged(nameof(VisibleRows)); OnPropertyChanged(nameof(TotalAmount)); } }
    }
    /// <summary>قائمة فعلية للجدول القابل للتحرير.</summary>
    public IList<IncentiveRow> VisibleRows => Rows.Where(r => Department.Matches(r.DepartmentId)).ToList();
    public AsyncRelayCommand SaveAllCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var hr = new HrService(db);
        var w = await hr.GetWeightsAsync();
        WeightsText = $"المعادلة: (الانضباط × {w.AttendanceWeight:0.##} + الأداء × {w.PerformanceWeight:0.##} + المهارات × {w.SkillsWeight:0.##}) ÷ 100 ← مبلغ حسب شريحة المقياس";

        var departments = await DepartmentChoice.LoadAsync(db);
        Departments.Clear();
        foreach (var d in departments) Departments.Add(d);
        _department = DepartmentChoice.Keep(departments, _department);
        OnPropertyChanged(nameof(Department));
        var employees = await db.Employees.AsNoTracking().Include(e => e.Department).Where(e => e.IsActive && !e.IsTemporary).OrderBy(e => e.FullName).ToListAsync();
        var saved = await db.MonthlyIncentiveEvaluations.AsNoTracking()
            .Where(e => e.PeriodMonth == Month && e.PeriodYear == Year).ToDictionaryAsync(e => e.EmployeeId);
        Rows.Clear();
        foreach (var e in employees)
        {
            saved.TryGetValue(e.Id, out var ev);
            Rows.Add(new IncentiveRow
            {
                EmployeeId = e.Id, EmployeeName = e.FullName,
                DepartmentId = e.DepartmentId, DepartmentName = e.Department?.Name ?? DepartmentChoice.NoDepartment,
                AttendanceScore = ev?.AttendanceScoreAuto ?? await hr.ComputeAttendanceScoreAsync(e.Id, Month, Year),
                Performance = ev?.PerformanceScoreManual ?? 0, Skills = ev?.SkillsScoreManual ?? 0,
                TotalScore = ev?.TotalScore ?? 0, Amount = ev?.IncentiveAmount ?? 0, IsSaved = ev is not null
            });
        }
        OnPropertyChanged(nameof(VisibleRows));
        OnPropertyChanged(nameof(TotalAmount));
    }

    private async Task SaveAllAsync()
    {
        if (!Require(CanEdit || CanAdd, "تقييم الحوافز")) return;
        await using var db = Session.NewDb();
        var hr = new HrService(db);
        // الظاهر فقط (القسم المختار)
        var rows = VisibleRows;
        foreach (var r in rows)
        {
            var (result, b) = await hr.SaveEvaluationAsync(r.EmployeeId, Month, Year, r.Performance, r.Skills);
            if (!result.Success) { Dialogs.Error($"{r.EmployeeName}: {result.ErrorMessage}"); return; }
            r.AttendanceScore = b!.AttendanceScore;
            r.TotalScore = b.TotalScore;
            r.Amount = b.Amount;
        }
        var message = $"تم حفظ تقييم {rows.Count} موظف لشهر {PeriodText}" + (Department.IsAll ? "" : $" — قسم {Department.Name}");
        await LoadAsync();
        StatusMessage = message;
    }
}

// ============================ تشغيل الرواتب ============================
public class PayrollRow
{
    public string EmployeeName { get; init; } = "";
    public int? DepartmentId { get; init; }
    public string DepartmentName { get; init; } = DepartmentChoice.NoDepartment;
    public string Currency { get; init; } = "";
    public decimal BaseSalary { get; init; }
    public decimal Allowances { get; init; }
    public decimal AbsenceDeduction { get; init; }
    public decimal RepIncentive { get; init; }
    public decimal ManagerIncentive { get; init; }
    public decimal MonthlyIncentive { get; init; }
    public decimal LoanDeduction { get; init; }
    public decimal WithdrawalDeduction { get; init; }
    public decimal PenaltyDeduction { get; init; }
    public decimal SocialSecurity { get; init; }
    public decimal SocialSolidarity { get; init; }
    public decimal OvertimeAmount { get; init; }
    public string OvertimeHint { get; init; } = "";
    public decimal UnpaidLeaveDeduction { get; init; }
    public string UnpaidLeaveHint { get; init; } = "";
    public decimal NetSalary { get; init; }

    // تجميع الأعمدة: المستحقات والاستقطاعات مع مجموع لكل مجموعة
    public decimal Incentives => RepIncentive + ManagerIncentive + MonthlyIncentive;
    public string IncentivesHint => Incentives == 0 ? "" : $"المندوب {RepIncentive:N0} · المدير {ManagerIncentive:N0} · الشهري {MonthlyIncentive:N0}";
    public decimal TotalEarnings => BaseSalary + Allowances + OvertimeAmount + Incentives;
    public decimal TotalDeductions => AbsenceDeduction + UnpaidLeaveDeduction + LoanDeduction + WithdrawalDeduction + PenaltyDeduction + SocialSecurity + SocialSolidarity;
}

public class PayrollSectionViewModel : PeriodSectionViewModel
{
    private int? _runId;
    private bool _isApproved;
    private PayrollSummary? _summary;

    public PayrollSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, "تشغيل الرواتب", Icons.Voucher, "#10B981", "توليد الرواتب بالدينار والدولار واعتمادها بقيد تلقائي")
    {
        GenerateCommand = new AsyncRelayCommand(GenerateAsync);
        ApproveCommand = new AsyncRelayCommand(ApproveAsync);
        PrintCommand = new AsyncRelayCommand(() => RunId is int id ? PrintAsync(db => DocumentReports.PayrollAsync(Session, db, id)) : Error("ولّد رواتب الشهر أولًا"));
    }

    protected override bool ReloadOnActivate => true;
    public ObservableCollection<PayrollRow> Rows { get; } = new();

    private DepartmentChoice _department = DepartmentChoice.All;
    public ObservableCollection<DepartmentChoice> Departments { get; } = new();
    public DepartmentChoice Department
    {
        get => _department;
        set { if (SetProperty(ref _department, value ?? DepartmentChoice.All)) { OnPropertyChanged(nameof(VisibleRows)); OnPropertyChanged(nameof(DepartmentTotalText)); } }
    }
    public IList<PayrollRow> VisibleRows => Rows.Where(r => Department.Matches(r.DepartmentId)).ToList();
    /// <summary>صافي رواتب القسم المختار (بعملته).</summary>
    public string DepartmentTotalText => Department.IsAll || Rows.Count == 0 ? ""
        : string.Join(" + ", VisibleRows.GroupBy(r => r.Currency).Select(g => $"{g.Sum(r => r.NetSalary):N0} {g.Key}")) is { Length: > 0 } t ? $"صافي قسم {Department.Name}: {t}" : $"لا موظفين في قسم {Department.Name}";
    public int? RunId { get => _runId; private set { if (SetProperty(ref _runId, value)) OnPropertyChanged(nameof(StatusText)); } }
    public bool IsApproved { get => _isApproved; private set { if (SetProperty(ref _isApproved, value)) OnPropertyChanged(nameof(StatusText)); } }
    public PayrollSummary? Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string StatusText => RunId is null ? "لم تُولَّد رواتب هذا الشهر بعد" : IsApproved ? "معتمدة ✓ (الشهر مقفل)" : "مسودة — راجعها ثم اعتمدها";

    public AsyncRelayCommand GenerateCommand { get; }
    public AsyncRelayCommand ApproveCommand { get; }
    public AsyncRelayCommand PrintCommand { get; }
    private Task Error(string message) { Dialogs.Error(message); return Task.CompletedTask; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var departments = await DepartmentChoice.LoadAsync(db);
        Departments.Clear();
        foreach (var d in departments) Departments.Add(d);
        _department = DepartmentChoice.Keep(departments, _department);
        OnPropertyChanged(nameof(Department));
        var run = await db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(r => r.PeriodMonth == Month && r.PeriodYear == Year);
        RunId = run?.Id;
        IsApproved = run?.Status == PayrollRunStatus.Approved;
        Rows.Clear();
        Summary = null;
        OnPropertyChanged(nameof(VisibleRows));
        OnPropertyChanged(nameof(DepartmentTotalText));
        if (run is null) return;

        var lines = await db.PayrollLines.AsNoTracking().Where(l => l.PayrollRunId == run.Id).Include(l => l.Employee).ThenInclude(e => e.Department)
                            .OrderBy(l => l.Employee.FullName).ToListAsync();
        foreach (var l in lines)
            Rows.Add(new PayrollRow
            {
                EmployeeName = l.Employee.FullName, Currency = l.Currency,
                DepartmentId = l.Employee.DepartmentId, DepartmentName = l.Employee.Department?.Name ?? DepartmentChoice.NoDepartment, BaseSalary = l.BaseSalary, Allowances = l.Allowances,
                AbsenceDeduction = l.AbsenceDeduction, RepIncentive = l.RepIncentiveAmount, ManagerIncentive = l.SalesManagerIncentiveAmount,
                MonthlyIncentive = l.MonthlyIncentiveAmount, LoanDeduction = l.LoanDeduction, WithdrawalDeduction = l.WithdrawalDeduction,
                PenaltyDeduction = l.PenaltyDeduction, SocialSecurity = l.SocialSecurityDeduction, SocialSolidarity = l.SocialSolidarityDeduction,
                OvertimeAmount = l.OvertimeAmount, OvertimeHint = l.OvertimeAmount == 0 ? "" : $"{l.OvertimeDays:0.##} يوم + {l.OvertimeHours:0.##} ساعة",
                UnpaidLeaveDeduction = l.UnpaidLeaveDeduction, UnpaidLeaveHint = l.UnpaidLeaveDays == 0 ? "" : $"{l.UnpaidLeaveDays:0.##} يوم بدون راتب",
                NetSalary = l.NetSalary
            });
        Summary = await new HrService(db).SummarizeAsync(run.Id);
        OnPropertyChanged(nameof(VisibleRows));
        OnPropertyChanged(nameof(DepartmentTotalText));
    }

    private async Task GenerateAsync()
    {
        if (!Require(CanAdd || CanEdit, "توليد الرواتب")) return;
        if (RunId is not null && !Dialogs.Confirm($"توجد مسودة رواتب لشهر {PeriodText}. إعادة التوليد تستبدلها بأحدث بيانات الحضور والحوافز. متابعة؟")) return;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(async () => (await new HrService(db).GenerateAsync(Month, Year)).result,
                                    $"تم توليد رواتب {PeriodText}"))
        {
            await LoadAsync();
            // ساعات إضافية بلا سعر = راتب ناقص بصمت: تنبيه صريح قبل الاعتماد
            var missing = (await new OvertimeService(db).SummaryAsync(Month, Year)).Where(o => o.MissingRate).Select(o => o.EmployeeName).ToList();
            if (missing.Count > 0)
                StatusMessage = $"تنبيه: ساعات إضافية بلا سعر ساعة لـ {string.Join("، ", missing)} — حدّد السعر من «العمل الإضافي» ثم أعد التوليد";
        }
    }

    private async Task ApproveAsync()
    {
        if (!Require(CanPost, "اعتماد الرواتب")) return;
        if (RunId is null) { Dialogs.Error("ولّد الرواتب أولًا"); return; }
        if (!Dialogs.Confirm($"اعتماد رواتب {PeriodText}؟ سيُنشأ قيد الاستحقاق ويُقفل الشهر نهائيًا (حضور، حوافز، رواتب).")) return;
        await using var db = Session.NewDb();
        PayrollSummary? s = null;
        if (await RunOperationAsync(async () => { var (r, sum) = await new HrService(db).ApproveAsync(RunId.Value, Session.UserId); s = sum; return r; },
                                    $"تم اعتماد رواتب {PeriodText}"))
        {
            Dialogs.Info($"تم اعتماد رواتب {PeriodText}\nبالدينار: {s!.TotalNetIqd:N0}\nبالدولار: {s.TotalNetUsd:N2}\nإجمالي القيد: {s.TotalInIqd:N0} د.ع");
            await LoadAsync();
        }
    }
}

// ============================ الترقيات والعلاوات ============================
public class PromotionsSectionViewModel : CrudSectionViewModel<PromotionAndRaise>
{
    public PromotionsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.HR, "الترقيات والعلاوات", Icons.Up, "#8B5CF6", "ترقية، علاوة سنوية دائمة، أو مكافأة لمرة واحدة")
    {
        PrintLetterCommand = new AsyncRelayCommand(p => p is PromotionAndRaise { Id: > 0 } r ? PrintAsync(db => DocumentReports.RaiseLetterAsync(Session, db, r.Id)) : Task.CompletedTask);
    }

    /// <summary>طباعة كتاب منح العلاوة/الترقية للموظف.</summary>
    public AsyncRelayCommand PrintLetterCommand { get; }

    public IReadOnlyList<Option<PromotionMovementType>> MovementOptions { get; } = ArabicLabels.OptionsOf<PromotionMovementType>();
    public IReadOnlyList<Option<PromotionApplicationType>> ApplicationOptions { get; } = ArabicLabels.OptionsOf<PromotionApplicationType>();
    public ObservableCollection<Employee> Employees { get; } = new();

    protected override int GetId(PromotionAndRaise e) => e.Id;
    protected override string Describe(PromotionAndRaise e) => $"{e.Employee?.FullName} — {ArabicLabels.Of(e.MovementType)} {e.Amount:N0}";

    protected override async Task LoadLookupsAsync(ProjectDbContext db)
    {
        Employees.Clear();
        foreach (var e in await db.Employees.AsNoTracking().Where(e => e.IsActive).OrderBy(e => e.FullName).ToListAsync()) Employees.Add(e);
    }

    protected override Task<List<PromotionAndRaise>> QueryAsync(ProjectDbContext db) =>
        db.PromotionsAndRaises.AsNoTracking().Include(p => p.Employee).OrderByDescending(p => p.EffectiveDate).ToListAsync();

    protected override PromotionAndRaise CreateNew() => new()
    {
        EffectiveDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1),
        MovementType = PromotionMovementType.AnnualRaise, ApplicationType = PromotionApplicationType.PermanentAddition
    };

    protected override string? Validate(PromotionAndRaise e)
    {
        if (e.EmployeeId == 0) return "اختر الموظف";
        if (e.Amount < 0) return "المبلغ لا يمكن أن يكون سالبًا";
        if (e.MovementType == PromotionMovementType.Promotion && string.IsNullOrWhiteSpace(e.NewJobTitle)) return "الترقية تتطلب المسمى الوظيفي الجديد";
        return null;
    }

    /// <summary>الترقية تحدّث المسمى الوظيفي للموظف؛ المبلغ نفسه يدخل الرواتب من سجل الحركات (لا يُعدَّل الراتب الأساسي).</summary>
    protected override async Task BeforeSaveAsync(ProjectDbContext db, PromotionAndRaise e)
    {
        if (e.Id == 0) e.CreatedByUserId = Session.UserId;
        if (e.MovementType == PromotionMovementType.Promotion && !string.IsNullOrWhiteSpace(e.NewJobTitle))
            await db.Employees.Where(x => x.Id == e.EmployeeId).ExecuteUpdateAsync(u => u.SetProperty(x => x.JobTitle, e.NewJobTitle.Trim()));
    }
}

// ============================ درجات العلاوة ============================
/// <summary>درجات العلاوة السنوية (A / B / C …) ومبالغها — تحددها الإدارة؛ تعديل المبلغ يسري على العلاوات القادمة فقط.</summary>
public class RaiseGradesSectionViewModel : CrudSectionViewModel<RaiseGrade>
{
    public RaiseGradesSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.HR, "درجات العلاوة", Icons.Layers, "#8B5CF6", "مبلغ العلاوة السنوية لكل درجة (A / B / C)") { }

    protected override int GetId(RaiseGrade e) => e.Id;
    protected override string Describe(RaiseGrade e) => $"{e.Name} — {e.Amount:N0}";
    protected override Task<List<RaiseGrade>> QueryAsync(ProjectDbContext db) => db.RaiseGrades.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
    protected override RaiseGrade CreateNew() => new() { IsActive = true };
    protected override string? Validate(RaiseGrade e) =>
        string.IsNullOrWhiteSpace(e.Name) ? "اكتب اسم الدرجة (مثل A)"
        : e.Amount < 0 ? "المبلغ لا يكون سالبًا"
        : Items.Any(x => x.Id != e.Id && string.Equals(x.Name.Trim(), e.Name.Trim(), StringComparison.OrdinalIgnoreCase)) ? "هذه الدرجة موجودة"
        : null;
    protected override Task BeforeSaveAsync(ProjectDbContext db, RaiseGrade e) { e.Name = e.Name.Trim(); return Task.CompletedTask; }
}

// ============================ الموظفون ============================
public class EmployeesSectionViewModel : CrudSectionViewModel<Employee>
{
    private byte[]? _editorPhoto;
    private bool _photoChanged;

    public EmployeesSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.HR, "الموظفون", Icons.People, "#0EA5E9", "البيانات الأساسية، الشفت، العملة، وعلامة المندوب/مدير المبيعات")
    {
        ChoosePhotoCommand = new RelayCommand(ChoosePhoto);
        RemovePhotoCommand = new RelayCommand(() => SetEditorPhoto(null));
        PrintCardCommand = new AsyncRelayCommand(p => p is Employee { Id: > 0 } e ? PrintAsync(db => DocumentReports.EmployeeCardAsync(Session, db, e.Id)) : Task.CompletedTask);
    }

    // ---- الصورة (تُحفظ مع حفظ النموذج) وبطاقة الموظف ----
    public byte[]? EditorPhoto { get => _editorPhoto; private set { if (SetProperty(ref _editorPhoto, value)) OnPropertyChanged(nameof(HasEditorPhoto)); } }
    public bool HasEditorPhoto => EditorPhoto is { Length: > 0 };
    public string EditorInitials => PhotoService.Initials(Editor?.FullName);
    public RelayCommand ChoosePhotoCommand { get; }
    public RelayCommand RemovePhotoCommand { get; }
    public AsyncRelayCommand PrintCardCommand { get; }

    protected override void OnEditorChanged()
    {
        _photoChanged = false;
        EditorPhoto = Editor?.PhotoPreview;
        OnPropertyChanged(nameof(EditorInitials));
    }

    private void ChoosePhoto()
    {
        if (Editor is null) return;
        var bytes = Dialogs.PickPhoto();
        if (bytes is null) return;
        if (PhotoService.Check(bytes) is { } error) { Dialogs.Error(error); return; }
        SetEditorPhoto(bytes);
    }

    private void SetEditorPhoto(byte[]? bytes)
    {
        if (Editor is null) return;
        EditorPhoto = bytes;
        _photoChanged = true;
    }

    protected override async Task<string?> AfterSaveAsync(Employee e, bool isNew)
    {
        await using var db = Session.NewDb();
        // بلا «شفت خاص»: يأخذ الشفت الافتراضي لقسمه
        if (!e.HasOwnShift && e.DepartmentId is int dept) await AnnualRaiseService.ApplyDepartmentShiftAsync(db, dept);
        if (!_photoChanged) return null;
        var result = await new PhotoService(db).SetEmployeeAsync(e.Id, EditorPhoto, Session.UserId);
        if (!result.Success) return result.ErrorMessage;
        Session.NotifyPhotoChanged();                 // يوزر مربوط بهذا الموظف يأخذ صورته أسفل القائمة
        return null;
    }

    public ObservableCollection<Branch> Branches { get; } = new();
    public ObservableCollection<Department> Departments { get; } = new();
    public ObservableCollection<Shift> Shifts { get; } = new();
    public ObservableCollection<RaiseGrade> RaiseGrades { get; } = new();
    public IReadOnlyList<SalaryCurrency> Currencies { get; } = Enum.GetValues<SalaryCurrency>();

    protected override int GetId(Employee e) => e.Id;
    protected override string Describe(Employee e) => e.FullName;
    protected override bool Matches(Employee e, string t) => base.Matches(e, t) || (e.JobTitle?.Contains(t) ?? false) || (e.Department?.Name.Contains(t) ?? false);

    // ---- عرض واختيار القسم (ملاحظة التجربة 9) ----
    private DepartmentChoice _department = DepartmentChoice.All;
    public ObservableCollection<DepartmentChoice> DepartmentFilters { get; } = new();
    public DepartmentChoice DepartmentFilter { get => _department; set { if (SetProperty(ref _department, value ?? DepartmentChoice.All)) ApplyFilter(); } }
    protected override bool Includes(Employee e) => DepartmentFilter.Matches(e.DepartmentId);

    protected override async Task LoadLookupsAsync(ProjectDbContext db)
    {
        var filters = await DepartmentChoice.LoadAsync(db);
        DepartmentFilters.Clear();
        foreach (var f in filters) DepartmentFilters.Add(f);
        _department = DepartmentChoice.Keep(filters, _department);
        OnPropertyChanged(nameof(DepartmentFilter));
        Branches.Clear();
        foreach (var b in await db.Branches.AsNoTracking().OrderBy(b => b.Name).ToListAsync()) Branches.Add(b);
        Departments.Clear();
        foreach (var x in await db.Departments.AsNoTracking().OrderBy(x => x.Name).ToListAsync()) Departments.Add(x);
        Shifts.Clear();
        foreach (var x in await db.Shifts.AsNoTracking().OrderBy(x => x.Name).ToListAsync()) Shifts.Add(x);
        RaiseGrades.Clear();
        foreach (var x in await db.RaiseGrades.AsNoTracking().Where(g => g.IsActive).OrderBy(g => g.Name).ToListAsync()) RaiseGrades.Add(x);
    }

    protected override async Task<List<Employee>> QueryAsync(ProjectDbContext db)
    {
        var list = await db.Employees.AsNoTracking().Include(e => e.Branch).Include(e => e.Department).Include(e => e.Shift).OrderBy(e => e.FullName).ToListAsync();
        var photos = await new PhotoService(db).AllEmployeesAsync();
        foreach (var e in list) e.PhotoPreview = photos.GetValueOrDefault(e.Id);
        return list;
    }

    protected override Employee CreateNew() => new() { HireDate = DateTime.Today, BranchId = Branches.FirstOrDefault()?.Id, ShiftId = Shifts.FirstOrDefault()?.Id };
    protected override string? Validate(Employee e) =>
        string.IsNullOrWhiteSpace(e.FullName) ? "أدخل اسم الموظف"
        : e.BaseSalary < 0 ? "الراتب لا يمكن أن يكون سالبًا"
        : e.IsTemporary && e.DailyWage is null or <= 0 ? "العامل الوقتي يحتاج أجرًا يوميًا"
        : e.IsTemporary && (e.IsSalesRep || e.IsSalesManager) ? "العامل الوقتي لا يكون مندوبًا أو مدير مبيعات"
        : e.HasSocialSecurity && e.SocialSecurityAmount is null or <= 0 ? "فعّلت الضمان الاجتماعي: أدخل مبلغه الشهري"
        : e.HasSocialSolidarity && e.SocialSolidarityAmount is null or <= 0 ? "فعّلت التكافل الاجتماعي: أدخل مبلغه الشهري"
        : (e.HasSocialSecurity || e.HasSocialSolidarity) && e.IsTemporary ? "العامل الوقتي لا تُستقطع منه رواتب شهرية (ضمان أو تكافل)"
        : e.AnnualRaiseAmount < 0 ? "مبلغ العلاوة لا يكون سالبًا"
        : e.LastRaiseDate > DateTime.Today ? "تاريخ آخر علاوة لا يكون في المستقبل"
        : null;

    protected override async Task BeforeSaveAsync(ProjectDbContext db, Employee e)
    {
        e.FingerprintCode = string.IsNullOrWhiteSpace(e.FingerprintCode) ? null : e.FingerprintCode.Trim();
        // تغيير درجة العلاوة (حالات خاصة بقرار المدير) يُسجَّل: من، متى، من/إلى — ولا يغيّر علاوة سابقة
        if (e.Id != 0)
        {
            var old = await db.Employees.AsNoTracking().Where(x => x.Id == e.Id).Select(x => x.RaiseGradeId).FirstOrDefaultAsync();
            if (old != e.RaiseGradeId)
            {
                string Name(int? id) => id is int g ? RaiseGrades.FirstOrDefault(x => x.Id == g)?.Name ?? $"#{g}" : "بلا درجة";
                await new AuditService(db).LogAsync(Session.UserId, "Update", "Employees", e.Id, $"تغيير درجة العلاوة لـ {e.FullName}: {Name(old)} ← {Name(e.RaiseGradeId)}");
            }
        }
        if (e.FingerprintCode is { } code && await db.Employees.AnyAsync(x => x.FingerprintCode == code && x.Id != e.Id))
            throw new DbUpdateException($"رقم البصمة {code} مربوط بموظف آخر");
    }
}

// ============================ الشفتات والأقسام ============================
public class ShiftsSectionViewModel : CrudSectionViewModel<Shift>
{
    public ShiftsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.HR, "الشفتات", Icons.Clock, "#14B8A6", "أوقات الدخول والخروج وفترات السماح") { }

    protected override int GetId(Shift e) => e.Id;
    protected override string Describe(Shift e) => e.Name;
    protected override Task<List<Shift>> QueryAsync(ProjectDbContext db) => db.Shifts.AsNoTracking().OrderBy(x => x.CheckInTime).ToListAsync();
    protected override Shift CreateNew() => new() { CheckInTime = new TimeSpan(8, 0, 0), CheckOutTime = new TimeSpan(16, 0, 0), CheckInGraceMinutes = 10 };

    protected override string? Validate(Shift e)
    {
        if (string.IsNullOrWhiteSpace(e.Name)) return "أدخل اسم الشفت";
        if (e.CheckInGraceMinutes < 0 || e.CheckOutGraceMinutes < 0) return "فترة السماح لا يمكن أن تكون سالبة";
        return null;
    }
}

public class DepartmentsSectionViewModel : CrudSectionViewModel<Department>
{
    public DepartmentsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.HR, "الأقسام", Icons.Layers, "#6366F1", "أقسام الشركة والشفت الافتراضي لكل قسم") { }

    public ObservableCollection<Shift> Shifts { get; } = new();

    protected override int GetId(Department e) => e.Id;
    protected override string Describe(Department e) => e.Name;
    protected override async Task LoadLookupsAsync(ProjectDbContext db)
    {
        Shifts.Clear();
        foreach (var x in await db.Shifts.AsNoTracking().OrderBy(x => x.CheckInTime).ToListAsync()) Shifts.Add(x);
    }
    protected override Task<List<Department>> QueryAsync(ProjectDbContext db) =>
        db.Departments.AsNoTracking().Include(x => x.DefaultShift).OrderBy(x => x.Name).ToListAsync();
    protected override string? Validate(Department e) =>
        string.IsNullOrWhiteSpace(e.Name) ? "أدخل اسم القسم" : null;

    /// <summary>موظفو القسم بلا «شفت خاص» يأخذون الشفت الافتراضي الجديد.</summary>
    protected override async Task<string?> AfterSaveAsync(Department e, bool isNew)
    {
        await using var db = Session.NewDb();
        await AnnualRaiseService.ApplyDepartmentShiftAsync(db, e.Id);
        return null;
    }
}

// ============================ إعدادات الحافز الشهري ============================
public class IncentiveSettingsSectionViewModel : CrudSectionViewModel<IncentiveScoreToAmountScale>
{
    private decimal _attendanceWeight = 40, _performanceWeight = 30, _skillsWeight = 30;

    public IncentiveSettingsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.HR, "إعدادات الحافز الشهري", Icons.Settings, "#64748B", "أوزان العوامل الثلاثة ومقياس تحويل النقاط لمبلغ")
    {
        SaveWeightsCommand = new AsyncRelayCommand(SaveWeightsAsync);
    }

    public decimal AttendanceWeight { get => _attendanceWeight; set { if (SetProperty(ref _attendanceWeight, value)) OnPropertyChanged(nameof(WeightsTotalText)); } }
    public decimal PerformanceWeight { get => _performanceWeight; set { if (SetProperty(ref _performanceWeight, value)) OnPropertyChanged(nameof(WeightsTotalText)); } }
    public decimal SkillsWeight { get => _skillsWeight; set { if (SetProperty(ref _skillsWeight, value)) OnPropertyChanged(nameof(WeightsTotalText)); } }
    public string WeightsTotalText => AttendanceWeight + PerformanceWeight + SkillsWeight == 100
        ? "المجموع 100 ✓" : $"المجموع {AttendanceWeight + PerformanceWeight + SkillsWeight:0.##} — يجب أن يساوي 100";
    public AsyncRelayCommand SaveWeightsCommand { get; }

    protected override int GetId(IncentiveScoreToAmountScale e) => e.Id;
    protected override string Describe(IncentiveScoreToAmountScale e) => $"{e.MinScore:0.##} – {e.MaxScore:0.##} ← {e.Amount:N0}";

    protected override async Task LoadLookupsAsync(ProjectDbContext db)
    {
        var w = await new HrService(db).GetWeightsAsync();
        (AttendanceWeight, PerformanceWeight, SkillsWeight) = (w.AttendanceWeight, w.PerformanceWeight, w.SkillsWeight);
    }

    protected override Task<List<IncentiveScoreToAmountScale>> QueryAsync(ProjectDbContext db) =>
        db.IncentiveScoreToAmountScale.AsNoTracking().OrderBy(x => x.MinScore).ToListAsync();

    protected override string? Validate(IncentiveScoreToAmountScale e)
    {
        if (e.MinScore < 0 || e.MaxScore > 100 || e.MinScore > e.MaxScore) return "حدود الشريحة يجب أن تكون بين 0 و 100، والحد الأدنى ≤ الأعلى";
        if (e.Amount < 0) return "المبلغ لا يمكن أن يكون سالبًا";
        if (Items.Any(x => x.Id != e.Id && e.MinScore <= x.MaxScore && x.MinScore <= e.MaxScore)) return "هذه الشريحة تتداخل مع شريحة موجودة";
        return null;
    }

    private async Task SaveWeightsAsync()
    {
        if (!Require(CanEdit, "تعديل الإعدادات")) return;
        if (AttendanceWeight + PerformanceWeight + SkillsWeight != 100 || AttendanceWeight < 0 || PerformanceWeight < 0 || SkillsWeight < 0)
        { Dialogs.Error("الأوزان يجب أن تكون موجبة ومجموعها 100"); return; }
        await using var db = Session.NewDb();
        var w = await db.IncentiveScoreWeights.OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (w is null) db.IncentiveScoreWeights.Add(w = new IncentiveScoreWeights());
        (w.AttendanceWeight, w.PerformanceWeight, w.SkillsWeight) = (AttendanceWeight, PerformanceWeight, SkillsWeight);
        await db.SaveChangesAsync();
        StatusMessage = "تم حفظ أوزان الحافز — تسري على التقييمات القادمة";
    }
}

// ============================ حوافز المبيعات ============================
/// <summary>مبلغ حافز المندوب لكل عبوة: الإدارة تحدد مبلغ الشرنك ومبلغ الكارتون لكل منتج.</summary>
/// <summary>سطر حافز قابل للتعديل: يُعلَّم «معدَّل» حتى يُحفظ.</summary>
public class RepIncentiveRateEditRow : ObservableObject
{
    private decimal _rate;

    public RepIncentiveRateEditRow(RepIncentiveRateRow row, bool isNew = false)
    {
        Row = row;
        _rate = row.Rate;
        Original = isNew ? -1 : row.Rate;
    }

    public RepIncentiveRateRow Row { get; }
    public string ItemName => Row.ItemName;
    public string LevelName => Row.LevelName;
    public decimal Pieces => Row.Pieces;
    public decimal Rate { get => _rate; set { if (SetProperty(ref _rate, value)) { OnPropertyChanged(nameof(IsChanged)); OnPropertyChanged(nameof(StateText)); } } }
    private decimal Original { get; }
    public bool IsChanged => Rate != Original;
    public string StateText => Original < 0 ? "جديد — لم يُحفظ" : IsChanged ? "معدَّل — لم يُحفظ" : Rate > 0 ? "مفعّل" : "بلا حافز";
}

public class RepIncentiveRatesSectionViewModel : SectionViewModel
{
    private string _filter = "";
    private Item? _newItem;
    private ItemPackagingLevel? _newLevel;
    private decimal _newRate;

    public RepIncentiveRatesSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.HR, "حافز المندوب", Icons.Reps, "#F97316", "مبلغ لكل شرنك ولكل كارتون — يُحسب على المباع (المحمّل − الراجع) ويُصرف مع الراتب")
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        AddCommand = new RelayCommand(AddRow);
    }

    protected override bool HasPendingInput => Rows.Any(r => r.IsChanged);

    public ObservableCollection<RepIncentiveRateEditRow> Rows { get; } = new();
    public string Filter { get => _filter; set { if (SetProperty(ref _filter, value ?? "")) OnPropertyChanged(nameof(VisibleRows)); } }
    /// <summary>قائمة فعلية للجدول القابل للتحرير.</summary>
    public IList<RepIncentiveRateEditRow> VisibleRows => Filter.Trim().Length == 0 ? Rows
        : Rows.Where(r => r.ItemName.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
    public AsyncRelayCommand SaveCommand { get; }

    // ---- إضافة منتج/عبوة غير ظاهرة في الجدول ----
    public ObservableCollection<Item> Products { get; } = new();
    public ObservableCollection<ItemPackagingLevel> NewLevels { get; } = new();
    public Item? NewItem { get => _newItem; set { if (SetProperty(ref _newItem, value)) Background(LoadLevelsAsync()); } }
    public ItemPackagingLevel? NewLevel { get => _newLevel; set => SetProperty(ref _newLevel, value); }
    public decimal NewRate { get => _newRate; set => SetProperty(ref _newRate, value); }
    public RelayCommand AddCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        Rows.Clear();
        foreach (var r in await new RepIncentiveService(db).RatesAsync()) Rows.Add(new RepIncentiveRateEditRow(r));
        Products.Clear();
        foreach (var i in await db.Items.AsNoTracking().Where(i => i.IsActive && i.SourcingMethod != SourcingMethod.Purchased).OrderBy(i => i.ItemName).ToListAsync())
            Products.Add(i);
        OnPropertyChanged(nameof(VisibleRows));
        StatusMessage = Rows.Count == 0 ? "لا توجد منتجات بعبوات — أضف منتجًا من الأعلى" : $"{Rows.Count(r => r.Rate > 0)} من {Rows.Count} عبوة لها حافز";
    }

    private async Task LoadLevelsAsync()
    {
        NewLevels.Clear();
        if (NewItem is not { } item) return;
        await using var db = Session.NewDb();
        foreach (var l in await db.ItemPackagingLevels.AsNoTracking().Where(l => l.ItemId == item.Id).OrderByDescending(l => l.EquivalentBaseUnits).ToListAsync())
            NewLevels.Add(l);
        NewLevel = NewLevels.FirstOrDefault();
    }

    private void AddRow()
    {
        if (NewItem is null || NewLevel is null) { Dialogs.Error("اختر المنتج والعبوة"); return; }
        if (NewRate < 0) { Dialogs.Error("الحافز لا يكون سالبًا"); return; }
        if (Rows.FirstOrDefault(r => r.Row.PackagingLevelId == NewLevel.Id) is { } existing)
        {
            existing.Rate = NewRate;
            StatusMessage = $"{existing.ItemName} — {existing.LevelName} موجود في الجدول: عُدّل مبلغه، اضغط «حفظ المبالغ»";
        }
        else
        {
            Rows.Add(new RepIncentiveRateEditRow(new RepIncentiveRateRow(NewItem.Id, NewItem.ItemName, NewLevel.Id, NewLevel.LevelName, NewLevel.EquivalentBaseUnits) { Rate = NewRate }, isNew: true));
            StatusMessage = $"أُضيف {NewItem.ItemName} — {NewLevel.LevelName}: اضغط «حفظ المبالغ» لاعتماده";
        }
        NewRate = 0;
        OnPropertyChanged(nameof(VisibleRows));
    }

    private async Task SaveAsync()
    {
        if (!Require(CanEdit || CanAdd, "تعديل حافز المندوب")) return;
        if (Rows.Any(r => r.Rate < 0)) { Dialogs.Error("الحافز لا يكون سالبًا"); return; }
        foreach (var r in Rows) r.Row.Rate = r.Rate;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new RepIncentiveService(db).SaveRatesAsync(Rows.Select(r => r.Row).ToList(), Session.UserId), "تم حفظ مبالغ الحافز — تسري على حساب الشهر"))
        {
            var message = StatusMessage;
            await LoadAsync();
            StatusMessage = message;
        }
    }
}

public class ManagerTiersSectionViewModel : CrudSectionViewModel<SalesManagerIncentiveTier>
{
    public ManagerTiersSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.HR, "حافز مدير المبيعات", Icons.Stock, "#0F766E", "شرائح تصاعدية على إجمالي الكمية المباعة × أيام الدوام") { }

    public ObservableCollection<Employee> Managers { get; } = new();

    protected override int GetId(SalesManagerIncentiveTier e) => e.Id;
    protected override string Describe(SalesManagerIncentiveTier e) => $"{e.Employee?.FullName}: {e.FromQuantity:N0} – {(e.ToQuantity is null ? "∞" : e.ToQuantity.Value.ToString("N0"))}";

    protected override async Task LoadLookupsAsync(ProjectDbContext db)
    {
        Managers.Clear();
        foreach (var m in await db.Employees.AsNoTracking().Where(e => e.IsSalesManager && e.IsActive).OrderBy(e => e.FullName).ToListAsync()) Managers.Add(m);
    }

    protected override Task<List<SalesManagerIncentiveTier>> QueryAsync(ProjectDbContext db) =>
        db.SalesManagerIncentiveTiers.AsNoTracking().Include(t => t.Employee).OrderBy(t => t.Employee.FullName).ThenBy(t => t.FromQuantity).ToListAsync();

    protected override SalesManagerIncentiveTier CreateNew() => new() { EmployeeId = Managers.FirstOrDefault()?.Id ?? 0 };

    protected override string? Validate(SalesManagerIncentiveTier e)
    {
        if (e.EmployeeId == 0) return "اختر مدير المبيعات (فعّل علامة \"مدير مبيعات\" للموظف أولًا)";
        if (e.FromQuantity < 0 || e.RatePerUnit < 0) return "القيم لا يمكن أن تكون سالبة";
        if (e.ToQuantity is not null && e.ToQuantity <= e.FromQuantity) return "نهاية الشريحة يجب أن تكون أكبر من بدايتها";
        var to = e.ToQuantity ?? decimal.MaxValue;
        if (Items.Any(x => x.Id != e.Id && x.EmployeeId == e.EmployeeId && e.FromQuantity < (x.ToQuantity ?? decimal.MaxValue) && x.FromQuantity < to))
            return "هذه الشريحة تتداخل مع شريحة أخرى لنفس المدير";
        return null;
    }
}
