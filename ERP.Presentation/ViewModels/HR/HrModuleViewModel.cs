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
        Attendance = Add(new AttendanceSectionViewModel(s, d));
        Incentives = Add(new MonthlyIncentiveSectionViewModel(s, d));
        Payroll = Add(new PayrollSectionViewModel(s, d));
        Add(new PromotionsSectionViewModel(s, d));
        Add(new EmployeesSectionViewModel(s, d));
        Add(new ShiftsSectionViewModel(s, d));
        Add(new DepartmentsSectionViewModel(s, d));
        Add(new IncentiveSettingsSectionViewModel(s, d));
        Add(new RepIncentiveRatesSectionViewModel(s, d));
        Add(new ManagerTiersSectionViewModel(s, d));
    }

    public AttendanceSectionViewModel Attendance { get; }
    public MonthlyIncentiveSectionViewModel Incentives { get; }
    public PayrollSectionViewModel Payroll { get; }
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

// ============================ الحضور اليومي ============================
public class AttendanceRow : ObservableObject
{
    private Option<AttendanceStatus>? _forced;
    private string _checkIn = "";
    private string _checkOut = "";

    public int EmployeeId { get; init; }
    public string EmployeeName { get; init; } = "";
    public string ShiftText { get; init; } = "";
    public Option<AttendanceStatus>? Forced { get => _forced; set => SetProperty(ref _forced, value); }
    public string CheckIn { get => _checkIn; set => SetProperty(ref _checkIn, value); }
    public string CheckOut { get => _checkOut; set => SetProperty(ref _checkOut, value); }
    public string SavedStatus { get; set; } = "";
    public int SavedLateMinutes { get; set; }
}

public class AttendanceSectionViewModel : SectionViewModel
{
    private DateTime _date = DateTime.Today;

    public AttendanceSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.HR, "الحضور اليومي", Icons.Calendar, "#EC4899", "الدخول والخروج والتأخير والغياب لكل موظف")
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        AllPresentCommand = new RelayCommand(() =>
        {
            foreach (var r in Rows.Where(r => r.CheckIn.Length == 0 && r.Forced is null)) r.CheckIn = r.ShiftText.Split(' ')[0] is { Length: 5 } t ? t : "08:00";
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

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var employees = await db.Employees.AsNoTracking().Include(e => e.Shift).Where(e => e.IsActive).OrderBy(e => e.FullName).ToListAsync();
        var records = await db.AttendanceRecords.AsNoTracking().Where(a => a.AttendanceDate == Date).ToDictionaryAsync(a => a.EmployeeId);
        Rows.Clear();
        foreach (var e in employees)
        {
            records.TryGetValue(e.Id, out var rec);
            Rows.Add(new AttendanceRow
            {
                EmployeeId = e.Id,
                EmployeeName = e.FullName,
                ShiftText = e.Shift is null ? "بلا شفت" : $"{e.Shift.CheckInTime:hh\\:mm} (سماح {e.Shift.CheckInGraceMinutes} د)",
                Forced = rec?.Status is AttendanceStatus.Absent or AttendanceStatus.ApprovedLeave
                    ? ForcedOptions.First(o => o?.Value == rec.Status) : null,
                CheckIn = rec?.CheckInTime?.ToString(@"hh\:mm") ?? "",
                CheckOut = rec?.CheckOutTime?.ToString(@"hh\:mm") ?? "",
                SavedStatus = rec is null ? "غير مسجّل" : ArabicLabels.Of(rec.Status),
                SavedLateMinutes = rec?.LateMinutes ?? 0
            });
        }
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
        foreach (var r in Rows)
        {
            if (!TryTime(r.CheckIn, out var inT) || !TryTime(r.CheckOut, out var outT))
            {
                Dialogs.Error($"وقت غير صحيح لـ {r.EmployeeName}. اكتب الوقت بصيغة 08:30");
                return;
            }
            inputs.Add(new AttendanceInput(r.EmployeeId, r.Forced?.Value, inT, outT));
        }
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new HrService(db).SaveAttendanceAsync(Date, inputs), $"تم حفظ حضور {Date:yyyy/MM/dd} لـ {inputs.Count} موظف"))
            await LoadAsync();
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
    public decimal TotalAmount => Rows.Sum(r => r.Amount);
    public AsyncRelayCommand SaveAllCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var hr = new HrService(db);
        var w = await hr.GetWeightsAsync();
        WeightsText = $"المعادلة: (الانضباط × {w.AttendanceWeight:0.##} + الأداء × {w.PerformanceWeight:0.##} + المهارات × {w.SkillsWeight:0.##}) ÷ 100 ← مبلغ حسب شريحة المقياس";

        var employees = await db.Employees.AsNoTracking().Where(e => e.IsActive).OrderBy(e => e.FullName).ToListAsync();
        var saved = await db.MonthlyIncentiveEvaluations.AsNoTracking()
            .Where(e => e.PeriodMonth == Month && e.PeriodYear == Year).ToDictionaryAsync(e => e.EmployeeId);
        Rows.Clear();
        foreach (var e in employees)
        {
            saved.TryGetValue(e.Id, out var ev);
            Rows.Add(new IncentiveRow
            {
                EmployeeId = e.Id, EmployeeName = e.FullName,
                AttendanceScore = ev?.AttendanceScoreAuto ?? await hr.ComputeAttendanceScoreAsync(e.Id, Month, Year),
                Performance = ev?.PerformanceScoreManual ?? 0, Skills = ev?.SkillsScoreManual ?? 0,
                TotalScore = ev?.TotalScore ?? 0, Amount = ev?.IncentiveAmount ?? 0, IsSaved = ev is not null
            });
        }
        OnPropertyChanged(nameof(TotalAmount));
    }

    private async Task SaveAllAsync()
    {
        if (!Require(CanEdit || CanAdd, "تقييم الحوافز")) return;
        await using var db = Session.NewDb();
        var hr = new HrService(db);
        foreach (var r in Rows)
        {
            var (result, b) = await hr.SaveEvaluationAsync(r.EmployeeId, Month, Year, r.Performance, r.Skills);
            if (!result.Success) { Dialogs.Error($"{r.EmployeeName}: {result.ErrorMessage}"); return; }
            r.AttendanceScore = b!.AttendanceScore;
            r.TotalScore = b.TotalScore;
            r.Amount = b.Amount;
        }
        StatusMessage = $"تم حفظ تقييم {Rows.Count} موظف لشهر {PeriodText}";
        await LoadAsync();
    }
}

