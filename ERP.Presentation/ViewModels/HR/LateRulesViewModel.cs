using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.HR;

/// <summary>شريحة خصم تأخير قابلة للتعديل في الشاشة (النهاية الفارغة = بلا حد أعلى).</summary>
public class LateTierRow : ObservableObject
{
    private int _from = 1;
    private string _to = "";
    private Option<LateDeductionKind> _kind = null!;
    private decimal _value;
    private string _preview = "";

    public int From { get => _from; set => SetProperty(ref _from, value); }
    public string To { get => _to; set => SetProperty(ref _to, value ?? ""); }
    public Option<LateDeductionKind> Kind { get => _kind; set { if (SetProperty(ref _kind, value)) OnPropertyChanged(nameof(ValueHint)); } }
    public decimal Value { get => _value; set => SetProperty(ref _value, value); }
    /// <summary>المبلغ لراتب المعاينة (يُحسب في الشاشة).</summary>
    public string Preview { get => _preview; set => SetProperty(ref _preview, value); }
    public string ValueHint => Kind?.Value switch { LateDeductionKind.Fraction => "جزء من أجر اليوم (0.25 = ربع)", LateDeductionKind.Fixed => "مبلغ بالدينار", _ => "—" };
}

public class LateExceptionRow : ObservableObject
{
    private bool _exempt;
    private string _grace = "";
    public int EmployeeId { get; init; }
    public string EmployeeName { get; init; } = "";
    public string DepartmentName { get; init; } = "";
    public bool OriginalExempt { get; init; }
    public string OriginalGrace { get; init; } = "";
    public bool Exempt { get => _exempt; set { if (SetProperty(ref _exempt, value)) OnPropertyChanged(nameof(IsChanged)); } }
    /// <summary>سماح خاص بالدقائق لخصم التأخير فقط (فارغ = سماح الشفت).</summary>
    public string Grace { get => _grace; set { if (SetProperty(ref _grace, value ?? "")) OnPropertyChanged(nameof(IsChanged)); } }
    public bool IsChanged => Exempt != OriginalExempt || Grace.Trim() != OriginalGrace;
}

/// <summary>
/// «قواعد التأخير» (في صفحة الحضور): تُضبط مرة واحدة وتسري على كل الأشهر من شهر التفعيل:
/// شرائح الخصم (جزء من أجر اليوم أو مبلغ ثابت)، استثناءات الموظفين (إعفاء أو سماح خاص)، ومسامحة شهرية بالدقائق.
/// المسامحة اليومية تُدخل من «الحضور اليومي». التعديل والمسامحة بصلاحية «مسامحة التأخير» ولا يمسان الأشهر المعتمدة.
/// </summary>
public class LateRulesSectionViewModel : SectionViewModel
{
    private bool _enabled;
    private int _fromMonth = DateTime.Today.Month;
    private int _fromYear = DateTime.Today.Year;
    private decimal _sampleSalary = 500_000;
    private LateExceptionRow? _selectedEmployee;
    private int _forgiveMonth = DateTime.Today.AddMonths(-1).Month;
    private int _forgiveYear = DateTime.Today.AddMonths(-1).Year;
    private int _forgiveMinutes;
    private string? _forgiveReason;

