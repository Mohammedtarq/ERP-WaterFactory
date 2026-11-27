using System.Collections.ObjectModel;
using System.Text;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;

namespace ERP.Presentation.ViewModels.Finance;

/// <summary>
/// «صندوق المنزل»: الأرباح المتحققة خارج العمل. سجل كل ما دخل وخرج منه (بحث وفلترة وطباعة وتصدير)،
/// والصرف خارج العمل (الخمس، مساعدات، شراء ماكنات وأصول) بلا أثر على كلفة القنينة ولا صافي الربح،
/// وقروض صناديق العمل منه وسدادها.
/// </summary>
public class HomeBoxSectionViewModel : SectionViewModel
{
    public const string AllCategories = "كل الأنواع";

    private DateTime _from = new(DateTime.Today.Year, 1, 1);
    private DateTime _to = DateTime.Today;
    private string _category = AllCategories;
    private string? _search;
    private decimal _minAmount, _maxAmount;
    private decimal _opening, _totalIn, _totalOut, _closing, _loansOutstanding;
    private string _byCategory = "";
    // صرف جديد
    private Option<HomeBoxEntryKind> _kind;
    private decimal _amount;
    private DateTime _date = DateTime.Today;
    private string? _entryTitle, _beneficiary, _notes;
    // قرض
    private CashBoxRow? _loanBox;
    private decimal _loanAmount;
    private string? _loanReason;
    private HomeLoanRow? _selectedLoan;
    private CashBoxRow? _repayBox;
    private decimal _repayAmount;
    private HomeLogRow? _selectedRow;
    private string? _voidReason;

