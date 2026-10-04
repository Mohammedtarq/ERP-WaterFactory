using System.Collections.ObjectModel;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Finance;

// ============================ المصروف ============================
/// <summary>
/// شاشة «مصروف» واحدة: النوع (تشغيلي / غير تشغيلي / إيراد آخر)، المبلغ، السيارة أو القسم، ورقم الوصل.
/// الصندوق تلقائيًا من المستخدم والقيد في الخلفية. لا حذف: إلغاء بسبب.
/// </summary>
public class ExpensesSectionViewModel : SectionViewModel
{
    private FinanceCategory? _category;
    private decimal _amount;
    private DateTime _date = DateTime.Today;
    private Vehicle? _vehicle;
    private Department? _department;
    private string? _partyName;
    private string? _receiptNumber;
    private string? _notes;
    private DateTime _from = DateTime.Today.AddDays(1 - DateTime.Today.Day);
    private DateTime _to = DateTime.Today;
    private FinanceCategory? _filterCategory;
    private string? _voidReason;

    public ExpensesSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Finance, "المصروفات", Icons.Voucher, "#DC2626",
               "مصروف أو إيراد آخر من صندوقك مباشرة: النوع، السيارة أو القسم، ورقم الوصل — والقيد يُنشأ تلقائيًا")
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        LoadListCommand = new AsyncRelayCommand(LoadListAsync);
        VoidCommand = new AsyncRelayCommand(p => p is FinanceEntryRow r ? VoidAsync(r) : Task.CompletedTask);
        PrintCommand = new RelayCommand(Print);
        ClearVehicleCommand = new RelayCommand(() => Vehicle = null);
        ClearDepartmentCommand = new RelayCommand(() => Department = null);
        ClearFilterCommand = new RelayCommand(() => FilterCategory = null);
    }

    protected override bool ReloadOnActivate => true;
    protected override bool HasPendingInput => Amount > 0;

    public ObservableCollection<FinanceCategory> Categories { get; } = new();
    public ObservableCollection<Vehicle> Vehicles { get; } = new();
    public ObservableCollection<Department> Departments { get; } = new();
    public ObservableCollection<FinanceEntryRow> Entries { get; } = new();

    public FinanceCategory? Category { get => _category; set { if (SetProperty(ref _category, value)) OnPropertyChanged(nameof(KindHint)); } }
    public string KindHint => Category is null ? "" : ArabicLabels.Of(Category.Kind);
    public decimal Amount { get => _amount; set => SetProperty(ref _amount, value); }
    public DateTime Date { get => _date; set => SetProperty(ref _date, value); }
    public Vehicle? Vehicle { get => _vehicle; set => SetProperty(ref _vehicle, value); }
    public Department? Department { get => _department; set => SetProperty(ref _department, value); }
    public string? PartyName { get => _partyName; set => SetProperty(ref _partyName, value); }
    public string? ReceiptNumber { get => _receiptNumber; set => SetProperty(ref _receiptNumber, value); }
    public string? Notes { get => _notes; set => SetProperty(ref _notes, value); }
    public DateTime From { get => _from; set => SetProperty(ref _from, value); }
    public DateTime To { get => _to; set => SetProperty(ref _to, value); }
    public FinanceCategory? FilterCategory { get => _filterCategory; set { if (SetProperty(ref _filterCategory, value)) Background(LoadListAsync()); } }
    public string? VoidReason { get => _voidReason; set => SetProperty(ref _voidReason, value); }

    private IEnumerable<FinanceEntryRow> Active => Entries.Where(e => !e.IsVoided);
    public decimal OperatingTotal => Active.Where(e => e.Kind == FinanceCategoryKind.Operating).Sum(e => e.Amount);
    public decimal NonOperatingTotal => Active.Where(e => e.Kind == FinanceCategoryKind.NonOperating).Sum(e => e.Amount);
    public decimal IncomeTotal => Active.Where(e => e.Kind == FinanceCategoryKind.OtherIncome).Sum(e => e.Amount);

    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand LoadListCommand { get; }
    public AsyncRelayCommand VoidCommand { get; }
    public RelayCommand PrintCommand { get; }
    public RelayCommand ClearVehicleCommand { get; }
    public RelayCommand ClearDepartmentCommand { get; }
    public RelayCommand ClearFilterCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var categoryId = Category?.Id;
        Categories.Clear();
        foreach (var c in await new FinanceEntryService(db).GetCategoriesAsync()) Categories.Add(c);
        Vehicles.Clear();
        foreach (var v in await db.Vehicles.AsNoTracking().Where(v => v.IsActive).OrderBy(v => v.VehicleName).ToListAsync()) Vehicles.Add(v);
        Departments.Clear();
        foreach (var dep in await db.Departments.AsNoTracking().OrderBy(x => x.Name).ToListAsync()) Departments.Add(dep);
        _category = Categories.FirstOrDefault(c => c.Id == categoryId) ?? Categories.FirstOrDefault();
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(KindHint));
        await LoadListAsync();
    }

    private async Task LoadListAsync()
    {
        await using var db = Session.NewDb();
        Entries.Clear();
        foreach (var e in await new FinanceEntryService(db).ListAsync(From, To, FilterCategory?.Id)) Entries.Add(e);
        OnPropertyChanged(nameof(OperatingTotal));
        OnPropertyChanged(nameof(NonOperatingTotal));
        OnPropertyChanged(nameof(IncomeTotal));
    }

    private async Task SaveAsync()
    {
        if (!Require(CanAdd, "المصروفات")) return;
        if (Category is null) { Dialogs.Error("اختر نوع المصروف"); return; }
        if (Amount <= 0) { Dialogs.Error("المبلغ يجب أن يكون أكبر من صفر"); return; }
        await using var db = Session.NewDb();
        FinanceEntry? entry = null;
        if (await RunOperationAsync(async () =>
            {
                var (r, e) = await new FinanceEntryService(db).CreateAsync(new FinanceEntryInput(Category.Id, Amount, Date, Vehicle?.Id, Department?.Id,
                                                                                                 PartyName, ReceiptNumber, Notes), Session.UserId);
                entry = e;
                return r;
            }, Category.Kind == FinanceCategoryKind.OtherIncome ? "سُجّل الإيراد ودخل صندوقك" : "سُجّل المصروف وخرج من صندوقك"))
        {
            StatusMessage = $"{StatusMessage} — {entry!.EntryNumber}";
            Amount = 0;
            PartyName = null;
            ReceiptNumber = null;
            Notes = null;
            await LoadListAsync();
        }
    }

    private async Task VoidAsync(FinanceEntryRow row)
    {
        if (row.IsVoided) { Dialogs.Error("ملغى مسبقًا"); return; }
        if (string.IsNullOrWhiteSpace(VoidReason)) { Dialogs.Error("اكتب سبب الإلغاء في الحقل أعلى القائمة"); return; }
        if (!Dialogs.Confirm($"إلغاء {row.EntryNumber} ({row.Category} — {row.Amount:N0})؟ يُعكس القيد وحركة الصندوق.")) return;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new FinanceEntryService(db).VoidAsync(row.Id, VoidReason!, Session.UserId), $"أُلغي {row.EntryNumber}"))
        {
            VoidReason = null;
            await LoadListAsync();
        }
    }

    private void Print()
    {
        var r = new ReportDocument { Key = "expenses", CompanyName = Session.ProjectName, Title = "كشف المصروفات والإيرادات الأخرى", PrintedBy = Session.FullName };
        r.Field("من", From.ToString("yyyy/MM/dd")).Field("إلى", To.ToString("yyyy/MM/dd")).Field("النوع", FilterCategory?.Name ?? "الكل");
        r.Columns.AddRange(new[] { "الرقم", "التاريخ", "النوع", "التصنيف", "المبلغ", "السيارة / القسم", "الجهة", "الوصل" });
        foreach (var e in Active)
            r.Rows.Add(new[] { e.EntryNumber, e.EntryDate.ToString("yyyy/MM/dd"), e.Category, e.KindText, $"{e.Amount:N0}",
                               e.Vehicle ?? e.Department ?? "", e.PartyName ?? "", e.ReceiptNumber ?? "" });
        r.Total("مصاريف تشغيلية", $"{OperatingTotal:N0} د.ع");
        r.Total("مصاريف غير تشغيلية", $"{NonOperatingTotal:N0} د.ع");
        r.Total("إيرادات أخرى", $"{IncomeTotal:N0} د.ع", true);
        Dialogs.ShowReport(r);
    }
}

