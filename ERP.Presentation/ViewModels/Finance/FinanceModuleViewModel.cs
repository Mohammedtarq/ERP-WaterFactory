using System.Collections.ObjectModel;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Finance;

public class FinanceModuleViewModel : ModuleViewModel
{
    public FinanceModuleViewModel(AppSession s, IDialogService d)
        : base("المالية", Icons.Finance, ModuleColors.Finance)
    {
        UseDashboard(s, d, ModuleCode.Finance, ModuleDashboardViewModel.Finance);
        Boxes = Add(new CashBoxesSectionViewModel(s, d));
        Reconciliation = Add(new ReconciliationSectionViewModel(s, d));
        Partners = Add(new PartnersSectionViewModel(s, d));
        Add(new ChartOfAccountsSectionViewModel(s, d));
        Add(new JournalEntriesSectionViewModel(s, d));
        Add(new VouchersSectionViewModel(s, d));
        Add(new MappingRulesSectionViewModel(s, d));
        Add(new ExchangeRatesSectionViewModel(s, d));
    }

    public CashBoxesSectionViewModel Boxes { get; }
    public ReconciliationSectionViewModel Reconciliation { get; }
    public PartnersSectionViewModel Partners { get; }
}

// ============================ دليل الحسابات ============================
public class ChartOfAccountsSectionViewModel : CrudSectionViewModel<ChartOfAccount>
{
    public ChartOfAccountsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Finance, "دليل الحسابات", Icons.Accounts, "#8B5CF6", "الحسابات الرئيسية والفرعية") { }

    public IReadOnlyList<Option<AccountType>> TypeOptions { get; } = ArabicLabels.OptionsOf<AccountType>();
    public ObservableCollection<ChartOfAccount> ParentOptions { get; } = new();

    protected override int GetId(ChartOfAccount e) => e.Id;
    protected override string Describe(ChartOfAccount e) => $"{e.AccountCode} — {e.AccountName}";
    protected override Task<List<ChartOfAccount>> QueryAsync(ProjectDbContext db) =>
        db.ChartOfAccounts.AsNoTracking().Include(a => a.ParentAccount).OrderBy(a => a.AccountCode).ToListAsync();

    protected override void OnEditorChanged()
    {
        ParentOptions.Clear();
        if (Editor is null) return;
        foreach (var a in Items.Where(a => a.Id != Editor.Id)) ParentOptions.Add(a);
    }

    protected override string? Validate(ChartOfAccount e)
    {
        if (string.IsNullOrWhiteSpace(e.AccountCode)) return "أدخل رمز الحساب";
        if (string.IsNullOrWhiteSpace(e.AccountName)) return "أدخل اسم الحساب";
        return null;
    }

    protected override Task BeforeSaveAsync(ProjectDbContext db, ChartOfAccount e)
    {
        e.AccountCode = e.AccountCode.Trim();
        e.AccountName = e.AccountName.Trim();
        return Task.CompletedTask;
    }
}

// ============================ القيود ============================
public class JournalLineInput : ObservableObject
{
    private ChartOfAccount? _account;
    private decimal _debit;
    private decimal _credit;
    private readonly Action _changed;

    public JournalLineInput(Action changed) => _changed = changed;

    public ChartOfAccount? Account { get => _account; set => SetProperty(ref _account, value); }
    public decimal Debit { get => _debit; set { if (SetProperty(ref _debit, value)) _changed(); } }
    public decimal Credit { get => _credit; set { if (SetProperty(ref _credit, value)) _changed(); } }
}

public class JournalEntryRow
{
    public int Id { get; init; }
    public string EntryNumber { get; init; } = "";
    public DateTime EntryDate { get; init; }
    public string TypeLabel { get; init; } = "";
    public string? Description { get; init; }
    public decimal Total { get; init; }
    public bool IsPosted { get; init; }
}

public class JournalLineRow
{
    public string AccountCode { get; init; } = "";
    public string AccountName { get; init; } = "";
    public decimal Debit { get; init; }
    public decimal Credit { get; init; }
    public string? Description { get; init; }
}

public class JournalEntriesSectionViewModel : SectionViewModel
{
    protected override bool HasPendingInput => IsComposing;

    private JournalEntryRow? _selectedEntry;
    private bool _isComposing;
    private DateTime _entryDate = DateTime.Today;
    private string? _description;

