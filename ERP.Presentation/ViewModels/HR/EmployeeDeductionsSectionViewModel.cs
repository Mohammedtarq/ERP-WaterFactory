using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.HR;

/// <summary>
/// السلف (أقساط شهرية) والمسحوبات (تُستقطع كاملة آخر الشهر) والعقوبات (خصم بسبب) — تُستقطع تلقائيًا عند توليد الرواتب.
/// السلفة والمسحوب يُصرفان من الصندوق ويُطبع سند صرف؛ العقوبة إشعار خصم بلا نقد.
/// </summary>
public class EmployeeDeductionsSectionViewModel : SectionViewModel
{
    private Employee? _employee;
    private Option<EmployeeDeductionKind> _kind;
    private decimal _amount;
    private decimal _installment;
    private DateTime _date = DateTime.Today;
    private int _startMonth = DateTime.Today.Month;
    private int _startYear = DateTime.Today.Year;
    private string? _reason;
    private Employee? _filterEmployee;
    private bool _openOnly = true;
    private EmployeeDeductionRow? _voidTarget;
    private string? _voidReason;

    public EmployeeDeductionsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.HR, "السلف والمسحوبات والعقوبات", Icons.Voucher, "#F59E0B", "تُستقطع تلقائيًا من الرواتب: السلفة بأقساط، والمسحوب والعقوبة كاملة")
    {
        _kind = Kinds[0];
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        PrintCommand = new AsyncRelayCommand(p => p is EmployeeDeductionRow r ? PrintAsync(db => DocumentReports.EmployeeDeductionAsync(Session, db, r.Id)) : Task.CompletedTask);
        BeginVoidCommand = new RelayCommand(p =>
        {
            if (p is not EmployeeDeductionRow r) return;
            if (r.IsVoided) { Dialogs.Error("ملغى مسبقًا"); return; }
            VoidReason = null;
            VoidTarget = r;
        });
        CancelVoidCommand = new RelayCommand(() => VoidTarget = null);
        VoidCommand = new AsyncRelayCommand(VoidAsync);
    }

    protected override bool HasPendingInput => Amount != 0;

    protected override void ResetInput()
    {
        _employee = null;
        OnPropertyChanged(nameof(Employee));
        Kind = Kinds[0];
        Amount = 0;
        Installment = 0;
        Date = DateTime.Today;
        Reason = null;
        VoidTarget = null;
        VoidReason = null;
    }
    protected override bool ReloadOnActivate => true;

    public IReadOnlyList<Option<EmployeeDeductionKind>> Kinds { get; } = ArabicLabels.OptionsOf<EmployeeDeductionKind>();
    public IReadOnlyList<int> Months { get; } = Enumerable.Range(1, 12).ToList();
    public IReadOnlyList<int> Years { get; } = Enumerable.Range(DateTime.Today.Year - 1, 4).ToList();
    public ObservableCollection<Employee> Employees { get; } = new();
    public ObservableCollection<EmployeeDeductionRow> Rows { get; } = new();

    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand PrintCommand { get; }
    public RelayCommand BeginVoidCommand { get; }
    public RelayCommand CancelVoidCommand { get; }
    public AsyncRelayCommand VoidCommand { get; }

    public Employee? Employee { get => _employee; set => SetProperty(ref _employee, value); }
    public Option<EmployeeDeductionKind> Kind
    {
        get => _kind;
        set
        {
            if (!SetProperty(ref _kind, value)) return;
            foreach (var n in new[] { nameof(IsLoan), nameof(IsPenalty), nameof(KindHint), nameof(SaveLabel), nameof(StartLabel) }) OnPropertyChanged(n);
            ApplyWithdrawalCutoff();
        }
    }
    public bool IsLoan => Kind.Value == EmployeeDeductionKind.Loan;
    public bool IsPenalty => Kind.Value == EmployeeDeductionKind.Penalty;
    public string KindHint => Kind.Value switch
    {
        EmployeeDeductionKind.Loan => "تُصرف من صندوقك الآن، وتُستقطع بالقسط الشهري من الرواتب بدءًا من شهر البداية حتى السداد.",
        EmployeeDeductionKind.Withdrawal => $"يُصرف من صندوقك الآن، ويُستقطع كاملًا من راتب الشهر المحدد. المسحوبات تُغلق يوم {HrRules.WithdrawalCutoffDay}: ما بعده يُستقطع من الشهر التالي.",
        _ => "لا يُصرف شيء من الصندوق؛ يُخصم المبلغ من راتب الشهر المحدد. السبب إلزامي."
    };
    public string SaveLabel => IsPenalty ? "حفظ العقوبة وطباعة الإشعار" : $"صرف {Kind.Label} وطباعة السند";
    public string StartLabel => IsLoan ? "يبدأ الاستقطاع من راتب شهر" : "يُستقطع من راتب شهر";

    public decimal Amount { get => _amount; set { if (SetProperty(ref _amount, value)) OnPropertyChanged(nameof(InstallmentsText)); } }
    public decimal Installment { get => _installment; set { if (SetProperty(ref _installment, value)) OnPropertyChanged(nameof(InstallmentsText)); } }
    public string InstallmentsText => IsLoan && Installment > 0 && Amount > 0 ? $"عدد الأقساط: {Math.Ceiling(Amount / Installment):N0} شهر" : "";
    public DateTime Date { get => _date; set { if (SetProperty(ref _date, value)) ApplyWithdrawalCutoff(); } }

    /// <summary>المسحوب بعد يوم الإغلاق ينتقل تلقائيًا لرواتب الشهر التالي.</summary>
    private void ApplyWithdrawalCutoff()
    {
        if (Kind.Value != EmployeeDeductionKind.Withdrawal) return;
        var (m, y) = HrRules.FirstWithdrawalPeriod(Date);
        if (StartYear * 12 + StartMonth >= y * 12 + m) return;
        StartMonth = m;
        StartYear = y;
    }
    public int StartMonth { get => _startMonth; set => SetProperty(ref _startMonth, value); }
    public int StartYear { get => _startYear; set => SetProperty(ref _startYear, value); }
    public string? Reason { get => _reason; set => SetProperty(ref _reason, value); }

    public Employee? FilterEmployee { get => _filterEmployee; set { if (SetProperty(ref _filterEmployee, value)) Background(LoadRowsAsync()); } }
    public bool OpenOnly { get => _openOnly; set { if (SetProperty(ref _openOnly, value)) Background(LoadRowsAsync()); } }
    public decimal TotalRemaining => Rows.Sum(r => r.Remaining);

    public EmployeeDeductionRow? VoidTarget { get => _voidTarget; private set { if (SetProperty(ref _voidTarget, value)) OnPropertyChanged(nameof(IsVoiding)); } }
    public bool IsVoiding => VoidTarget is not null;
    public string? VoidReason { get => _voidReason; set => SetProperty(ref _voidReason, value); }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var (selected, filter) = (Employee?.Id, FilterEmployee?.Id);
        Employees.Clear();
        foreach (var e in await db.Employees.AsNoTracking().Where(e => e.IsActive && !e.IsTemporary).OrderBy(e => e.FullName).ToListAsync()) Employees.Add(e);
        _employee = Employees.FirstOrDefault(e => e.Id == selected);
        _filterEmployee = Employees.FirstOrDefault(e => e.Id == filter);
        OnPropertyChanged(nameof(Employee));
        OnPropertyChanged(nameof(FilterEmployee));
        await LoadRowsAsync();
    }

    private async Task LoadRowsAsync()
    {
        VoidTarget = null;
        await using var db = Session.NewDb();
        Rows.Clear();
        foreach (var r in await new EmployeeDeductionService(db).GetListAsync(FilterEmployee?.Id, OpenOnly)) Rows.Add(r);
        OnPropertyChanged(nameof(TotalRemaining));
    }

    private async Task SaveAsync()
    {
        if (!Require(CanAdd, "تسجيل السلف والمسحوبات والعقوبات")) return;
        if (Employee is null) { Dialogs.Error("اختر الموظف"); return; }
        if (Amount <= 0) { Dialogs.Error("أدخل مبلغًا أكبر من صفر"); return; }
        if (IsLoan && Installment <= 0) { Dialogs.Error("أدخل القسط الشهري"); return; }
        if (IsPenalty && string.IsNullOrWhiteSpace(Reason)) { Dialogs.Error("اكتب سبب العقوبة"); return; }

        await using var db = Session.NewDb();
        var req = new EmployeeDeductionService.CreateRequest(Kind.Value, Employee.Id, Amount, Date, StartMonth, StartYear,
                                                             IsLoan ? Installment : null, Reason);
        var (result, created) = await new EmployeeDeductionService(db).CreateAsync(req, Session.UserId);
        if (!result.Success) { Dialogs.Error(result.ErrorMessage ?? "تعذّر الحفظ"); return; }
        StatusMessage = $"حُفظت {Kind.Label} {created!.DeductionNumber} لـ {Employee.FullName} بمبلغ {Amount:N0} د.ع";
        Amount = 0;
        Installment = 0;
        Reason = null;
        await LoadRowsAsync();
        await PrintAsync(d => DocumentReports.EmployeeDeductionAsync(Session, d, created.Id));
    }

    private async Task VoidAsync()
    {
        if (VoidTarget is null) return;
        if (string.IsNullOrWhiteSpace(VoidReason)) { Dialogs.Error("اكتب سبب الإلغاء"); return; }
        var target = VoidTarget;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new EmployeeDeductionService(db).VoidAsync(target.Id, VoidReason!, Session.UserId),
                                    $"أُلغي {target.KindText} {target.DeductionNumber} — إن كانت رواتب الشهر مولّدة فأعد توليدها"))
            await LoadRowsAsync();
    }
}