// ============================ أنواع المصروف ============================
public class FinanceCategoriesSectionViewModel : CrudSectionViewModel<FinanceCategory>
{
    public FinanceCategoriesSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Finance, "أنواع المصروف", Icons.List, "#F97316",
               "تشغيلي يدخل كلفة القنينة، وغير تشغيلي (توسعة، مكائن) يُطرح من ربح الشهر فقط، وإيرادات أخرى") { }

    public IReadOnlyList<Option<FinanceCategoryKind>> KindOptions { get; } = ArabicLabels.OptionsOf<FinanceCategoryKind>();
    public ObservableCollection<ChartOfAccount> Accounts { get; } = new();

    protected override int GetId(FinanceCategory e) => e.Id;
    protected override string Describe(FinanceCategory e) => e.Name;

    protected override async Task LoadLookupsAsync(ProjectDbContext db)
    {
        Accounts.Clear();
        foreach (var a in await db.ChartOfAccounts.AsNoTracking()
                     .Where(a => a.IsActive && (a.AccountType == AccountType.Expense || a.AccountType == AccountType.Revenue))
                     .OrderBy(a => a.AccountCode).ToListAsync()) Accounts.Add(a);
    }

    protected override Task<List<FinanceCategory>> QueryAsync(ProjectDbContext db) =>
        db.FinanceCategories.AsNoTracking().Include(c => c.Account).OrderBy(c => c.Kind).ThenBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();

    protected override FinanceCategory CreateNew() => new() { Kind = FinanceCategoryKind.Operating, SortOrder = Items.Count + 1 };

    protected override string? Validate(FinanceCategory e) => string.IsNullOrWhiteSpace(e.Name) ? "أدخل اسم النوع" : null;

    protected override Task BeforeSaveAsync(ProjectDbContext db, FinanceCategory e)
    {
        e.Name = e.Name.Trim();
        return Task.CompletedTask;
    }
}