    public JournalEntriesSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Finance, "القيود المحاسبية", Icons.Journal, "#6366F1", "القيود اليدوية والتلقائية مع فحص التوازن")
    {
        NewEntryCommand = new RelayCommand(StartNew);
        AddLineCommand = new RelayCommand(() => NewLines.Add(new JournalLineInput(RaiseTotals)));
        RemoveLineCommand = new RelayCommand(p => { if (p is JournalLineInput l) { NewLines.Remove(l); RaiseTotals(); } });
        PostCommand = new AsyncRelayCommand(PostAsync);
        CancelCommand = new RelayCommand(() => IsComposing = false);
        PrintCommand = new AsyncRelayCommand(p => p is JournalEntryRow r ? PrintAsync(db => DocumentReports.JournalEntryAsync(Session, db, r.Id)) : Task.CompletedTask);
    }

    public AsyncRelayCommand PrintCommand { get; }
    public ObservableCollection<JournalEntryRow> Entries { get; } = new();
    public ObservableCollection<JournalLineRow> SelectedLines { get; } = new();
    public ObservableCollection<ChartOfAccount> Accounts { get; } = new();
    public ObservableCollection<JournalLineInput> NewLines { get; } = new();

    public JournalEntryRow? SelectedEntry
    {
        get => _selectedEntry;
        set { if (SetProperty(ref _selectedEntry, value)) Background(LoadLinesAsync()); }
    }

    public bool IsComposing { get => _isComposing; private set => SetProperty(ref _isComposing, value); }
    public DateTime EntryDate { get => _entryDate; set => SetProperty(ref _entryDate, value); }
    public string? EntryDescription { get => _description; set => SetProperty(ref _description, value); }

    public decimal TotalDebit => NewLines.Sum(l => l.Debit);
    public decimal TotalCredit => NewLines.Sum(l => l.Credit);
    public decimal Difference => TotalDebit - TotalCredit;
    public bool IsBalanced => TotalDebit > 0 && Difference == 0;
    public string BalanceText => IsBalanced ? "القيد متوازن ✓" : $"الفرق: {Difference:N2}";

    public RelayCommand NewEntryCommand { get; }
    public RelayCommand AddLineCommand { get; }
    public RelayCommand RemoveLineCommand { get; }
    public AsyncRelayCommand PostCommand { get; }
    public RelayCommand CancelCommand { get; }

    private void RaiseTotals()
    {
        OnPropertyChanged(nameof(TotalDebit));
        OnPropertyChanged(nameof(TotalCredit));
        OnPropertyChanged(nameof(Difference));
        OnPropertyChanged(nameof(IsBalanced));
        OnPropertyChanged(nameof(BalanceText));
    }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        Accounts.Clear();
        foreach (var a in await db.ChartOfAccounts.AsNoTracking().Where(a => a.IsActive).OrderBy(a => a.AccountCode).ToListAsync()) Accounts.Add(a);

        var rows = await db.JournalEntries.AsNoTracking().OrderByDescending(j => j.EntryDate).ThenByDescending(j => j.Id).Take(300)
            .Select(j => new { j.Id, j.EntryNumber, j.EntryDate, j.EntryType, j.Description, j.IsPosted, Total = j.Lines.Sum(l => l.Debit) })
            .ToListAsync();
        Entries.Clear();
        foreach (var j in rows)
            Entries.Add(new JournalEntryRow { Id = j.Id, EntryNumber = j.EntryNumber, EntryDate = j.EntryDate, TypeLabel = ArabicLabels.Of(j.EntryType),
                                              Description = j.Description, IsPosted = j.IsPosted, Total = j.Total });
    }

    private async Task LoadLinesAsync()
    {
        SelectedLines.Clear();
        if (SelectedEntry is null) return;
        await using var db = Session.NewDb();
        foreach (var l in await db.JournalEntryLines.AsNoTracking().Where(l => l.JournalEntryId == SelectedEntry.Id)
                     .Select(l => new JournalLineRow { AccountCode = l.Account.AccountCode, AccountName = l.Account.AccountName,
                                                       Debit = l.Debit, Credit = l.Credit, Description = l.Description })
                     .ToListAsync())
            SelectedLines.Add(l);
    }

    private void StartNew()
    {
        if (!Require(CanAdd, "إضافة القيود")) return;
        NewLines.Clear();
        NewLines.Add(new JournalLineInput(RaiseTotals));
        NewLines.Add(new JournalLineInput(RaiseTotals));
        EntryDate = DateTime.Today;
        EntryDescription = null;
        RaiseTotals();
        IsComposing = true;
    }

    private async Task PostAsync()
    {
        if (!Require(CanPost, "ترحيل القيود")) return;
        if (NewLines.Any(l => (l.Debit != 0 || l.Credit != 0) && l.Account is null)) { Dialogs.Error("اختر الحساب لكل سطر فيه مبلغ"); return; }
        if (NewLines.Any(l => l.Debit < 0 || l.Credit < 0 || (l.Debit > 0 && l.Credit > 0)))
        { Dialogs.Error("كل سطر إما مدين أو دائن بمبلغ موجب، لا الاثنان معًا"); return; }

        var lines = NewLines.Where(l => l.Account is not null && (l.Debit > 0 || l.Credit > 0))
                            .Select(l => (l.Account!.Id, l.Debit, l.Credit)).ToList();
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new FinanceService(db).PostManualJournalEntryAsync(EntryDate, EntryDescription, Session.UserId, lines),
                                    "تم ترحيل القيد اليدوي"))
        {
            IsComposing = false;
            await LoadAsync();
        }
    }
}

