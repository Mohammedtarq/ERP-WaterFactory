using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Sales;

/// <summary>
/// تأمينات العملاء: استلام مبلغ أمانة من العميل (مثل تأمين طباعة ستيكر خاص باسمه) أو إرجاعه، مع سجل كل عميل ورصيده،
/// وقائمة بكل العملاء الذين لديهم تأمينات. منفصلة عن الدين — لا تظهر في كشف الفواتير ولا تسدد منه.
/// </summary>
public class CustomerDepositsSectionViewModel : SectionViewModel
{
    public const string DefaultPurpose = "تأمين طباعة ستيكر خاص";

    private Customer? _customer;
    private Option<CustomerDepositKind> _kind;
    private decimal _amount;
    private DateTime _date = DateTime.Today;
    private string _currency = "IQD";
    private decimal _currencyAmount;
    private string? _purpose = DefaultPurpose;
    private CustomRecipe? _recipe;
    private string? _notes;
    private CustomerDepositRow? _voidTarget;
    private string? _voidReason;
    private decimal _balance;

    public CustomerDepositsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Sales, "تأمينات العملاء", Icons.Shield, "#0EA5E9", "مبالغ يودعها العملاء كأمانة (مثل تأمين الستيكر الخاص) — منفصلة عن الدين")
    {
        _kind = Kinds[0];
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        PrintCommand = new AsyncRelayCommand(p => p is CustomerDepositRow r ? PrintAsync(db => DocumentReports.CustomerDepositAsync(Session, db, r.Id)) : Task.CompletedTask);
        OpenCustomerCommand = new RelayCommand(p => { if (p is CustomerDepositBalanceRow b) Customer = Customers.FirstOrDefault(c => c.Id == b.CustomerId); });
        BeginVoidCommand = new RelayCommand(p =>
        {
            if (p is not CustomerDepositRow r) return;
            if (r.IsVoided) { Dialogs.Error("السند ملغى مسبقًا"); return; }
            VoidReason = null;
            VoidTarget = r;
        });
        CancelVoidCommand = new RelayCommand(() => VoidTarget = null);
        VoidCommand = new AsyncRelayCommand(VoidAsync);
    }

    protected override bool HasPendingInput => Amount != 0;
    protected override bool ReloadOnActivate => true;

    /// <summary>من الشاشة: استلام أو إرجاع فقط. الرصيد الافتتاحي يأتي من أداة النقل.</summary>
    public IReadOnlyList<Option<CustomerDepositKind>> Kinds { get; } =
        ArabicLabels.OptionsOf<CustomerDepositKind>().Where(k => k.Value != CustomerDepositKind.Opening).ToList();
    public IReadOnlyList<string> Currencies { get; } = new[] { "IQD", "USD" };

    public ObservableCollection<Customer> Customers { get; } = new();
    public ObservableCollection<CustomRecipe> Recipes { get; } = new();
    public ObservableCollection<CustomerDepositRow> History { get; } = new();
    public ObservableCollection<CustomerDepositBalanceRow> Balances { get; } = new();

    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand PrintCommand { get; }
    public RelayCommand OpenCustomerCommand { get; }
    public RelayCommand BeginVoidCommand { get; }
    public RelayCommand CancelVoidCommand { get; }
    public AsyncRelayCommand VoidCommand { get; }

    public Customer? Customer
    {
        get => _customer;
        set { if (SetProperty(ref _customer, value)) Background(LoadCustomerAsync()); }
    }

    public Option<CustomerDepositKind> Kind
    {
        get => _kind;
        set { if (SetProperty(ref _kind, value)) { OnPropertyChanged(nameof(IsReceipt)); OnPropertyChanged(nameof(SaveLabel)); } }
    }
    public bool IsReceipt => Kind.Value == CustomerDepositKind.Receipt;
    public string SaveLabel => IsReceipt ? "حفظ سند الاستلام وطباعته" : "حفظ سند الإرجاع وطباعته";

    public decimal Amount { get => _amount; set => SetProperty(ref _amount, value); }
    public DateTime Date { get => _date; set => SetProperty(ref _date, value); }
    public string Currency
    {
        get => _currency;
        set { if (SetProperty(ref _currency, value)) OnPropertyChanged(nameof(IsForeign)); }
    }
    public bool IsForeign => Currency != "IQD";
    public decimal CurrencyAmount { get => _currencyAmount; set => SetProperty(ref _currencyAmount, value); }
    public string? Purpose { get => _purpose; set => SetProperty(ref _purpose, value); }
    public CustomRecipe? Recipe { get => _recipe; set => SetProperty(ref _recipe, value); }
    public string? Notes { get => _notes; set => SetProperty(ref _notes, value); }

    public decimal Balance { get => _balance; private set { if (SetProperty(ref _balance, value)) OnPropertyChanged(nameof(BalanceText)); } }
    public string BalanceText => Customer is null ? "اختر عميلًا من القائمة أو من جدول الأرصدة"
        : Balance > 0 ? $"رصيد تأمين {Customer.Name}: {Balance:N0} د.ع" : $"لا يوجد تأمين قائم لـ {Customer.Name}";
    public decimal TotalBalances => Balances.Sum(b => b.Balance);

    public CustomerDepositRow? VoidTarget
    {
        get => _voidTarget;
        private set { if (SetProperty(ref _voidTarget, value)) OnPropertyChanged(nameof(IsVoiding)); }
    }
    public bool IsVoiding => VoidTarget is not null;
    public string? VoidReason { get => _voidReason; set => SetProperty(ref _voidReason, value); }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var selectedId = Customer?.Id;
        Customers.Clear();
        foreach (var c in await db.Customers.AsNoTracking().Where(c => c.IsActive || c.Id == selectedId).OrderBy(c => c.Name).ToListAsync())
            Customers.Add(c);
        Balances.Clear();
        foreach (var b in await new CustomerDepositService(db).GetBalancesAsync()) Balances.Add(b);
        OnPropertyChanged(nameof(TotalBalances));
        _customer = Customers.FirstOrDefault(c => c.Id == selectedId);
        OnPropertyChanged(nameof(Customer));
        await LoadCustomerAsync();
    }

    public async Task LoadCustomerAsync()
    {
        History.Clear();
        Recipes.Clear();
        Recipe = null;
        VoidTarget = null;
        if (Customer is null) { Balance = 0; OnPropertyChanged(nameof(BalanceText)); return; }
        await using var db = Session.NewDb();
        var svc = new CustomerDepositService(db);
        foreach (var r in (await svc.GetHistoryAsync(Customer.Id)).AsEnumerable().Reverse()) History.Add(r);
        foreach (var r in await db.CustomRecipes.AsNoTracking().Where(r => r.CustomerId == Customer.Id && r.IsActive).OrderBy(r => r.Name).ToListAsync())
            Recipes.Add(r);
        Balance = await svc.GetBalanceAsync(Customer.Id);
        OnPropertyChanged(nameof(BalanceText));
    }

    private async Task SaveAsync()
    {
        if (!Require(CanAdd, "تسجيل التأمينات")) return;
        if (Customer is null) { Dialogs.Error("اختر العميل"); return; }
        if (Amount <= 0) { Dialogs.Error("أدخل مبلغًا أكبر من صفر"); return; }
        if (IsReceipt && IsForeign && CurrencyAmount <= 0) { Dialogs.Error($"أدخل المبلغ المستلم بعملة {Currency}"); return; }

        await using var db = Session.NewDb();
        var svc = new CustomerDepositService(db);
        var (result, deposit) = IsReceipt
            ? await svc.ReceiveAsync(Customer.Id, Amount, Date, Session.UserId, Purpose, Recipe?.Id, Notes, Currency, IsForeign ? CurrencyAmount : null)
            : await svc.RefundAsync(Customer.Id, Amount, Date, Session.UserId, Notes);
        if (!result.Success) { Dialogs.Error(result.ErrorMessage ?? "تعذّر الحفظ"); return; }

        StatusMessage = $"حُفظ {Kind.Label} {deposit!.DepositNumber} بمبلغ {Amount:N0} د.ع";
        Amount = 0;
        CurrencyAmount = 0;
        Notes = null;
        Purpose = DefaultPurpose;
        await LoadAsync();
        await PrintAsync(d => DocumentReports.CustomerDepositAsync(Session, d, deposit.Id));
    }

    private async Task VoidAsync()
    {
        if (VoidTarget is null) return;
        if (string.IsNullOrWhiteSpace(VoidReason)) { Dialogs.Error("اكتب سبب الإلغاء"); return; }
        var target = VoidTarget;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new CustomerDepositService(db).VoidAsync(target.Id, VoidReason!, Session.UserId),
                                    $"أُلغي السند {target.DepositNumber} وعُكس قيده وحركة صندوقه"))
        {
            await LoadAsync();
        }
    }
}