// ============================ الحسابات الختامية ============================
public class FinalAccountsSectionViewModel : SectionViewModel
{
    private int _year = DateTime.Today.Year;
    private int _month = DateTime.Today.Month;
    private FinalAccountsReport? _report;

    public FinalAccountsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Finance, "الحسابات الختامية", Icons.Statement, "#0F766E",
               "ربح الشهر: المبيعات والخصم، كلفة المواد، المصاريف التشغيلية وغير التشغيلية، الخسائر، وكلفة القنينة")
    {
        PrintCommand = new RelayCommand(Print);
    }

    protected override bool ReloadOnActivate => true;
    public IReadOnlyList<int> Years { get; } = Enumerable.Range(DateTime.Today.Year - 3, 5).Reverse().ToList();
    public IReadOnlyList<int> Months { get; } = Enumerable.Range(1, 12).ToList();
    public int Year { get => _year; set { if (SetProperty(ref _year, value)) Background(LoadAsync()); } }
    public int Month { get => _month; set { if (SetProperty(ref _month, value)) Background(LoadAsync()); } }
    public FinalAccountsReport? Report { get => _report; private set => SetProperty(ref _report, value); }
    public ObservableCollection<FinalAccountLine> Lines { get; } = new();
    public ObservableCollection<ProductMarginRow> Products { get; } = new();
    public RelayCommand PrintCommand { get; }

    public override async Task LoadAsync()
    {
        if (!Has(SpecialPermission.FinalAccounts)) { StatusMessage = "تحتاج صلاحية «الحسابات الختامية»"; return; }
        await using var db = Session.NewDb();
        var report = await new FinalAccountsService(db).MonthAsync(Year, Month);
        Lines.Clear();
        foreach (var l in report.Lines) Lines.Add(l);
        Products.Clear();
        foreach (var p in report.Products) Products.Add(p);
        Report = report;
        StatusMessage = report.PiecesSold == 0 ? "لا مبيعات مرحّلة في هذا الشهر" : $"كلفة القنينة {report.CostPerPiece:N2} د.ع — صافي الربح {report.NetProfit:N0} د.ع";
    }

    private void Print()
    {
        if (Report is null) return;
        var r = new ReportDocument { Key = "final-accounts", CompanyName = Session.ProjectName, Title = $"الحسابات الختامية — {Month}/{Year}", PrintedBy = Session.FullName };
        r.Field("القطع المباعة", $"{Report.PiecesSold:N0}").Field("كلفة القنينة", $"{Report.CostPerPiece:N2} د.ع")
         .Field("منها مصاريف للقطعة", $"{Report.OverheadPerPiece:N2} د.ع");
        r.Columns.AddRange(new[] { "القسم", "البند", "المبلغ" });
        foreach (var l in Lines) r.Rows.Add(new[] { l.Section, l.IsTotal ? $"◄ {l.Label}" : l.Label, $"{l.Amount:N0}" });
        r.Total("صافي ربح الشهر", $"{Report.NetProfit:N0} د.ع", true);
        Dialogs.ShowReport(r);

        var p = new ReportDocument { Key = "product-margins", CompanyName = Session.ProjectName, Title = $"هامش كل منتج — {Month}/{Year}", PrintedBy = Session.FullName };
        p.Columns.AddRange(new[] { "المنتج", "القطع", "الإيراد", "بسعر القائمة", "الخصم", "الكلفة", "الهامش", "%" });
        foreach (var x in Products)
            p.Rows.Add(new[] { x.ItemName, $"{x.PiecesSold:N0}", $"{x.Revenue:N0}", $"{x.ListValue:N0}", $"{x.Discount:N0}", $"{x.Cost:N0}", $"{x.Margin:N0}", $"{x.MarginPercent:N1}" });
        Dialogs.ShowReport(p);
    }
}