// ============================ تشغيل الرواتب ============================
public class PayrollRow
{
    public string EmployeeName { get; init; } = "";
    public string Currency { get; init; } = "";
    public decimal BaseSalary { get; init; }
    public decimal Allowances { get; init; }
    public decimal AbsenceDeduction { get; init; }
    public decimal RepIncentive { get; init; }
    public decimal ManagerIncentive { get; init; }
    public decimal MonthlyIncentive { get; init; }
    public decimal NetSalary { get; init; }
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
        var run = await db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(r => r.PeriodMonth == Month && r.PeriodYear == Year);
        RunId = run?.Id;
        IsApproved = run?.Status == PayrollRunStatus.Approved;
        Rows.Clear();
        Summary = null;
        if (run is null) return;

        var lines = await db.PayrollLines.AsNoTracking().Where(l => l.PayrollRunId == run.Id).Include(l => l.Employee)
                            .OrderBy(l => l.Employee.FullName).ToListAsync();
        foreach (var l in lines)
            Rows.Add(new PayrollRow
            {
                EmployeeName = l.Employee.FullName, Currency = l.Currency, BaseSalary = l.BaseSalary, Allowances = l.Allowances,
                AbsenceDeduction = l.AbsenceDeduction, RepIncentive = l.RepIncentiveAmount, ManagerIncentive = l.SalesManagerIncentiveAmount,
                MonthlyIncentive = l.MonthlyIncentiveAmount, NetSalary = l.NetSalary
            });
        Summary = await new HrService(db).SummarizeAsync(run.Id);
    }

    private async Task GenerateAsync()
    {
        if (!Require(CanAdd || CanEdit, "توليد الرواتب")) return;
        if (RunId is not null && !Dialogs.Confirm($"توجد مسودة رواتب لشهر {PeriodText}. إعادة التوليد تستبدلها بأحدث بيانات الحضور والحوافز. متابعة؟")) return;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(async () => (await new HrService(db).GenerateAsync(Month, Year)).result,
                                    $"تم توليد رواتب {PeriodText}"))
            await LoadAsync();
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
        : base(s, d, ModuleCode.HR, "الترقيات والعلاوات", Icons.Up, "#8B5CF6", "ترقية، علاوة سنوية دائمة، أو مكافأة لمرة واحدة") { }

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