// ============================ السندات ============================
public class VoucherRow
{
    public int Id { get; init; }
    public string VoucherNumber { get; init; } = "";
    public DateTime VoucherDate { get; init; }
    public string TypeLabel { get; init; } = "";
    public string PartyLabel { get; init; } = "";
    public decimal Amount { get; init; }
    public string MethodLabel { get; init; } = "";
    public string? Notes { get; init; }
    public string? EntryNumber { get; init; }
}

public record PartyOption(int? Id, string Name);

public class VouchersSectionViewModel : SectionViewModel
{
    protected override bool HasPendingInput => Amount != 0;

    private Option<VoucherType> _voucherType;
    private Option<VoucherPartyType> _partyType;
    private PartyOption? _party;
    private decimal _amount;
    private Option<PaymentMethod> _paymentMethod;
    private DateTime _voucherDate = DateTime.Today;
    private AccountMappingRule? _mappingRule;
    private string? _notes;

    public VouchersSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Finance, "السندات", Icons.Voucher, "#10B981", "سندات القبض والصرف — القيد يُنشأ تلقائيًا")
    {
        _voucherType = VoucherTypes[0];
        _partyType = PartyTypes[0];
        _paymentMethod = PaymentMethods[0];
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        PrintCommand = new AsyncRelayCommand(p => p is VoucherRow r ? PrintAsync(db => DocumentReports.VoucherAsync(Session, db, r.Id)) : Task.CompletedTask);
    }

    public AsyncRelayCommand PrintCommand { get; }

    public IReadOnlyList<Option<VoucherType>> VoucherTypes { get; } = ArabicLabels.OptionsOf<VoucherType>();
    public IReadOnlyList<Option<VoucherPartyType>> PartyTypes { get; } = ArabicLabels.OptionsOf<VoucherPartyType>();
    public IReadOnlyList<Option<PaymentMethod>> PaymentMethods { get; } = ArabicLabels.OptionsOf<PaymentMethod>();
    public ObservableCollection<PartyOption> Parties { get; } = new();
    public ObservableCollection<AccountMappingRule> MappingRules { get; } = new();
    public ObservableCollection<VoucherRow> Vouchers { get; } = new();

    public Option<VoucherType> VoucherType { get => _voucherType; set { if (SetProperty(ref _voucherType, value)) SuggestRule(); } }
    public Option<VoucherPartyType> PartyType { get => _partyType; set { if (SetProperty(ref _partyType, value)) Background(LoadPartiesAsync()); } }
    public PartyOption? Party { get => _party; set => SetProperty(ref _party, value); }
    public decimal Amount { get => _amount; set => SetProperty(ref _amount, value); }
    public Option<PaymentMethod> PaymentMethod { get => _paymentMethod; set => SetProperty(ref _paymentMethod, value); }
    public DateTime VoucherDate { get => _voucherDate; set => SetProperty(ref _voucherDate, value); }
    public AccountMappingRule? MappingRule { get => _mappingRule; set { if (SetProperty(ref _mappingRule, value)) OnPropertyChanged(nameof(RulePreview)); } }
    public string? Notes { get => _notes; set => SetProperty(ref _notes, value); }

    /// <summary>ما سيفعله العقل المالي — يُعرض للموظف قبل الحفظ دون حاجة لخبرة محاسبية.</summary>
    public string RulePreview => MappingRule is null ? "اختر نوع العملية" :
        $"سيُنشأ قيد تلقائي: مدين {MappingRule.DebitAccount?.AccountName} / دائن {MappingRule.CreditAccount?.AccountName}";

    public AsyncRelayCommand SaveCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        MappingRules.Clear();
        foreach (var r in await db.AccountMappingRules.AsNoTracking().Include(r => r.DebitAccount).Include(r => r.CreditAccount)
                     .OrderBy(r => r.TransactionType).ToListAsync())
            MappingRules.Add(r);
        SuggestRule();
        await LoadPartiesAsync();

        var customers = await db.Customers.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name);
        var suppliers = await db.Suppliers.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name);
        var employees = await db.Employees.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.FullName);
        var rows = await db.Vouchers.AsNoTracking().Include(v => v.JournalEntry).OrderByDescending(v => v.VoucherDate).ThenByDescending(v => v.Id)
            .Take(300).ToListAsync();
        Vouchers.Clear();
        foreach (var v in rows)
        {
            string name = v.PartyId is int id ? v.PartyType switch
            {
                VoucherPartyType.Customer => customers.GetValueOrDefault(id, ""),
                VoucherPartyType.Supplier => suppliers.GetValueOrDefault(id, ""),
                VoucherPartyType.Employee => employees.GetValueOrDefault(id, ""),
                _ => ""
            } : "";
            Vouchers.Add(new VoucherRow
            {
                Id = v.Id, VoucherNumber = v.VoucherNumber, VoucherDate = v.VoucherDate, TypeLabel = ArabicLabels.Of(v.VoucherType),
                PartyLabel = $"{ArabicLabels.Of(v.PartyType)}{(name.Length > 0 ? ": " + name : "")}", Amount = v.Amount,
                MethodLabel = ArabicLabels.Of(v.PaymentMethod), Notes = v.Notes, EntryNumber = v.JournalEntry?.EntryNumber
            });
        }
    }

    private void SuggestRule()
    {
        var preferred = VoucherType.Value == Data.ProjectDb.Entities.VoucherType.Receipt ? "CashReceiptVoucher" : "CashPaymentVoucher";
        MappingRule = MappingRules.FirstOrDefault(r => r.TransactionType == preferred) ?? MappingRule;
    }

    private async Task LoadPartiesAsync()
    {
        Parties.Clear();
        await using var db = Session.NewDb();
        IEnumerable<PartyOption> list = PartyType.Value switch
        {
            VoucherPartyType.Customer => await db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).Select(c => new PartyOption(c.Id, c.Name)).ToListAsync(),
            VoucherPartyType.Supplier => await db.Suppliers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).Select(c => new PartyOption(c.Id, c.Name)).ToListAsync(),
            VoucherPartyType.Employee => await db.Employees.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.FullName).Select(c => new PartyOption(c.Id, c.FullName)).ToListAsync(),
            _ => new[] { new PartyOption(null, "أخرى (بدون طرف محدد)") }
        };
        foreach (var p in list) Parties.Add(p);
        Party = Parties.FirstOrDefault();
    }

    private async Task SaveAsync()
    {
        if (!Require(CanAdd, "إضافة السندات")) return;
        if (Amount <= 0) { Dialogs.Error("أدخل مبلغًا أكبر من صفر"); return; }
        if (MappingRule is null) { Dialogs.Error("اختر نوع العملية (قاعدة الربط) — أضفها من تبويب العقل المالي إن لم توجد"); return; }
        if (PartyType.Value != VoucherPartyType.Other && Party?.Id is null) { Dialogs.Error("اختر الطرف"); return; }

        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new FinanceService(db).CreateVoucherAsync(
                VoucherType.Value, PartyType.Value, Party?.Id, Amount, PaymentMethod.Value, VoucherDate,
                MappingRule.TransactionType, Session.UserId, Notes),
            $"تم حفظ {VoucherType.Label} بمبلغ {Amount:N0} مع قيده التلقائي"))
        {
            Amount = 0;
            Notes = null;
            await LoadAsync();
        }
    }
}