// ============================ رأس المال التشغيلي والفائض ============================
public class WorkingCapitalSectionViewModel : SectionViewModel
{
    private WorkingCapitalSnapshot? _snapshot;
    private DateTime _effectiveFrom = DateTime.Today;
    private decimal _capitalAmount;
    private string? _capitalNotes;
    private CashBoxRow? _fromBox;
    private decimal _moveAmount;
    private decimal _reservePercent;

    public WorkingCapitalSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Finance, "رأس المال والفائض", Icons.Currency, "#7C3AED",
               "ما زاد عن رأس المال التشغيلي المخصص ربح متحقق: يُنقل لصندوق المنزل ويُوزَّع على الشركاء")
    {
        SetCapitalCommand = new AsyncRelayCommand(SetCapitalAsync);
        MoveCommand = new AsyncRelayCommand(MoveAsync);
    }

    protected override bool ReloadOnActivate => true;
    public WorkingCapitalSnapshot? Snapshot { get => _snapshot; private set => SetProperty(ref _snapshot, value); }
    public ObservableCollection<WorkingCapitalSetting> History { get; } = new();
    public ObservableCollection<CashBoxRow> Boxes { get; } = new();
    public ObservableCollection<PartnerSharePreview> Shares { get; } = new();
    public DateTime EffectiveFrom { get => _effectiveFrom; set => SetProperty(ref _effectiveFrom, value); }
    public decimal CapitalAmount { get => _capitalAmount; set => SetProperty(ref _capitalAmount, value); }
    public string? CapitalNotes { get => _capitalNotes; set => SetProperty(ref _capitalNotes, value); }
    public CashBoxRow? FromBox { get => _fromBox; set => SetProperty(ref _fromBox, value); }
    public decimal MoveAmount { get => _moveAmount; set => SetProperty(ref _moveAmount, value); }
    /// <summary>نسبة تُحتجز احتياطيًا قبل التوزيع (صفر = توزيع الكل).</summary>
    public decimal ReservePercent { get => _reservePercent; set { if (SetProperty(ref _reservePercent, value)) Background(LoadSharesAsync()); } }
    public decimal Distributable => Snapshot is null ? 0 : Math.Max(0, Math.Round(Snapshot.Surplus * (100 - ReservePercent) / 100, 0));

    public AsyncRelayCommand SetCapitalCommand { get; }
    public AsyncRelayCommand MoveCommand { get; }

    public override async Task LoadAsync()
    {
        if (!Has(SpecialPermission.FinalAccounts)) { StatusMessage = "تحتاج صلاحية «الحسابات الختامية»"; return; }
        await using var db = Session.NewDb();
        var fa = new FinalAccountsService(db);
        Snapshot = await fa.WorkingCapitalAsync(DateTime.Today);
        History.Clear();
        foreach (var h in await fa.CapitalHistoryAsync()) History.Add(h);
        var fromId = FromBox?.Id;
        Boxes.Clear();
        foreach (var b in (await new CashBoxService(db).GetBoxesAsync(Session.UserId)).Where(b => b.BoxType is CashBoxType.Main or CashBoxType.User)) Boxes.Add(b);
        _fromBox = Boxes.FirstOrDefault(b => b.Id == fromId) ?? Boxes.OrderByDescending(b => b.Balance).FirstOrDefault();
        OnPropertyChanged(nameof(FromBox));
        if (CapitalAmount == 0) CapitalAmount = Snapshot.Capital;
        MoveAmount = Snapshot.TransferableNow;
        await LoadSharesAsync();
        StatusMessage = Snapshot.CapitalEffectiveFrom is null ? "حدّد رأس المال التشغيلي أولًا" : Snapshot.Surplus > 0
            ? $"الفائض المتحقق {Snapshot.Surplus:N0} د.ع — المتاح نقدًا للنقل الآن {Snapshot.TransferableNow:N0}"
            : $"لا فائض: الموجودات أقل من رأس المال التشغيلي بـ {-Snapshot.Surplus:N0} د.ع";
    }

    private async Task LoadSharesAsync()
    {
        OnPropertyChanged(nameof(Distributable));
        Shares.Clear();
        if (Distributable <= 0) return;
        await using var db = Session.NewDb();
        foreach (var s in await new ReconciliationService(db).PreviewSharesAsync(Distributable)) Shares.Add(s);
    }

    private async Task SetCapitalAsync()
    {
        if (CapitalAmount < 0) { Dialogs.Error("المبلغ لا يكون سالبًا"); return; }
        if (!Dialogs.Confirm($"رأس المال التشغيلي {CapitalAmount:N0} د.ع اعتبارًا من {EffectiveFrom:yyyy/MM/dd}؟ الأيام السابقة تبقى على القيمة القديمة.")) return;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new FinalAccountsService(db).SetCapitalAsync(EffectiveFrom, CapitalAmount, CapitalNotes, Session.UserId), "حُفظ رأس المال التشغيلي"))
        {
            CapitalNotes = null;
            await LoadAsync();
        }
    }

    private async Task MoveAsync()
    {
        if (FromBox is null) { Dialogs.Error("اختر الصندوق المصدر"); return; }
        if (MoveAmount <= 0) { Dialogs.Error("المبلغ يجب أن يكون أكبر من صفر"); return; }
        if (!Dialogs.Confirm($"نقل {MoveAmount:N0} د.ع من {FromBox.Name} إلى صندوق المنزل؟")) return;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new FinalAccountsService(db).MoveSurplusToHomeAsync(FromBox.Id, MoveAmount, DateTime.Today, Session.UserId), "نُقل الفائض إلى صندوق المنزل"))
            await LoadAsync();
    }
}

