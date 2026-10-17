using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;

namespace ERP.Presentation.ViewModels.HR;

/// <summary>سطر كشف اليوم لعامل وقتي: 0 غائب، 0.5 نصف يوم، 1 يوم، 1.5 يوم ونصف.</summary>
public class TempDayRow : ObservableObject
{
    private decimal _days;
    private string? _notes;
    public int EmployeeId { get; init; }
    public string FullName { get; init; } = "";
    public string? Department { get; init; }
    public decimal DailyWage { get; init; }
    public bool IsPaid { get; init; }
    public decimal Days { get => _days; set => SetProperty(ref _days, value); }
    public string? Notes { get => _notes; set => SetProperty(ref _notes, value); }
    public string PaidText => IsPaid ? "مصروف" : "";
}

/// <summary>
/// العمال الوقتيون: كشف أيام يدوي بلا بصمة، وصرف الأجر عن الأيام غير المصروفة أسبوعيًا أو تسوية نهاية الخدمة.
/// </summary>
public class TempWorkersSectionViewModel : SectionViewModel
{
    private DateTime _date = DateTime.Today;
    private DateTime _from = DateTime.Today.AddDays(-30);
    private DateTime _to = DateTime.Today;

    public TempWorkersSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.HR, "العمال الوقتيون", Icons.People, "#A855F7",
               "أجر يومي بلا بصمة ولا سلف: كشف أيام يدوي، وصرف أسبوعي أو عند إنهاء الخدمة")
    {
        SaveDayCommand = new AsyncRelayCommand(SaveDayAsync);
        AllPresentCommand = new RelayCommand(() => { foreach (var r in Rows.Where(r => !r.IsPaid)) r.Days = 1; });
        PayCommand = new AsyncRelayCommand(p => p is TempWorkerBalanceRow b ? PayAsync(b, false) : Task.CompletedTask);
        EndServiceCommand = new AsyncRelayCommand(p => p is TempWorkerBalanceRow b ? PayAsync(b, true) : Task.CompletedTask);
        LoadPaymentsCommand = new AsyncRelayCommand(LoadPaymentsAsync);
        PrintCommand = new RelayCommand(p => { if (p is TempPaymentRow r) Print(r); });
    }

    protected override bool ReloadOnActivate => true;
    public DateTime Date { get => _date; set { if (SetProperty(ref _date, value)) Background(LoadDayAsync()); } }
    public DateTime From { get => _from; set => SetProperty(ref _from, value); }
    public DateTime To { get => _to; set => SetProperty(ref _to, value); }
    public ObservableCollection<TempDayRow> Rows { get; } = new();
    public ObservableCollection<TempWorkerBalanceRow> Balances { get; } = new();
    public ObservableCollection<TempPaymentRow> Payments { get; } = new();
    public decimal TotalDue => Balances.Sum(b => b.Due);

    public AsyncRelayCommand SaveDayCommand { get; }
    public RelayCommand AllPresentCommand { get; }
    public AsyncRelayCommand PayCommand { get; }
    public AsyncRelayCommand EndServiceCommand { get; }
    public AsyncRelayCommand LoadPaymentsCommand { get; }
    public RelayCommand PrintCommand { get; }

    public override async Task LoadAsync()
    {
        await LoadDayAsync();
        await LoadBalancesAsync();
        await LoadPaymentsAsync();
    }

    private async Task LoadDayAsync()
    {
        await using var db = Session.NewDb();
        var svc = new TempWorkersService(db);
        var days = (await svc.DaysAsync(Date)).ToDictionary(x => x.EmployeeId);
        Rows.Clear();
        foreach (var w in await svc.WorkersAsync())
        {
            days.TryGetValue(w.Id, out var day);
            Rows.Add(new TempDayRow
            {
                EmployeeId = w.Id, FullName = w.FullName, Department = w.Department?.Name, DailyWage = w.DailyWage ?? 0,
                IsPaid = day?.PaymentId is not null, Days = day?.Days ?? 0, Notes = day?.Notes
            });
        }
        StatusMessage = Rows.Count == 0 ? "لا عمال وقتيون — أضفهم من «الموظفون» بعلامة «عامل وقتي» وأجر يومي" : null;
    }

    private async Task LoadBalancesAsync()
    {
        await using var db = Session.NewDb();
        Balances.Clear();
        foreach (var b in await new TempWorkersService(db).BalancesAsync()) Balances.Add(b);
        OnPropertyChanged(nameof(TotalDue));
    }

    private async Task LoadPaymentsAsync()
    {
        await using var db = Session.NewDb();
        Payments.Clear();
        foreach (var p in await new TempWorkersService(db).PaymentsAsync(From, To)) Payments.Add(p);
    }

    private async Task SaveDayAsync()
    {
        if (!Require(CanAdd, "كشف العمال الوقتيين")) return;
        await using var db = Session.NewDb();
        var inputs = Rows.Select(r => new TempDayInput(r.EmployeeId, r.Days, r.Notes)).ToList();
        if (await RunOperationAsync(() => new TempWorkersService(db).SaveDayAsync(Date, inputs, Session.UserId),
                                    $"حُفظ كشف {Date:yyyy/MM/dd}: {Rows.Sum(r => r.Days):0.##} يوم عمل"))
            await LoadBalancesAsync();
    }

    private async Task PayAsync(TempWorkerBalanceRow b, bool endOfService)
    {
        if (!Require(CanAdd, "صرف أجور العمال الوقتيين")) return;
        var question = endOfService
            ? $"إنهاء خدمة {b.FullName} بتاريخ {Date:yyyy/MM/dd} وصرف {b.Due:N0} د.ع عن {b.UnpaidDays:0.##} يوم؟ تُغلق بطاقته وتبقى في الأرشيف."
            : $"صرف أجر {b.FullName}: {b.UnpaidDays:0.##} يوم × {b.DailyWage:N0} = {b.Due:N0} د.ع من صندوقك؟";
        if (!Dialogs.Confirm(question)) return;
        await using var db = Session.NewDb();
        TempWorkerPayment? payment = null;
        if (await RunOperationAsync(async () =>
            {
                var (r, p) = await new TempWorkersService(db).PayAsync(b.EmployeeId, Date, Date, endOfService, Session.UserId);
                payment = p;
                return r;
            }, endOfService ? $"أُنهيت خدمة {b.FullName}" : $"صُرف أجر {b.FullName}"))
        {
            StatusMessage = $"{StatusMessage} — {payment!.PaymentNumber}";
            await LoadAsync();
            if (Payments.FirstOrDefault(x => x.Id == payment.Id) is { } row) Print(row);
        }
    }

    private void Print(TempPaymentRow p)
    {
        var r = new ReportDocument { Key = "temp-wages", CompanyName = Session.ProjectName,
                                     Title = p.IsFinal ? $"تسوية نهاية خدمة {p.PaymentNumber}" : $"وصل أجور {p.PaymentNumber}", PrintedBy = Session.FullName };
        r.Field("العامل", p.FullName).Field("تاريخ الصرف", p.PaidDate.ToString("yyyy/MM/dd"))
         .Field("عن الفترة", $"{p.FromDate:yyyy/MM/dd} – {p.ToDate:yyyy/MM/dd}");
        r.Columns.AddRange(new[] { "الأيام", "الأجر اليومي", "المبلغ" });
        r.Rows.Add(new[] { $"{p.Days:0.##}", $"{p.DailyWage:N0}", $"{p.Amount:N0}" });
        r.Total("المبلغ المستلم", $"{p.Amount:N0} د.ع", true);
        r.Signatures.AddRange(new[] { "المستلم", "أمين الصندوق" });
        Dialogs.ShowReport(r);
    }
}