// ============================ الموظفون ============================
public class EmployeesSectionViewModel : CrudSectionViewModel<Employee>
{
    public EmployeesSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.HR, "الموظفون", Icons.People, "#0EA5E9", "البيانات الأساسية، الشفت، العملة، وعلامة المندوب/مدير المبيعات") { }

    public ObservableCollection<Branch> Branches { get; } = new();
    public ObservableCollection<Department> Departments { get; } = new();
    public ObservableCollection<Shift> Shifts { get; } = new();
    public IReadOnlyList<SalaryCurrency> Currencies { get; } = Enum.GetValues<SalaryCurrency>();

    protected override int GetId(Employee e) => e.Id;
    protected override string Describe(Employee e) => e.FullName;
    protected override bool Matches(Employee e, string t) => base.Matches(e, t) || (e.JobTitle?.Contains(t) ?? false);

    protected override async Task LoadLookupsAsync(ProjectDbContext db)
    {
        Branches.Clear();
        foreach (var b in await db.Branches.AsNoTracking().OrderBy(b => b.Name).ToListAsync()) Branches.Add(b);
        Departments.Clear();
        foreach (var x in await db.Departments.AsNoTracking().OrderBy(x => x.Name).ToListAsync()) Departments.Add(x);
        Shifts.Clear();
        foreach (var x in await db.Shifts.AsNoTracking().OrderBy(x => x.Name).ToListAsync()) Shifts.Add(x);
    }

    protected override Task<List<Employee>> QueryAsync(ProjectDbContext db) =>
        db.Employees.AsNoTracking().Include(e => e.Branch).Include(e => e.Department).Include(e => e.Shift).OrderBy(e => e.FullName).ToListAsync();

    protected override Employee CreateNew() => new() { HireDate = DateTime.Today, BranchId = Branches.FirstOrDefault()?.Id, ShiftId = Shifts.FirstOrDefault()?.Id };
    protected override string? Validate(Employee e) =>
        string.IsNullOrWhiteSpace(e.FullName) ? "أدخل اسم الموظف" : e.BaseSalary < 0 ? "الراتب لا يمكن أن يكون سالبًا" : null;
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
        : base(s, d, ModuleCode.HR, "الأقسام", Icons.Layers, "#6366F1", "أقسام الشركة (الإنتاج، المبيعات، المخازن...)") { }

    protected override int GetId(Department e) => e.Id;
    protected override string Describe(Department e) => e.Name;
    protected override Task<List<Department>> QueryAsync(ProjectDbContext db) => db.Departments.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
    protected override string? Validate(Department e) => string.IsNullOrWhiteSpace(e.Name) ? "أدخل اسم القسم" : null;
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
public class RepIncentiveRatesSectionViewModel : CrudSectionViewModel<RepItemIncentiveRate>
{
    public RepIncentiveRatesSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.HR, "حافز المندوب", Icons.Reps, "#F97316", "حافز لكل قطعة مباعة من كل صنف") { }

    public ObservableCollection<Item> ItemsLookup { get; } = new();

    protected override int GetId(RepItemIncentiveRate e) => e.Id;
    protected override string Describe(RepItemIncentiveRate e) => $"{e.Item?.ItemName}: {e.IncentiveRatePerUnit:N2}";

    protected override async Task LoadLookupsAsync(ProjectDbContext db)
    {
        ItemsLookup.Clear();
        foreach (var i in await db.Items.AsNoTracking().Where(i => i.IsActive).OrderBy(i => i.ItemName).ToListAsync()) ItemsLookup.Add(i);
    }

    protected override Task<List<RepItemIncentiveRate>> QueryAsync(ProjectDbContext db) =>
        db.RepItemIncentiveRates.AsNoTracking().Include(r => r.Item).OrderBy(r => r.Item.ItemName).ToListAsync();

    protected override string? Validate(RepItemIncentiveRate e)
    {
        if (e.ItemId == 0) return "اختر الصنف";
        if (e.IncentiveRatePerUnit < 0) return "الحافز لا يمكن أن يكون سالبًا";
        if (Items.Any(x => x.Id != e.Id && x.ItemId == e.ItemId)) return "لهذا الصنف حافز مسجّل؛ عدّله بدل إضافة جديد";
        return null;
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