// ============================ محاكاة الكلفة ============================
public class SimulatedMaterial : ObservableObject
{
    private decimal _proposed;
    public int ItemId { get; init; }
    public string ItemName { get; init; } = "";
    public decimal Current { get; init; }
    public decimal Proposed { get => _proposed; set { if (SetProperty(ref _proposed, value)) OnPropertyChanged(nameof(ChangePercent)); } }
    public decimal ChangePercent => Current == 0 ? 0 : Math.Round((Proposed - Current) / Current * 100, 1);
}

/// <summary>«لو ارتفعت الأسعار»: أسعار مقترحة للمواد الأولية ← كلفة القنينة والهامش لكل منتج، دون تغيير أي سعر فعلي.</summary>
public class CostSimulationSectionViewModel : SectionViewModel
{
    private decimal _overheadPerPiece;

    public CostSimulationSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Finance, "محاكاة الكلفة", Icons.Brain, "#0284C7",
               "غيّر أسعار المواد الأولية افتراضيًا لترى أثرها على كلفة القنينة والهامش — لا يتغير أي سعر فعلي")
    {
        SimulateCommand = new AsyncRelayCommand(SimulateAsync);
        ResetCommand = new RelayCommand(() => { foreach (var m in Materials) m.Proposed = m.Current; Results.Clear(); });
        PrintCommand = new RelayCommand(Print);
    }

    public ObservableCollection<SimulatedMaterial> Materials { get; } = new();
    public ObservableCollection<CostSimulationRow> Results { get; } = new();
    public decimal OverheadPerPiece { get => _overheadPerPiece; set => SetProperty(ref _overheadPerPiece, value); }
    public AsyncRelayCommand SimulateCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand PrintCommand { get; }

    public override async Task LoadAsync()
    {
        if (!Has(SpecialPermission.CostAndProfit)) { StatusMessage = "تحتاج صلاحية «رؤية الكلفة والهامش والأرباح»"; return; }
        await using var db = Session.NewDb();
        var used = await db.BOMLines.Where(l => l.BOM.IsActive).Select(l => l.RawMaterialItemId).Distinct().ToListAsync();
        Materials.Clear();
        foreach (var i in await db.Items.AsNoTracking().Where(i => used.Contains(i.Id)).OrderBy(i => i.ItemName).ToListAsync())
            Materials.Add(new SimulatedMaterial { ItemId = i.Id, ItemName = i.ItemName, Current = i.CostPrice ?? 0, Proposed = i.CostPrice ?? 0 });
        var last = DateTime.Today.AddMonths(-1);
        OverheadPerPiece = (await new FinalAccountsService(db).MonthAsync(last.Year, last.Month)).OverheadPerPiece;
        await SimulateAsync();
    }

    private async Task SimulateAsync()
    {
        await using var db = Session.NewDb();
        var rows = await new FinalAccountsService(db).SimulateAsync(Materials.ToDictionary(m => m.ItemId, m => m.Proposed), OverheadPerPiece);
        Results.Clear();
        foreach (var r in rows) Results.Add(r);
        var changed = Materials.Count(m => m.Proposed != m.Current);
        StatusMessage = Results.Count == 0 ? "لا توجد وصفات فعّالة — عرّف وصفة كل منتج من الإنتاج ← الوصفات"
                      : changed == 0 ? "الأسعار الحالية — عدّل «المقترح» ثم احسب" : $"{changed} مادة بسعر مقترح";
    }

    private void Print()
    {
        var changes = string.Join("، ", Materials.Where(m => m.Proposed != m.Current)
            .Select(m => $"{m.ItemName}: {m.Current:N2} ← {m.Proposed:N2} ({m.ChangePercent:+0.0;-0.0}%)"));
        var r = new ReportDocument { Key = "cost-simulation", CompanyName = Session.ProjectName, Title = "محاكاة الكلفة", PrintedBy = Session.FullName,
                                     Notes = changes.Length == 0 ? "بالأسعار الحالية" : "الأسعار المقترحة: " + changes };
        r.Field("مصاريف تشغيلية للقطعة", $"{OverheadPerPiece:N2} د.ع");
        r.Columns.AddRange(new[] { "المنتج", "سعر البيع", "الكلفة الحالية", "الكلفة المقترحة", "الفرق", "الهامش المقترح" });
        foreach (var x in Results)
            r.Rows.Add(new[] { x.ItemName, $"{x.SalePrice:N2}", $"{x.CurrentCost:N2}", $"{x.SimulatedCost:N2}", $"{x.CostChange:N2}", $"{x.SimulatedMargin:N2}" });
        Dialogs.ShowReport(r);
    }
}