// ============================ العقل المالي (قواعد الربط) ============================
public class MappingRulesSectionViewModel : CrudSectionViewModel<AccountMappingRule>
{
    public MappingRulesSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Finance, "العقل المالي", Icons.Brain, "#F59E0B", "الحسابان الافتراضيان لكل نوع عملية") { }

    public ObservableCollection<ChartOfAccount> Accounts { get; } = new();

    /// <summary>أنواع العمليات التي يستخدمها النظام فعليًا، مع شرح كل واحدة.</summary>
    public IReadOnlyList<Option<string>> KnownTypes { get; } = new[]
    {
        new Option<string>("CashReceiptVoucher", "CashReceiptVoucher — سند قبض نقدي"),
        new Option<string>("CashPaymentVoucher", "CashPaymentVoucher — سند صرف نقدي"),
        new Option<string>("SupplierAdvancePayment", "SupplierAdvancePayment — دفعة مقدمة لمورد"),
        new Option<string>("GoodsReceiptOnAccount", "GoodsReceiptOnAccount — استلام بضاعة على الحساب"),
        new Option<string>("SupplierAdvanceOffset", "SupplierAdvanceOffset — تسوية دفعة مقدمة"),
        new Option<string>("SalesInvoiceCash", "SalesInvoiceCash — فاتورة مبيعات نقدية"),
        new Option<string>("SalesInvoiceCredit", "SalesInvoiceCredit — فاتورة مبيعات آجلة"),
        new Option<string>("SalesInvoiceElectronic", "SalesInvoiceElectronic — فاتورة دفع إلكتروني"),
        new Option<string>("SalesInvoiceRepCash", "SalesInvoiceRepCash — نقد مندوب (كاش فان)"),
        new Option<string>("SalesTax", "SalesTax — ضريبة المبيعات (الدائن فقط)"),
        new Option<string>("LoadingSuppliesCharge", "LoadingSuppliesCharge — مستلزمات التحميل (الدائن فقط)"),
    };

    public IEnumerable<string> MissingTypes =>
        KnownTypes.Select(k => k.Value).Where(t => Items.All(r => r.TransactionType != t));
    public string MissingText => MissingTypes.Any() ? "قواعد غير معرّفة بعد: " + string.Join("، ", MissingTypes) : "كل القواعد المطلوبة معرّفة ✓";

    protected override int GetId(AccountMappingRule e) => e.Id;
    protected override string Describe(AccountMappingRule e) => e.TransactionType;

    protected override async Task LoadLookupsAsync(ProjectDbContext db)
    {
        Accounts.Clear();
        foreach (var a in await db.ChartOfAccounts.AsNoTracking().Where(a => a.IsActive).OrderBy(a => a.AccountCode).ToListAsync()) Accounts.Add(a);
    }

    protected override async Task<List<AccountMappingRule>> QueryAsync(ProjectDbContext db)
    {
        var list = await db.AccountMappingRules.AsNoTracking().Include(r => r.DebitAccount).Include(r => r.CreditAccount)
            .OrderBy(r => r.TransactionType).ToListAsync();
        OnPropertyChanged(nameof(MissingText));
        return list;
    }

    public override async Task LoadAsync()
    {
        await base.LoadAsync();
        OnPropertyChanged(nameof(MissingText));
    }

    protected override string? Validate(AccountMappingRule e)
    {
        if (string.IsNullOrWhiteSpace(e.TransactionType)) return "اختر أو اكتب نوع العملية";
        if (e.DebitAccountId == 0 || e.CreditAccountId == 0) return "اختر الحساب المدين والحساب الدائن";
        return null;
    }

    protected override Task BeforeSaveAsync(ProjectDbContext db, AccountMappingRule e)
    {
        e.TransactionType = e.TransactionType.Trim();
        return Task.CompletedTask;
    }
}