    public LateRulesSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.HR, "قواعد التأخير", Icons.Clock, "#8B5CF6", "شرائح خصم التأخير تُضبط مرة واحدة، واستثناءات الموظفين، والمسامحة الشهرية")
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        AddTierCommand = new RelayCommand(AddTier);
        RemoveTierCommand = new RelayCommand(p => { if (p is LateTierRow r) { Tiers.Remove(r); RecomputePreviews(); } });
        SingleTierCommand = new RelayCommand(() =>
        {
            Tiers.Clear();
            Tiers.Add(NewTier(1, "", LateDeductionKind.Fraction, 0.25m));
            RecomputePreviews();
        });
        SaveExceptionsCommand = new AsyncRelayCommand(SaveExceptionsAsync);
        SaveForgivenessCommand = new AsyncRelayCommand(SaveForgivenessAsync);
        UndoForgivenessCommand = new AsyncRelayCommand(() => SaveForgivenessAsync(clear: true));
    }

    protected override bool ReloadOnActivate => false;
    protected override bool HasPendingInput => true;   // جدول شرائح قيد التحرير: لا يُعاد تحميله تحت المستخدم

    public IReadOnlyList<Option<LateDeductionKind>> KindOptions { get; } = new Option<LateDeductionKind>[]
    {
        new(LateDeductionKind.Fraction, "جزء من أجر اليوم"),
        new(LateDeductionKind.Fixed, "مبلغ ثابت (د.ع)"),
        new(LateDeductionKind.None, "لا خصم"),
    };
    public IReadOnlyList<int> Months { get; } = Enumerable.Range(1, 12).ToList();
    public IReadOnlyList<int> Years { get; } = Enumerable.Range(DateTime.Today.Year - 1, 4).ToList();

    public bool CanEdit2 => Has(SpecialPermission.LateForgive);
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public int FromMonth { get => _fromMonth; set => SetProperty(ref _fromMonth, value); }
    public int FromYear { get => _fromYear; set => SetProperty(ref _fromYear, value); }
    /// <summary>راتب للمعاينة فقط: يُحسب منه المبلغ المقابل لكل شريحة.</summary>
    public decimal SampleSalary { get => _sampleSalary; set { if (SetProperty(ref _sampleSalary, value)) RecomputePreviews(); } }
    public string SampleWageText => $"أجر اليوم للمعاينة = {SampleSalary:N0} ÷ 30 = {SampleSalary / HrRules.DaysPerMonthForDailyWage:N0}";
    public ObservableCollection<LateTierRow> Tiers { get; } = new();
    public ObservableCollection<LateExceptionRow> Employees { get; } = new();

    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand AddTierCommand { get; }
    public RelayCommand RemoveTierCommand { get; }
    public RelayCommand SingleTierCommand { get; }
    public AsyncRelayCommand SaveExceptionsCommand { get; }

    // ---- المسامحة الشهرية ----
    public LateExceptionRow? SelectedEmployee { get => _selectedEmployee; set => SetProperty(ref _selectedEmployee, value); }
    public int ForgiveMonth { get => _forgiveMonth; set => SetProperty(ref _forgiveMonth, value); }
    public int ForgiveYear { get => _forgiveYear; set => SetProperty(ref _forgiveYear, value); }
    public int ForgiveMinutes { get => _forgiveMinutes; set => SetProperty(ref _forgiveMinutes, value); }
    public string? ForgiveReason { get => _forgiveReason; set => SetProperty(ref _forgiveReason, value); }
    public AsyncRelayCommand SaveForgivenessCommand { get; }
    public AsyncRelayCommand UndoForgivenessCommand { get; }

    private LateTierRow NewTier(int from, string to, LateDeductionKind kind, decimal value)
    {
        var row = new LateTierRow { From = from, To = to, Kind = KindOptions.First(o => o.Value == kind), Value = value };
        row.PropertyChanged += (_, _) => RecomputePreviews();
        return row;
    }

    private void AddTier()
    {
        var last = Tiers.LastOrDefault();
        var from = last is null ? 1 : int.TryParse(last.To, out var t) ? t + 1 : last.From + 30;
        if (last is not null && last.To.Trim().Length == 0) last.To = (from - 1).ToString();   // كانت مفتوحة: تُغلق قبل الجديدة
        Tiers.Add(NewTier(from, "", LateDeductionKind.Fraction, 0.25m));
        RecomputePreviews();
    }

    /// <summary>المبلغ لكل شريحة بحسب راتب المعاينة.</summary>
    private void RecomputePreviews()
    {
        OnPropertyChanged(nameof(SampleWageText));
        var wage = SampleSalary / HrRules.DaysPerMonthForDailyWage;
        foreach (var t in Tiers)
            t.Preview = t.Kind?.Value switch
            {
                LateDeductionKind.Fraction => $"{Math.Min(wage * t.Value, wage):N0}",
                LateDeductionKind.Fixed => $"{(wage > 0 ? Math.Min(t.Value, wage) : t.Value):N0}",
                _ => "0"
            };
    }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var settings = await new LateDeductionService(db).GetSettingsAsync();
        Enabled = settings.Enabled;
        var from = settings.From ?? DateTime.Today;
        FromMonth = from.Month; FromYear = from.Year;
        Tiers.Clear();
        foreach (var t in settings.Tiers) Tiers.Add(NewTier(t.FromMinutes, t.ToMinutes?.ToString() ?? "", t.Kind, t.Value));
        RecomputePreviews();

        Employees.Clear();
        foreach (var e in await db.Employees.AsNoTracking().Include(e => e.Department).Where(e => e.IsActive && !e.IsTemporary).OrderBy(e => e.FullName).ToListAsync())
        {
            var grace = e.LateGraceOverride?.ToString() ?? "";
            Employees.Add(new LateExceptionRow
            {
                EmployeeId = e.Id, EmployeeName = e.FullName, DepartmentName = e.Department?.Name ?? DepartmentChoice.NoDepartment,
                OriginalExempt = e.LateDeductionExempt, OriginalGrace = grace, Exempt = e.LateDeductionExempt, Grace = grace
            });
        }
        OnPropertyChanged(nameof(CanEdit2));
        SelectedEmployee = null;
    }

    private async Task SaveAsync()
    {
        if (!Has(SpecialPermission.LateForgive)) { Dialogs.Error($"ضبط قواعد التأخير يحتاج صلاحية «{SpecialPermission.NameOf(SpecialPermission.LateForgive)}»"); return; }
        var inputs = new List<LateTierInput>();
        foreach (var t in Tiers)
        {
            int? to = null;
            if (t.To.Trim().Length > 0)
            {
                if (!int.TryParse(t.To.Trim(), out var n)) { Dialogs.Error($"نهاية الشريحة «{t.To}» ليست رقمًا (اتركها فارغة لبلا حد أعلى)"); return; }
                to = n;
            }
            inputs.Add(new LateTierInput(t.From, to, t.Kind.Value, t.Value));
        }
        await using var db = Session.NewDb();
        var from = new DateTime(FromYear, FromMonth, 1);
        if (await RunOperationAsync(() => new LateDeductionService(db).SaveSettingsAsync(Enabled, from, inputs, Session.UserId),
                                    Enabled ? $"حُفظت قواعد التأخير وهي مفعّلة من {FromMonth:00}/{FromYear} وتسري على الأشهر التالية تلقائيًا"
                                            : "حُفظت قواعد التأخير (معطّلة: لا خصم على أي راتب)"))
        {
            var message = StatusMessage;
            await LoadAsync();
            StatusMessage = message;
        }
    }

    private async Task SaveExceptionsAsync()
    {
        if (!Has(SpecialPermission.LateForgive)) { Dialogs.Error($"الاستثناءات تحتاج صلاحية «{SpecialPermission.NameOf(SpecialPermission.LateForgive)}»"); return; }
        var changed = Employees.Where(e => e.IsChanged).ToList();
        if (changed.Count == 0) { StatusMessage = "لا استثناءات معدَّلة للحفظ"; return; }
        await using var db = Session.NewDb();
        var svc = new LateDeductionService(db);
        foreach (var e in changed)
        {
            int? grace = null;
            if (e.Grace.Trim().Length > 0)
            {
                if (!int.TryParse(e.Grace.Trim(), out var g) || g < 0) { Dialogs.Error($"السماح الخاص لـ {e.EmployeeName} يجب أن يكون عددًا من الدقائق"); return; }
                grace = g;
            }
            var r = await svc.SetEmployeeExceptionAsync(e.EmployeeId, e.Exempt, grace, Session.UserId);
            if (!r.Success) { Dialogs.Error($"{e.EmployeeName}: {r.ErrorMessage}"); return; }
        }
        await LoadAsync();
        StatusMessage = $"حُفظت استثناءات {changed.Count} موظف";
    }

    private Task SaveForgivenessAsync() => SaveForgivenessAsync(clear: false);

    private async Task SaveForgivenessAsync(bool clear)
    {
        if (!Has(SpecialPermission.LateForgive)) { Dialogs.Error($"المسامحة تحتاج صلاحية «{SpecialPermission.NameOf(SpecialPermission.LateForgive)}»"); return; }
        if (SelectedEmployee is null) { Dialogs.Error("اختر الموظف من الجدول"); return; }
        var name = SelectedEmployee.EmployeeName;
        await using var db = Session.NewDb();
        var minutes = clear ? 0 : ForgiveMinutes;
        if (await RunOperationAsync(() => new LateDeductionService(db).SetMonthlyForgivenessAsync(SelectedEmployee.EmployeeId, ForgiveMonth, ForgiveYear, minutes, ForgiveReason, Session.UserId),
                                    minutes == 0 ? $"أُلغيت المسامحة الشهرية لـ {name} ({ForgiveMonth:00}/{ForgiveYear})"
                                                 : $"سومح {name} بـ {minutes} دقيقة من تأخير {ForgiveMonth:00}/{ForgiveYear}"))
        {
            ForgiveMinutes = 0; ForgiveReason = null;
        }
    }
}