// ============================ التقرير اليومي للصناديق ============================
public class DailyCashSectionViewModel : SectionViewModel
{
    private DateTime _date = DateTime.Today;

    public DailyCashSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Finance, "تقرير الصندوق اليومي", Icons.Calendar, "#059669",
               "لكل صندوق: رصيد أول اليوم، الداخل والخارج حسب النوع، ورصيد آخر اليوم — المطابقة اليومية")
    {
        PrintCommand = new RelayCommand(Print);
    }

    protected override bool ReloadOnActivate => true;
    public DateTime Date { get => _date; set { if (SetProperty(ref _date, value)) Background(LoadAsync()); } }
    public ObservableCollection<DailyCashBoxRow> Boxes { get; } = new();
    public decimal TotalIn => Boxes.Sum(b => b.In);
    public decimal TotalOut => Boxes.Sum(b => b.Out);
    public decimal TotalClosing => Boxes.Sum(b => b.Closing);
    public RelayCommand PrintCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        // بلا صلاحية «كل الصناديق»: صناديقه فقط
        var visible = (await new CashBoxService(db).GetBoxesAsync(Session.UserId)).Select(b => b.Id).ToList();
        Boxes.Clear();
        foreach (var b in await new FinalAccountsService(db).DailyCashAsync(Date, visible)) Boxes.Add(b);
        OnPropertyChanged(nameof(TotalIn));
        OnPropertyChanged(nameof(TotalOut));
        OnPropertyChanged(nameof(TotalClosing));
    }

    private void Print()
    {
        var r = new ReportDocument { Key = "daily-cash", CompanyName = Session.ProjectName, Title = $"التقرير اليومي للصناديق — {Date:yyyy/MM/dd}", PrintedBy = Session.FullName };
        r.Columns.AddRange(new[] { "الصندوق", "البند", "داخل", "خارج" });
        foreach (var b in Boxes)
        {
            r.Rows.Add(new[] { b.BoxName, "رصيد أول اليوم", $"{b.Opening:N0}", "" });
            foreach (var (label, @in, @out) in b.ByType) r.Rows.Add(new[] { "", label, @in == 0 ? "" : $"{@in:N0}", @out == 0 ? "" : $"{@out:N0}" });
            r.Rows.Add(new[] { "", "رصيد آخر اليوم", $"{b.Closing:N0}", "" });
        }
        r.Total("مجموع الداخل", $"{TotalIn:N0} د.ع");
        r.Total("مجموع الخارج", $"{TotalOut:N0} د.ع");
        r.Total("الرصيد الكلي آخر اليوم", $"{TotalClosing:N0} د.ع", true);
        r.Signatures.AddRange(new[] { "أمين الصندوق", "المحاسب", "المدير" });
        Dialogs.ShowReport(r);
    }
}