// ============================ أسعار الصرف ============================
public class ExchangeRatesSectionViewModel : CrudSectionViewModel<ExchangeRate>
{
    public ExchangeRatesSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Finance, "أسعار الصرف", Icons.Currency, "#0EA5E9", "سعر الدولار بالدينار بتاريخ سريان (لرواتب الدولار)") { }

    protected override int GetId(ExchangeRate e) => e.Id;
    protected override string Describe(ExchangeRate e) => $"{e.CurrencyCode} = {e.RateToIQD:N0} د.ع من {e.EffectiveDate:yyyy/MM/dd}";
    protected override Task<List<ExchangeRate>> QueryAsync(ProjectDbContext db) =>
        db.ExchangeRates.AsNoTracking().Include(r => r.EnteredByUser).OrderByDescending(r => r.EffectiveDate).ToListAsync();
    protected override ExchangeRate CreateNew() => new() { EffectiveDate = DateTime.Today, CurrencyCode = "USD" };

    protected override string? Validate(ExchangeRate e)
    {
        if (e.RateToIQD <= 0) return "أدخل سعرًا أكبر من صفر";
        if (string.IsNullOrWhiteSpace(e.CurrencyCode) || e.CurrencyCode.Trim().Length != 3) return "رمز العملة من 3 أحرف (مثل USD)";
        return null;
    }

    protected override Task BeforeSaveAsync(ProjectDbContext db, ExchangeRate e)
    {
        e.CurrencyCode = e.CurrencyCode.Trim().ToUpperInvariant();
        e.EnteredByUserId = Session.UserId;
        return Task.CompletedTask;
    }
}