    public HomeBoxSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Finance, "صندوق المنزل", Icons.Currency, "#B45309",
               "الأرباح خارج العمل: الخمس والمساعدات وشراء الأصول، وقروض صناديق العمل وسدادها")
    {
        Kinds = new[]
        {
            new Option<HomeBoxEntryKind>(HomeBoxEntryKind.Khums, "الخمس"),
            new Option<HomeBoxEntryKind>(HomeBoxEntryKind.Aid, "مساعدات وتبرعات"),
            new Option<HomeBoxEntryKind>(HomeBoxEntryKind.Asset, "شراء ماكنات وأصول"),
            new Option<HomeBoxEntryKind>(HomeBoxEntryKind.Other, "أخرى")
        };
        _kind = Kinds[0];
        Categories = new[] { AllCategories }.Concat(HomeBoxService.Categories).ToList();
        SpendCommand = new AsyncRelayCommand(SpendAsync);
        SearchCommand = new AsyncRelayCommand(LoadLogAsync);
        PrintCommand = new AsyncRelayCommand(PrintAsync);
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        VoidCommand = new AsyncRelayCommand(VoidAsync);
        LendCommand = new AsyncRelayCommand(LendAsync);
        RepayCommand = new AsyncRelayCommand(RepayAsync);
        PrintLoansCommand = new AsyncRelayCommand(PrintLoansAsync);
    }

    protected override bool ReloadOnActivate => true;
    protected override bool HasPendingInput => Amount != 0 || LoanAmount != 0 || RepayAmount != 0;
    protected override void ResetInput() { Amount = 0; LoanAmount = 0; RepayAmount = 0; EntryTitle = Beneficiary = Notes = LoanReason = null; }

    public IReadOnlyList<Option<HomeBoxEntryKind>> Kinds { get; }
    public IReadOnlyList<string> Categories { get; }
    public ObservableCollection<HomeLogRow> Rows { get; } = new();
    public ObservableCollection<HomeLoanRow> Loans { get; } = new();
    public ObservableCollection<CashBoxRow> WorkBoxes { get; } = new();

    public AsyncRelayCommand SpendCommand { get; }
    public AsyncRelayCommand SearchCommand { get; }
    public AsyncRelayCommand PrintCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public AsyncRelayCommand VoidCommand { get; }
    public AsyncRelayCommand LendCommand { get; }
    public AsyncRelayCommand RepayCommand { get; }
    public AsyncRelayCommand PrintLoansCommand { get; }

    public DateTime From { get => _from; set => SetProperty(ref _from, value); }
    public DateTime To { get => _to; set => SetProperty(ref _to, value); }
    public string Category { get => _category; set => SetProperty(ref _category, value); }
    public string? Search { get => _search; set => SetProperty(ref _search, value); }
    public decimal MinAmount { get => _minAmount; set => SetProperty(ref _minAmount, value); }
    public decimal MaxAmount { get => _maxAmount; set => SetProperty(ref _maxAmount, value); }
    public decimal Opening { get => _opening; private set => SetProperty(ref _opening, value); }
    public decimal TotalIn { get => _totalIn; private set => SetProperty(ref _totalIn, value); }
    public decimal TotalOut { get => _totalOut; private set => SetProperty(ref _totalOut, value); }
    public decimal Closing { get => _closing; private set => SetProperty(ref _closing, value); }
    public decimal LoansOutstanding { get => _loansOutstanding; private set => SetProperty(ref _loansOutstanding, value); }
    public string ByCategory { get => _byCategory; private set => SetProperty(ref _byCategory, value); }
    public HomeLogRow? SelectedRow { get => _selectedRow; set => SetProperty(ref _selectedRow, value); }
    public string? VoidReason { get => _voidReason; set => SetProperty(ref _voidReason, value); }

    public Option<HomeBoxEntryKind> Kind
    {
        get => _kind;
        set { if (SetProperty(ref _kind, value)) OnPropertyChanged(nameof(KindHint)); }
    }
    public string KindHint => Kind.Value switch
    {
        HomeBoxEntryKind.Asset => "يُسجَّل أصلًا ثابتًا من أرباح مستثمرة: لا يدخل المصروفات ولا يخصم من ربح الشهر، ولا يمس رأس المال التشغيلي.",
        HomeBoxEntryKind.Khums => "توزيع من الأرباح: لا يدخل كلفة القنينة ولا صافي ربح الشهر.",
        HomeBoxEntryKind.Aid => "توزيع من الأرباح: لا يدخل كلفة القنينة ولا صافي ربح الشهر.",
        _ => "صرف خارج العمل: لا يدخل كلفة القنينة ولا صافي ربح الشهر."
    };
    public decimal Amount { get => _amount; set { if (SetProperty(ref _amount, value)) OnPropertyChanged(nameof(AmountWords)); } }
    public string AmountWords => Amount > 0 ? ArabicNumberWords.Amount(Amount) : "";
    public DateTime Date { get => _date; set => SetProperty(ref _date, value); }
    public string? EntryTitle { get => _entryTitle; set => SetProperty(ref _entryTitle, value); }
    public string? Beneficiary { get => _beneficiary; set => SetProperty(ref _beneficiary, value); }
    public string? Notes { get => _notes; set => SetProperty(ref _notes, value); }

    public CashBoxRow? LoanBox { get => _loanBox; set => SetProperty(ref _loanBox, value); }
    public decimal LoanAmount { get => _loanAmount; set => SetProperty(ref _loanAmount, value); }
    public string? LoanReason { get => _loanReason; set => SetProperty(ref _loanReason, value); }
    public HomeLoanRow? SelectedLoan { get => _selectedLoan; set { if (SetProperty(ref _selectedLoan, value)) OnPropertyChanged(nameof(HasOpenLoan)); } }
    public bool HasOpenLoan => SelectedLoan is { Status: HomeLoanStatus.Open };
    public CashBoxRow? RepayBox { get => _repayBox; set => SetProperty(ref _repayBox, value); }
    public decimal RepayAmount { get => _repayAmount; set => SetProperty(ref _repayAmount, value); }

    public override async Task LoadAsync()
    {
        await using (var db = Session.NewDb())
        {
            var keepLoan = SelectedLoan?.Id;
            var boxes = (await new CashBoxService(db).GetBoxesAsync(Session.UserId)).Where(b => b.BoxType is CashBoxType.Main or CashBoxType.User).ToList();
            WorkBoxes.Clear();
            foreach (var b in boxes) WorkBoxes.Add(b);
            LoanBox ??= WorkBoxes.FirstOrDefault(b => b.BoxType == CashBoxType.Main) ?? WorkBoxes.FirstOrDefault();
            RepayBox ??= WorkBoxes.FirstOrDefault(b => b.BoxType == CashBoxType.Main) ?? WorkBoxes.FirstOrDefault();
            var hb = new HomeBoxService(db);
            Loans.Clear();
            foreach (var l in await hb.LoansAsync()) Loans.Add(l);
            LoansOutstanding = await hb.OutstandingTotalAsync();
            SelectedLoan = Loans.FirstOrDefault(l => l.Id == keepLoan);
        }
        await LoadLogAsync();
    }

    private async Task LoadLogAsync()
    {
        await using var db = Session.NewDb();
        var res = await new HomeBoxService(db).LogAsync(From, To, Category == AllCategories ? null : Category, Search,
                                                        MinAmount > 0 ? MinAmount : null, MaxAmount > 0 ? MaxAmount : null);
        Rows.Clear();
        foreach (var r in res.Rows) Rows.Add(r);
        (Opening, TotalIn, TotalOut, Closing) = (res.Opening, res.TotalIn, res.TotalOut, res.Closing);
        ByCategory = string.Join("   |   ", res.ByCategory.Where(c => c.Out > 0 || c.In > 0).Select(c => $"{c.Category}: {(c.Out > 0 ? $"صرف {c.Out:N0}" : $"وارد {c.In:N0}")}"));
        StatusMessage = Rows.Count == 0 ? "لا حركات في هذه الفترة" : $"{Rows.Count} حركة";
    }

    private async Task SpendAsync()
    {
        if (Amount <= 0) { Dialogs.Error("أدخل المبلغ"); return; }
        if (!Dialogs.Confirm($"صرف {Amount:N0} د.ع من صندوق المنزل ({Kind.Label})؟ لن يدخل كلفة القنينة ولا صافي الربح.")) return;
        await using var db = Session.NewDb();
        var (r, entry) = await new HomeBoxService(db).SpendAsync(new HomeSpendInput(Kind.Value, Amount, Date, EntryTitle, Beneficiary, Notes), Session.UserId);
        if (!r.Success) { Dialogs.Error(r.ErrorMessage!); return; }
        StatusMessage = $"تم تسجيل {Kind.Label}: {Amount:N0} د.ع ({entry!.EntryNumber})";
        Amount = 0; EntryTitle = null; Beneficiary = null; Notes = null;
        Session.MarkDataChanged();
        await LoadAsync();
    }

    private async Task VoidAsync()
    {
        if (SelectedRow?.HomeEntryId is not int id) { Dialogs.Error("اختر سطر صرف من السجل لإلغائه"); return; }
        if (string.IsNullOrWhiteSpace(VoidReason)) { Dialogs.Error("اكتب سبب الإلغاء"); return; }
        if (!Dialogs.Confirm($"إلغاء {SelectedRow.TxNumber}؟ يبقى في السجل مشطوبًا ويُعكس قيده.")) return;
        await using var db = Session.NewDb();
        var r = await new HomeBoxService(db).VoidEntryAsync(id, VoidReason, Session.UserId);
        if (!r.Success) { Dialogs.Error(r.ErrorMessage!); return; }
        VoidReason = null;
        Session.MarkDataChanged();
        await LoadAsync();
    }

    private async Task LendAsync()
    {
        if (LoanBox is null) { Dialogs.Error("اختر صندوق العمل المقترض"); return; }
        if (LoanAmount <= 0) { Dialogs.Error("أدخل مبلغ القرض"); return; }
        if (!Dialogs.Confirm($"إقراض \"{LoanBox.Name}\" مبلغ {LoanAmount:N0} د.ع من صندوق المنزل؟ لا أثر له على الأرباح ويُسجَّل دينًا يُسدَّد عند الوفرة.")) return;
        await using var db = Session.NewDb();
        var (r, loan) = await new HomeBoxService(db).LendAsync(LoanBox.Id, LoanAmount, Date, LoanReason, Session.UserId);
        if (!r.Success) { Dialogs.Error(r.ErrorMessage!); return; }
        StatusMessage = $"تم القرض {loan!.LoanNumber}: {LoanAmount:N0} د.ع إلى {LoanBox.Name}";
        LoanAmount = 0; LoanReason = null;
        Session.MarkDataChanged();
        await LoadAsync();
    }

    private async Task RepayAsync()
    {
        if (SelectedLoan is not { Status: HomeLoanStatus.Open }) { Dialogs.Error("اختر قرضًا قائمًا من الجدول"); return; }
        if (RepayBox is null) { Dialogs.Error("اختر الصندوق الذي يُسدَّد منه"); return; }
        if (RepayAmount <= 0) { Dialogs.Error("أدخل مبلغ السداد"); return; }
        if (!Dialogs.Confirm($"سداد {RepayAmount:N0} د.ع من \"{RepayBox.Name}\" لصندوق المنزل (القرض {SelectedLoan.LoanNumber})؟")) return;
        await using var db = Session.NewDb();
        var r = await new HomeBoxService(db).RepayAsync(SelectedLoan.Id, RepayAmount, RepayBox.Id, Date, null, Session.UserId);
        if (!r.Success) { Dialogs.Error(r.ErrorMessage!); return; }
        StatusMessage = $"تم السداد: {RepayAmount:N0} د.ع";
        RepayAmount = 0;
        Session.MarkDataChanged();
        await LoadAsync();
    }

    // ============================ الطباعة والتصدير ============================

    public ReportDocument BuildLogReport()
    {
        var r = new ReportDocument { CompanyName = Session.ProjectName, Title = "كشف صندوق المنزل", PrintedBy = Session.Username };
        r.Field("الفترة", $"{From:yyyy/MM/dd} — {To:yyyy/MM/dd}").Field("النوع", Category).Field("بحث", Search);
        r.Columns.AddRange(new[] { "التاريخ", "الرقم", "النوع", "الجهة", "البيان", "داخل", "خارج" });
        foreach (var x in Rows)
            r.Rows.Add(new[]
            {
                x.TxDate.ToString("yyyy/MM/dd"), x.TxNumber, x.Category + (x.IsVoided ? " (ملغاة)" : ""), x.Party ?? "", x.Description ?? "",
                x.In == 0 ? "" : $"{x.In:N0}", x.Out == 0 ? "" : $"{x.Out:N0}"
            });
        r.Total("رصيد أول المدة", $"{Opening:N0} د.ع").Total("مجموع الداخل", $"{TotalIn:N0} د.ع").Total("مجموع الخارج", $"{TotalOut:N0} د.ع")
         .Total("الرصيد", $"{Closing:N0} د.ع", true);
        if (!string.IsNullOrWhiteSpace(ByCategory)) r.Total("حسب النوع", ByCategory);
        r.Signatures.AddRange(new[] { "أمين الصندوق", "المحاسب", "المدير" });
        return r;
    }

    private Task PrintAsync() { Dialogs.ShowReport(BuildLogReport()); return Task.CompletedTask; }

    /// <summary>تصدير السجل المعروض كملف CSV (يفتح في Excel) في مجلد المستندات.</summary>
    public string? ExportCsv(string? folder = null)
    {
        var sb = new StringBuilder("التاريخ,الرقم,النوع,الجهة,البيان,داخل,خارج,المنشئ,ملغاة\r\n");
        static string Q(string? v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
        foreach (var x in Rows)
            sb.Append(string.Join(",", x.TxDate.ToString("yyyy-MM-dd"), Q(x.TxNumber), Q(x.Category), Q(x.Party), Q(x.Description), x.In, x.Out, Q(x.CreatedBy), x.IsVoided ? "نعم" : "")).Append("\r\n");
        var dir = folder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(dir)) dir = Path.GetTempPath();
        var path = Path.Combine(dir, $"صندوق-المنزل-{From:yyyyMMdd}-{To:yyyyMMdd}.csv");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }

    private Task ExportAsync()
    {
        var path = ExportCsv();
        StatusMessage = $"صُدِّر السجل إلى {path}";
        Dialogs.OpenUrl(path!);
        return Task.CompletedTask;
    }

    private Task PrintLoansAsync()
    {
        var r = new ReportDocument { CompanyName = Session.ProjectName, Title = "كشف قروض صندوق المنزل", PrintedBy = Session.Username };
        r.Columns.AddRange(new[] { "الرقم", "الصندوق", "التاريخ", "السبب", "المبلغ", "المسدَّد", "المتبقي", "الأيام", "الحالة" });
        foreach (var l in Loans)
            r.Rows.Add(new[] { l.LoanNumber, l.BoxName, l.LoanDate.ToString("yyyy/MM/dd"), l.Reason, $"{l.Amount:N0}", $"{l.Repaid:N0}", $"{l.Remaining:N0}",
                               l.Status == HomeLoanStatus.Open ? l.DaysOpen.ToString() : "", l.StatusText });
        r.Total("إجمالي المتبقي على صناديق العمل", $"{LoansOutstanding:N0} د.ع", true);
        r.Signatures.AddRange(new[] { "المحاسب", "المدير" });
        Dialogs.ShowReport(r);
        return Task.CompletedTask;
    }
}
