using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;
using WarehouseEntity = ERP.Data.ProjectDb.Entities.Warehouse;

namespace ERP.Presentation.ViewModels.Warehouse;

public class DocumentLineDraft
{
    public int ItemId { get; init; }
    public string ItemCode { get; init; } = "";
    public string ItemName { get; init; } = "";
    public int PackagingLevelId { get; init; }
    public string LevelName { get; init; } = "";
    public decimal QuantityInLevel { get; init; }
    public decimal Pieces { get; init; }
    public int? BatchId { get; init; }
    public string? NewBatchNumber { get; init; }
    public DateTime? NewBatchExpiry { get; init; }
    public string BatchLabel { get; init; } = "";
    /// <summary>متغير المنتج التام عند الإدخال اليدوي (طلب خاص باسم زبون/مناسبة)؛ NULL = الأساسي.</summary>
    public int? CustomRecipeId { get; init; }
}

/// <summary>خيار «المتغير» للمنتج التام: الأساسي أو طلب خاص.</summary>
public class VariantChoice
{
    public int? RecipeId { get; init; }
    public string Label { get; init; } = "";
    public override string ToString() => Label;
}

public class BatchChoice
{
    public int? BatchId { get; init; }
    public string Label { get; init; } = "";
    public decimal? Available { get; init; }
    public int? RecipeId { get; init; }
    public override string ToString() => Label;
}

/// <summary>
/// واجهة مخزن واحد (تبويب لكل مخزن، ومنها أي مخزن جديد): عمليات الإدخال والإخراج والمناقلة والتالف
/// والمسحوب المجاني كمستندات مرقّمة قابلة للطباعة، مع أرصدة هذا المخزن وحده وتقارير حركته.
/// </summary>
public class WarehouseWorkspaceSectionViewModel : SectionViewModel
{
    private Option<StockDocumentType> _operation;
    private DateTime _documentDate = DateTime.Today;
    private string? _partyName;
    private WarehouseEntity? _counterWarehouse;
    private Option<DamageReason>? _damageReason;
    private Option<DamageCause>? _damageCause;
    private bool _moveToDamaged = true;
    private string? _notes;
    private bool _showAllItems;
    private Item? _lineItem;
    private ItemPackagingLevel? _lineLevel;
    private BatchChoice? _lineBatch;
    private VariantChoice? _lineVariant;
    private string? _lineNewBatch;
    private DateTime? _lineExpiry;
    private decimal _lineQuantity = 1;
    private DateTime _reportFrom = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _reportTo = DateTime.Today;
    private Item? _ledgerItem;
    private Option<StockDocumentType>? _documentFilter;
    private List<Item> _allItems = new();
    /// <summary>عبوات الأصناف: كل الكميات تُعرض بالكارتون/الشرنك، والقطع للحساب فقط.</summary>
    private PackFormatter _packs = new(Array.Empty<ItemPackagingLevel>());
    private ProductNames? _names;

    public WarehouseWorkspaceSectionViewModel(AppSession s, IDialogService d, WarehouseEntity warehouse)
        : base(s, d, ModuleCode.Warehouse, warehouse.Name, GlyphOf(warehouse.WarehouseType), ColorOf(warehouse.WarehouseType),
               $"{ArabicLabels.Of(warehouse.WarehouseType)} — إدخال، إخراج، مناقلة، تالف، مسحوب مجاني، وتقارير الحركة")
    {
        WarehouseId = warehouse.Id;
        WarehouseType = warehouse.WarehouseType;
        Alerts = new WarehouseAlertsEditor(s, d, () => CanEdit);
        Alerts.Saved += () => Background(LoadKpisAndBalancesAsync());
        OperationOptions = OperationsFor(warehouse.WarehouseType);
        _operation = OperationOptions[0];
        _damageReason = ReasonOptions[1];
        DocumentFilters = new[] { new Option<StockDocumentType>(default, "كل الأنواع") }.Concat(ArabicLabels.OptionsOf<StockDocumentType>().Where(o => OperationOptions.Any(x => x.Value == o.Value) || WarehouseTypeShowsRepDocs(warehouse.WarehouseType) && o.Value is StockDocumentType.RepLoad or StockDocumentType.RepReturn)).ToList();
        _documentFilter = DocumentFilters[0];

        AddLineCommand = new AsyncRelayCommand(AddLineAsync);
        RemoveLineCommand = new RelayCommand(p => { if (p is DocumentLineDraft l) { Lines.Remove(l); OnPropertyChanged(nameof(LinesTotalText)); } });
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        ClearCommand = new RelayCommand(() => { if (Lines.Count == 0 || Dialogs.Confirm("تفريغ سطور المستند؟")) ClearForm(); });
        PrintDocumentCommand = new AsyncRelayCommand(p => p is StockDocumentRow r ? PrintDocumentAsync(r.Id) : Task.CompletedTask);
        VoidDocumentCommand = new AsyncRelayCommand(p => p is StockDocumentRow r ? VoidDocumentAsync(r) : Task.CompletedTask);
        CopyDocumentCommand = new AsyncRelayCommand(p => p is StockDocumentRow r ? CopyDocumentAsync(r) : Task.CompletedTask);
        LoadReportsCommand = new AsyncRelayCommand(LoadReportsAsync);
        PrintBalancesCommand = new RelayCommand(() => Dialogs.ShowReport(BuildBalancesReport()));
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        PrintSummaryCommand = new RelayCommand(() => Dialogs.ShowReport(BuildSummaryReport()));
        PrintLedgerCommand = new RelayCommand(() => Dialogs.ShowReport(BuildLedgerReport()));
        AllItemsLedgerCommand = new RelayCommand(() => LedgerItem = null);
    }

    protected override bool ReloadOnActivate => true;

    protected override void ResetInput()
    {
        ClearForm();
    }

    public int WarehouseId { get; }
    public WarehouseType WarehouseType { get; }
    public string WarehouseTypeLabel => ArabicLabels.Of(WarehouseType);

    public static string GlyphOf(WarehouseType t) => t switch
    {
        WarehouseType.RawMaterial => Icons.Layers,
        WarehouseType.FinishedGoods => Icons.Item,
        WarehouseType.RepVan => Icons.Truck,
        WarehouseType.Damaged => Icons.Alert,
        _ => Icons.Store
    };

    public static string ColorOf(WarehouseType t) => t switch
    {
        WarehouseType.RawMaterial => "#0EA5E9",
        WarehouseType.FinishedGoods => "#10B981",
        WarehouseType.RepVan => "#F59E0B",
        WarehouseType.Damaged => "#EF4444",
        WarehouseType.Returns => "#8B5CF6",
        _ => "#6366F1"
    };

    // ---------------- مؤشرات المخزن ----------------
    private int _itemsCount;
    private decimal _totalPieces, _todayIn, _todayOut;
    private int _lowCount;
    public int ItemsCount { get => _itemsCount; private set => SetProperty(ref _itemsCount, value); }
    public decimal TotalPieces { get => _totalPieces; private set => SetProperty(ref _totalPieces, value); }
    public decimal TodayIn { get => _todayIn; private set => SetProperty(ref _todayIn, value); }
    public decimal TodayOut { get => _todayOut; private set => SetProperty(ref _todayOut, value); }
    private string _totalText = "0", _todayInText = "0", _todayOutText = "0";
    /// <summary>إجمالي الرصيد بالعبوات: «6,196 كارتون + 4,380 شرنك».</summary>
    public string TotalText { get => _totalText; private set { if (SetProperty(ref _totalText, value)) OnPropertyChanged(nameof(TotalLines)); } }
    /// <summary>بطاقة الإجمالي: سطر لكل عبوة («819 شرنك 20×330» ثم «22 كارتون 40×330») بدل التفاف الأرقام.</summary>
    public string TotalLines => _totalText.Replace(" + ", "\n");
    private string _totalLabel = "إجمالي الرصيد";
    public string TotalLabel { get => _totalLabel; private set => SetProperty(ref _totalLabel, value); }
    /// <summary>
    /// أعمدة الأرصدة بحسب ما يحويه المخزن: منتج تام فقط ← العبوة | المحتوى | المتغير؛
    /// مواد أولية أو مختلط (التالف) ← الرمز | الصنف (الاسم الكامل أو الموحد) بلا أعمدة فارغة.
    /// </summary>
    public bool PartsLayout => Balances.Count > 0 && Balances.All(b => b.IsFinished);
    public bool NameLayout => !PartsLayout;
    /// <summary>مخزن فيه النوعان معًا: يظهر وسم «تام/أولية» بجانب الاسم.</summary>
    public bool MixedLayout => Balances.Any(b => b.IsFinished) && Balances.Any(b => !b.IsFinished);
    public bool LedgerPartsLayout => Ledger.Count > 0 && Ledger.Where(r => r.ItemId is not null && r.DisplayName.Length > 0).All(r => r.IsFinished);
    public bool LedgerNameLayout => !LedgerPartsLayout;
    public string TodayInText { get => _todayInText; private set => SetProperty(ref _todayInText, value); }
    public string TodayOutText { get => _todayOutText; private set => SetProperty(ref _todayOutText, value); }
    public int LowCount { get => _lowCount; private set => SetProperty(ref _lowCount, value); }
    /// <summary>حدود التنبيه لهذا المخزن تُضبط من داخله (تبويب الأرصدة) — لا للسيارات ولا للتالف.</summary>
    public WarehouseAlertsEditor Alerts { get; }
    public bool HasAlerts => WarehouseType is not (WarehouseType.RepVan or WarehouseType.WorkInProcess or WarehouseType.Damaged);

    // ---------------- عملية جديدة ----------------
    public IReadOnlyList<Option<StockDocumentType>> OperationOptions { get; }

    /// <summary>مخزن المواد الأولية بلا إخراج حر ولا مسحوب مجاني: المواد تُصرف فقط لأمر إنتاج.</summary>
    /// <summary>مخازن المنتج والسيارات تعرض مستندات المندوبين في سجل مستنداتها (للتصفية فقط).</summary>
    private static bool WarehouseTypeShowsRepDocs(WarehouseType type) => type is not (WarehouseType.RawMaterial or WarehouseType.Damaged or WarehouseType.WorkInProcess);

    /// <remarks>إسناد الحمولة والإرجاع من المندوب يُنفَّذان من وحدة المندوبين ← مستندات المندوبين.</remarks>
    public static IReadOnlyList<Option<StockDocumentType>> OperationsFor(WarehouseType type) =>
        ArabicLabels.OptionsOf<StockDocumentType>()
            .Where(o => o.Value is not (StockDocumentType.RepLoad or StockDocumentType.RepReturn))
            .Where(o => type != WarehouseType.RawMaterial || o.Value is not (StockDocumentType.Issue or StockDocumentType.FreeIssue))
            .ToList();
    /// <summary>التلف الميداني خاص بالإرجاع من المندوب.</summary>
    public IReadOnlyList<Option<DamageReason>> ReasonOptions { get; } =
        ArabicLabels.OptionsOf<DamageReason>().Where(o => o.Value != Data.ProjectDb.Entities.DamageReason.Field).ToList();
    /// <summary>سبب التلف بجانب مكانه (سوء خزن، تسرب، انتهاء صلاحية، خطأ تصنيع، طباعة خاطئة، أخرى).</summary>
    public IReadOnlyList<Option<DamageCause>> CauseOptions { get; } = ArabicLabels.OptionsOf<DamageCause>();
    public ObservableCollection<WarehouseEntity> OtherWarehouses { get; } = new();
    public ObservableCollection<Item> ItemsLookup { get; } = new();
    public ObservableCollection<ItemPackagingLevel> LevelOptions { get; } = new();
    public ObservableCollection<BatchChoice> BatchOptions { get; } = new();
    /// <summary>متغيرات المنتج التام المختار (الأساسي + الطلبات الخاصة الفعّالة) — تظهر في الإدخال فقط.</summary>
    public ObservableCollection<VariantChoice> VariantOptions { get; } = new();
    public ObservableCollection<DocumentLineDraft> Lines { get; } = new();

    public Option<StockDocumentType> Operation
    {
        get => _operation;
        set
        {
            if (!SetProperty(ref _operation, value)) return;
            foreach (var n in new[] { nameof(IsReceipt), nameof(IsOutgoing), nameof(IsTransfer), nameof(IsDamaged), nameof(NeedsParty), nameof(PartyLabel), nameof(OperationHint), nameof(SaveLabel), nameof(ShowVariant) })
                OnPropertyChanged(n);
            if (Lines.Count > 0 && Dialogs.Confirm("تغيير نوع العملية يفرّغ السطور المضافة. متابعة؟")) Lines.Clear();
            Background(RefreshBatchesAsync());
        }
    }
    public bool IsReceipt => Operation.Value == StockDocumentType.Receipt;
    public bool IsOutgoing => !IsReceipt;
    public bool IsTransfer => Operation.Value == StockDocumentType.Transfer;
    public bool IsDamaged => Operation.Value == StockDocumentType.Damaged;
    public bool NeedsParty => Operation.Value is StockDocumentType.Receipt or StockDocumentType.Issue or StockDocumentType.FreeIssue;
    public string PartyLabel => Operation.Value switch
    {
        StockDocumentType.Receipt => "الجهة المورِّدة / المصدر (اختياري)",
        StockDocumentType.Issue => "الجهة المستلمة أو الغرض",
        _ => "الجهة المستفيدة"
    };
    public string OperationHint => Operation.Value switch
    {
        StockDocumentType.Receipt => "يضيف الكميات لرصيد هذا المخزن، مع تشغيلة جديدة أو موجودة.",
        StockDocumentType.Issue => "يصرف من هذا المخزن لجهة أو غرض، بترتيب الأقرب انتهاءً، ولا يسمح برصيد سالب.",
        StockDocumentType.Transfer => "ينقل الكميات إلى مخزن آخر بنفس التشغيلات (صادر من هنا، وارد هناك).",
        StockDocumentType.Damaged => "يخصم التالف من هذا المخزن مع السبب، وينقله لمخزن التالف إن اخترت ذلك.",
        _ => "صرف مجاني (عينات، هدايا، ضيافة) باسم الجهة المستفيدة — يُخصم من المخزون بلا قيد مالي."
    };
    public string SaveLabel => $"حفظ مستند {Operation.Label}";

    public DateTime DocumentDate { get => _documentDate; set => SetProperty(ref _documentDate, value); }
    public string? PartyName { get => _partyName; set => SetProperty(ref _partyName, value); }
    /// <summary>جهات المسحوب المجاني الثابتة (يمكن كتابة جهة جديدة، وتُصنَّف "أخرى").</summary>
    public ObservableCollection<string> PartySuggestions { get; } = new();
    public WarehouseEntity? CounterWarehouse { get => _counterWarehouse; set => SetProperty(ref _counterWarehouse, value); }
    public Option<DamageReason>? DamageReason { get => _damageReason; set => SetProperty(ref _damageReason, value); }
    public Option<DamageCause>? DamageCause { get => _damageCause; set => SetProperty(ref _damageCause, value); }
    public bool MoveToDamaged { get => _moveToDamaged; set => SetProperty(ref _moveToDamaged, value); }
    public string? Notes { get => _notes; set => SetProperty(ref _notes, value); }

    /// <summary>افتراضيًا: أصناف تناسب نوع المخزن (مواد أولية = شراء، منتج تام = تصنيع).</summary>
    public bool ShowAllItems { get => _showAllItems; set { if (SetProperty(ref _showAllItems, value)) FillItems(); } }

    public Item? LineItem
    {
        get => _lineItem;
        set { if (SetProperty(ref _lineItem, value)) Background(OnLineItemChangedAsync()); }
    }
    public ItemPackagingLevel? LineLevel { get => _lineLevel; set { if (SetProperty(ref _lineLevel, value)) OnPropertyChanged(nameof(LinePiecesText)); } }
    public BatchChoice? LineBatch { get => _lineBatch; set { if (SetProperty(ref _lineBatch, value)) OnPropertyChanged(nameof(LineAvailableText)); } }
    public string? LineNewBatch { get => _lineNewBatch; set => SetProperty(ref _lineNewBatch, value); }
    public VariantChoice? LineVariant
    {
        get => _lineVariant;
        set { if (SetProperty(ref _lineVariant, value) && IsReceipt) Background(RefreshBatchesAsync()); }
    }
    /// <summary>قائمة «المتغير» للمنتج التام في الإدخال اليدوي فقط، وحين له طلبات خاصة.</summary>
    public bool ShowVariant => IsReceipt && VariantOptions.Count > 1;
    public DateTime? LineExpiry { get => _lineExpiry; set => SetProperty(ref _lineExpiry, value); }
    public decimal LineQuantity { get => _lineQuantity; set { if (SetProperty(ref _lineQuantity, value)) OnPropertyChanged(nameof(LinePiecesText)); } }
    /// <summary>تحويل الكمية المدخلة بوحدة صغرى (قطعة/شرنك) إلى العبوة الكبرى: «= 2 كارتون + 5 قطعة».</summary>
    public string LinePiecesText
    {
        get
        {
            if (LineLevel is null || LineItem is null || LineQuantity <= 0) return "";
            var top = _packs.LevelsOf(LineItem.Id).FirstOrDefault();
            return top is null || top.Id == LineLevel.Id ? "" : $"= {_packs.Of(LineItem.Id, LineQuantity * LineLevel.EquivalentBaseUnits)}";
        }
    }
    public string LineAvailableText
    {
        get
        {
            if (LineItem is null || IsReceipt) return "";
            var total = BatchOptions.FirstOrDefault(b => b.BatchId == null)?.Available ?? 0;
            var chosen = LineBatch?.BatchId is null ? total : LineBatch.Available ?? 0;
            return $"المتاح: {_packs.Of(LineItem.Id, chosen)}";
        }
    }
    public string LinesTotalText => Lines.Count == 0 ? "" : $"{Lines.Count} سطر — {LinesPacks}";
    private string LinesPacks => _packs.Total(Lines.Select(l => (l.ItemId, l.Pieces)));

    public AsyncRelayCommand AddLineCommand { get; }
    public RelayCommand RemoveLineCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand ClearCommand { get; }

    // ---------------- المستندات والتقارير ----------------
    public IReadOnlyList<Option<StockDocumentType>> DocumentFilters { get; }
    public Option<StockDocumentType>? DocumentFilter { get => _documentFilter; set { if (SetProperty(ref _documentFilter, value)) Background(LoadDocumentsAsync()); } }
    public DateTime ReportFrom { get => _reportFrom; set => SetProperty(ref _reportFrom, value); }
    public DateTime ReportTo { get => _reportTo; set => SetProperty(ref _reportTo, value); }
    public Item? LedgerItem { get => _ledgerItem; set { if (SetProperty(ref _ledgerItem, value)) Background(LoadLedgerAsync()); } }
    public ObservableCollection<Item> LedgerItems { get; } = new();
    public ObservableCollection<StockDocumentRow> Documents { get; } = new();
    public ObservableCollection<StockBalanceRow> Balances { get; } = new BulkObservableCollection<StockBalanceRow>();

    // ---------------- البحث في أرصدة المخزن (صندوق في صف التبويبات) ----------------
    private string _searchText = "";
    private int _selectedTabIndex;
    private const int BalancesTab = 1;

    /// <summary>الأرصدة المعروضة بعد البحث (نفسها Balances حين لا بحث) — الجدول والطباعة منها.</summary>
    public ObservableCollection<StockBalanceRow> VisibleBalances { get; } = new BulkObservableCollection<StockBalanceRow>();
    /// <summary>
    /// بحث بالكود أو اسم الصنف أو التشغيلة أو المتغير (اسم المطعم)، مع توحيد الهمزات والتاء المربوطة؛ كلمات متعددة = كلها مطلوبة.
    /// الكتابة تنقل إلى تبويب «الأرصدة الحالية» حيث النتائج.
    /// </summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? "")) return;
            ApplyBalanceFilter();
            if (HasSearch) SelectedTabIndex = BalancesTab;
            OnPropertyChanged(nameof(HasSearch));
        }
    }
    public bool HasSearch => !string.IsNullOrWhiteSpace(SearchText);
    public int SelectedTabIndex { get => _selectedTabIndex; set => SetProperty(ref _selectedTabIndex, value); }
    /// <summary>«12 من 131» أثناء البحث.</summary>
    public string BalancesCountText => HasSearch ? $"{VisibleBalances.Count} من {Balances.Count}" : "";
    public RelayCommand ClearSearchCommand { get; }

    private void ApplyBalanceFilter()
    {
        var terms = ArabicText.Normalize(SearchText).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        VisibleBalances.ReplaceAll(terms.Length == 0 ? Balances.ToList() : Balances.Where(b =>
        {
            var hay = ArabicText.Normalize($"{b.ItemCode} {b.ItemName} {b.BatchNumber} {b.Variant}");
            return terms.All(t => hay.Contains(t, StringComparison.OrdinalIgnoreCase));
        }).ToList());
        OnPropertyChanged(nameof(BalancesCountText));
    }
    public ObservableCollection<StockSummaryRow> Summary { get; } = new();
    public ObservableCollection<StockLedgerRow> Ledger { get; } = new();

    public AsyncRelayCommand PrintDocumentCommand { get; }
    /// <summary>إلغاء مستند بسبب إلزامي (حركات عكسية، ويبقى ظاهرًا «ملغى») — لصاحب صلاحية «إلغاء المستندات المرحّلة».</summary>
    public AsyncRelayCommand VoidDocumentCommand { get; }
    /// <summary>«نسخ»: مستند جديد بنفس السطور في «عملية جديدة» لتعديلها وحفظها (هو التعديل بعد الإلغاء).</summary>
    public AsyncRelayCommand CopyDocumentCommand { get; }
    private string? _voidReason;
    public string? VoidReason { get => _voidReason; set => SetProperty(ref _voidReason, value); }
    public bool CanVoidDocuments => Has(SpecialPermission.VoidPosted);
    public AsyncRelayCommand LoadReportsCommand { get; }
    public RelayCommand PrintBalancesCommand { get; }
    public RelayCommand PrintSummaryCommand { get; }
    public RelayCommand PrintLedgerCommand { get; }
    public RelayCommand AllItemsLedgerCommand { get; }

    /// <summary>آخر مستند حُفظ (للعرض والاختبارات).</summary>
    public StockDocument? LastDocument { get; private set; }

    // ============================ التحميل ============================
    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        // صنف جديد يظهر عند فتح التبويب، ما لم يكن مستند قيد الإدخال
        if (_allItems.Count == 0 || (Lines.Count == 0 && LineItem is null))
        {
            _allItems = await db.Items.AsNoTracking().Where(i => i.IsActive).OrderBy(i => i.ItemName).ToListAsync();
            await ProductNames.FillDisplayNamesAsync(db, _allItems);
            FillItems();
        }
        PartySuggestions.Clear();
        foreach (var b in await db.FreeIssueBeneficiaries.AsNoTracking().Where(b => b.IsActive).OrderBy(b => b.Name).Select(b => b.Name).ToListAsync())
            PartySuggestions.Add(b);
        var counterId = CounterWarehouse?.Id;
        OtherWarehouses.Clear();
        foreach (var w in await db.Warehouses.AsNoTracking().Where(w => w.IsActive && w.Id != WarehouseId && w.WarehouseType != WarehouseType.WorkInProcess).OrderBy(w => w.Name).ToListAsync())
            OtherWarehouses.Add(w);
        _counterWarehouse = OtherWarehouses.FirstOrDefault(w => w.Id == counterId);
        OnPropertyChanged(nameof(CounterWarehouse));
        await LoadKpisAndBalancesAsync();
        await LoadReportsAsync();
    }

    private void FillItems()
    {
        var selected = LineItem?.Id;
        ItemsLookup.Clear();
        IEnumerable<Item> items = _allItems;
        if (!ShowAllItems)
        {
            if (WarehouseType == WarehouseType.RawMaterial) items = items.Where(i => i.SourcingMethod != SourcingMethod.Manufactured);
            else if (WarehouseType is WarehouseType.FinishedGoods or WarehouseType.RepVan) items = items.Where(i => i.SourcingMethod != SourcingMethod.Purchased);
        }
        var stock = Balances.GroupBy(b => b.ItemId).ToDictionary(g => g.Key, g => g.Sum(b => b.Quantity));
        foreach (var i in items)
        {
            var top = _packs.LevelsOf(i.Id).Select(l => l.EquivalentBaseUnits).DefaultIfEmpty(1).Max();
            (i.StockHint, i.StockState) = StockHint.Of(stock.GetValueOrDefault(i.Id), top, i.MinStockAlertLevel);
            ItemsLookup.Add(i);
        }
        if (selected is not null && ItemsLookup.All(i => i.Id != selected)) LineItem = null;
    }

    private async Task LoadKpisAndBalancesAsync()
    {
        await using var db = Session.NewDb();
        var balances = await new WarehouseDocumentService(db).GetBalancesAsync(WarehouseId);
        Balances.ReplaceAll(balances);
        ApplyBalanceFilter();
        ItemsCount = balances.Select(b => b.ItemId).Distinct().Count();
        _packs = await PackFormatter.LoadAsync(db);
        _names = await ProductNames.LoadAsync(db);
        FillItems();   // المتوفر بجانب كل صنف في قائمة الاختيار
        TotalPieces = balances.Sum(b => b.Quantity);
        // لا يُجمع ما اختلفت وحدته: إجمالي العبوات للمنتج التام فقط، والمواد الأولية تُعدّ أصنافًا
        var finished = balances.Where(b => b.IsFinished).ToList();
        var rawCount = balances.Where(b => !b.IsFinished).Select(b => b.ItemId).Distinct().Count();
        TotalLabel = finished.Count > 0 ? "إجمالي المنتج التام" : "إجمالي الرصيد";
        TotalText = finished.Count > 0 ? NamedTotal(finished.Select(b => (b.ItemId, b.RecipeId, b.Quantity))) + (rawCount > 0 ? $" + {rawCount} مادة أولية" : "")
                  : rawCount > 0 ? $"{rawCount} مادة أولية" : "0";
        OnPropertyChanged(nameof(PartsLayout));
        OnPropertyChanged(nameof(NameLayout));
        OnPropertyChanged(nameof(MixedLayout));
        LowCount = balances.Where(b => b.BelowAlert).Select(b => b.ItemId).Distinct().Count();
        if (HasAlerts)
        {
            await Alerts.LoadAsync(WarehouseId);
            LowCount = Alerts.LowCount;          // يشمل ما نفد رصيده كليًا
        }

        var today = DateTime.Today.ToUniversalTime();
        var tomorrow = DateTime.Today.AddDays(1).ToUniversalTime();
        var todayTx = db.StockTransactions.Where(t => t.WarehouseId == WarehouseId && t.TransactionDate >= today && t.TransactionDate < tomorrow);
        var todayByItem = await todayTx.GroupBy(t => t.ItemId)
            .Select(g => new { ItemId = g.Key, In = g.Sum(t => t.QuantityBaseUnits > 0 ? t.QuantityBaseUnits : 0), Out = g.Sum(t => t.QuantityBaseUnits < 0 ? -t.QuantityBaseUnits : 0) })
            .ToListAsync();
        TodayIn = todayByItem.Sum(t => t.In);
        TodayOut = todayByItem.Sum(t => t.Out);
        // نفس قاعدة الإجمالي: عبوات المنتج التام، والمواد الأولية تُعدّ أصنافًا (لا جمع لقطع مواد مختلفة)
        var finishedIds = (await db.Items.AsNoTracking().Where(i => todayByItem.Select(t => t.ItemId).Contains(i.Id)).ToListAsync())
            .Where(i => i.SourcingMethod != SourcingMethod.Purchased).Select(i => i.Id).ToHashSet();
        string Today(Func<decimal, bool> any, Func<(int ItemId, decimal In, decimal Out), decimal> qty)
        {
            var rows = todayByItem.Select(t => (t.ItemId, t.In, t.Out)).Where(t => any(qty(t))).ToList();
            var fin = rows.Where(t => finishedIds.Contains(t.ItemId)).ToList();
            var raw = rows.Count(t => !finishedIds.Contains(t.ItemId));
            if (fin.Count == 0) return raw > 0 ? $"{raw} مادة أولية" : "0";
            return _packs.Total(fin.Select(t => (t.ItemId, qty(t)))) + (raw > 0 ? $" + {raw} مادة أولية" : "");
        }
        TodayInText = Today(q => q > 0, t => t.In);
        TodayOutText = Today(q => q > 0, t => t.Out);

        var ledgerId = LedgerItem?.Id;
        LedgerItems.Clear();
        foreach (var id in balances.Select(b => b.ItemId).Distinct())
            if (_allItems.FirstOrDefault(i => i.Id == id) is { } item) LedgerItems.Add(item);
        _ledgerItem = LedgerItems.FirstOrDefault(i => i.Id == ledgerId);
        OnPropertyChanged(nameof(LedgerItem));
    }

    public async Task LoadReportsAsync()
    {
        if (ReportTo < ReportFrom) { Dialogs.Error("تاريخ النهاية قبل تاريخ البداية"); return; }
        await LoadDocumentsAsync();
        await using var db = Session.NewDb();
        Summary.Clear();
        foreach (var r in await new WarehouseDocumentService(db).GetSummaryAsync(WarehouseId, ReportFrom, ReportTo)) Summary.Add(r);
        await LoadLedgerAsync();
    }

    private async Task LoadDocumentsAsync()
    {
        await using var db = Session.NewDb();
        StockDocumentType? type = DocumentFilter is null || ReferenceEquals(DocumentFilter, DocumentFilters[0]) ? null : DocumentFilter.Value;
        Documents.Clear();
        foreach (var d in await new WarehouseDocumentService(db).GetDocumentsAsync(WarehouseId, ReportFrom, ReportTo, type)) Documents.Add(d);
    }

    private async Task LoadLedgerAsync()
    {
        await using var db = Session.NewDb();
        Ledger.Clear();
        // الأحدث أولًا (طلب المالك)؛ الرصيد الجاري يُحسب تصاعديًا في الخدمة ثم يُعكس العرض
        foreach (var r in Enumerable.Reverse(await new WarehouseDocumentService(db).GetLedgerAsync(WarehouseId, LedgerItem?.Id, ReportFrom, ReportTo))) Ledger.Add(r);
        OnPropertyChanged(nameof(LedgerPartsLayout));
        OnPropertyChanged(nameof(LedgerNameLayout));
    }

    // ============================ إدخال السطور ============================
    private async Task OnLineItemChangedAsync()
    {
        LevelOptions.Clear();
        LineLevel = null;
        VariantOptions.Clear();
        _lineVariant = null;
        OnPropertyChanged(nameof(LineVariant));
        OnPropertyChanged(nameof(ShowVariant));
        if (LineItem is null) { BatchOptions.Clear(); OnPropertyChanged(nameof(LineAvailableText)); return; }
        await using var db = Session.NewDb();
        foreach (var l in await db.ItemPackagingLevels.AsNoTracking().Where(l => l.ItemId == LineItem.Id).OrderByDescending(l => l.EquivalentBaseUnits).ToListAsync())
            LevelOptions.Add(l);
        LineLevel = LevelOptions.FirstOrDefault();
        if (LineItem.SourcingMethod != SourcingMethod.Purchased)
        {
            var itemId = LineItem.Id;
            var names = await ProductNames.LoadAsync(db, new[] { itemId });
            VariantOptions.Add(new VariantChoice { RecipeId = null, Label = names.Variant(null) + " (أساسي)" });
            foreach (var r in await db.CustomRecipes.AsNoTracking().Where(r => r.FinishedItemId == itemId && r.IsActive).OrderBy(r => r.Name).Select(r => r.Id).ToListAsync())
                VariantOptions.Add(new VariantChoice { RecipeId = r, Label = names.Variant(r) });
            _lineVariant = VariantOptions[0];
            OnPropertyChanged(nameof(LineVariant));
            OnPropertyChanged(nameof(ShowVariant));
        }
        await RefreshBatchesAsync();
    }

    private async Task RefreshBatchesAsync()
    {
        BatchOptions.Clear();
        if (LineItem is null) { LineBatch = null; return; }
        await using var db = Session.NewDb();
        if (IsReceipt)
        {
            BatchOptions.Add(new BatchChoice { BatchId = null, Label = "بدون تشغيلة / تشغيلة جديدة (اكتب رقمها)" });
            // تشغيلات المتغير المختار فقط (لا يُدخَل طلب خاص على تشغيلة الأساسي أو العكس)
            var recipe = ShowVariant ? LineVariant?.RecipeId : null;
            foreach (var b in await db.ItemBatches.AsNoTracking().Where(b => b.ItemId == LineItem.Id && (!ShowVariant || b.CustomRecipeId == recipe)).OrderByDescending(b => b.Id).ToListAsync())
                BatchOptions.Add(new BatchChoice { BatchId = b.Id, RecipeId = b.CustomRecipeId, Label = b.BatchNumber + (b.ExpiryDate is { } e ? $" — ينتهي {e:yyyy/MM/dd}" : "") });
        }
        else
        {
            var itemId = LineItem.Id;
            var rows = await db.StockTransactions.Where(t => t.ItemId == itemId && t.WarehouseId == WarehouseId && t.BatchId != null)
                .GroupBy(t => t.BatchId).Select(g => new { BatchId = g.Key, Qty = g.Sum(t => t.QuantityBaseUnits) }).Where(x => x.Qty > 0).ToListAsync();
            var noBatch = await db.StockTransactions.Where(t => t.ItemId == itemId && t.WarehouseId == WarehouseId && t.BatchId == null)
                .SumAsync(t => (decimal?)t.QuantityBaseUnits) ?? 0;
            var ids = rows.Select(r => r.BatchId!.Value).ToList();
            var batches = await db.ItemBatches.AsNoTracking().Where(b => ids.Contains(b.Id)).ToDictionaryAsync(b => b.Id);
            // صافي الرصيد (رصيد سالب بلا تشغيلة يُنقص المتاح) — نفس قاعدة الخدمة
            var positive = rows.Sum(r => r.Qty) + Math.Max(noBatch, 0);
            BatchOptions.Add(new BatchChoice { BatchId = null, Label = "تلقائي (الأقرب انتهاءً)", Available = Math.Min(positive, rows.Sum(r => r.Qty) + noBatch) });
            foreach (var r in rows.OrderBy(r => batches[r.BatchId!.Value].ExpiryDate ?? DateTime.MaxValue))
            {
                var b = batches[r.BatchId!.Value];
                BatchOptions.Add(new BatchChoice
                {
                    BatchId = b.Id, Available = r.Qty,
                    Label = $"{b.BatchNumber}" + (b.ExpiryDate is { } e ? $" — ينتهي {e:yyyy/MM/dd}" : "") + $" — {_packs.Of(itemId, r.Qty)}"
                });
            }
        }
        LineBatch = BatchOptions.FirstOrDefault();
        OnPropertyChanged(nameof(LineAvailableText));
    }

    private async Task AddLineAsync()
    {
        if (LineItem is null || LineLevel is null) { Dialogs.Error("اختر الصنف ووحدة التعبئة"); return; }
        if (LineQuantity <= 0) { Dialogs.Error("الكمية يجب أن تكون أكبر من صفر"); return; }
        var pieces = LineQuantity * LineLevel.EquivalentBaseUnits;
        if (IsOutgoing)
        {
            // فحص مبدئي يشمل السطور المضافة لنفس الصنف/التشغيلة (والخدمة تتحقق نهائيًا عند الحفظ)
            var available = LineBatch?.BatchId is null ? BatchOptions.FirstOrDefault()?.Available ?? 0 : LineBatch.Available ?? 0;
            var already = Lines.Where(l => l.ItemId == LineItem.Id && (LineBatch?.BatchId is null || l.BatchId == LineBatch.BatchId)).Sum(l => l.Pieces);
            if (pieces + already > available) { Dialogs.Error($"الكمية أكبر من المتاح ({_packs.Of(LineItem.Id, available - already)})"); return; }
        }
        var newBatch = IsReceipt && LineBatch?.BatchId is null && !string.IsNullOrWhiteSpace(LineNewBatch) ? LineNewBatch.Trim() : null;
        var variant = ShowVariant ? LineVariant?.RecipeId : null;
        Lines.Add(new DocumentLineDraft
        {
            ItemId = LineItem.Id, ItemCode = LineItem.ItemCode,
            ItemName = variant is null ? LineItem.ItemName : $"{LineItem.ItemName} — {LineVariant!.Label}", CustomRecipeId = variant,
            PackagingLevelId = LineLevel.Id, LevelName = LineLevel.LevelName, QuantityInLevel = LineQuantity, Pieces = pieces,
            BatchId = LineBatch?.BatchId, NewBatchNumber = newBatch, NewBatchExpiry = newBatch is null ? null : LineExpiry,
            BatchLabel = newBatch is not null ? $"{newBatch} (جديدة)" + (LineExpiry is { } e ? $" — {e:yyyy/MM/dd}" : "")
                       : LineBatch?.BatchId is null ? (IsReceipt ? "—" : "تلقائي") : LineBatch.Label
        });
        OnPropertyChanged(nameof(LinesTotalText));
        LineQuantity = 1;
        LineNewBatch = null;
        LineExpiry = null;
        await Task.CompletedTask;
    }

    private async Task SaveAsync()
    {
        if (!Require(CanAdd, "تسجيل مستندات المخزن")) return;
        if (Lines.Count == 0) { Dialogs.Error("أضف صنفًا واحدًا على الأقل"); return; }
        // تأكيد قبل الحفظ في حالتين فقط: عملية حساسة (مناقلة، تالف، مسحوب مجاني) أو كمية غير معتادة لصنف
        var unusual = await UnusualQuantityAsync();
        var sensitive = Operation.Value is StockDocumentType.Transfer or StockDocumentType.Damaged or StockDocumentType.FreeIssue;
        if (sensitive || unusual is not null)
        {
            var confirm = $"حفظ مستند {Operation.Label} ({Lines.Count} سطر، {LinesPacks})" +
                          (IsTransfer && CounterWarehouse is not null ? $" إلى \"{CounterWarehouse.Name}\"" : "") + "؟" +
                          (unusual is null ? "" : $"\n\n⚠ كمية غير معتادة: {unusual}");
            if (!Dialogs.Confirm(confirm)) return;
        }

        IsBusy = true;
        try
        {
            await using var db = Session.NewDb();
            var request = new StockDocumentRequest(Operation.Value, WarehouseId, DocumentDate,
                Lines.Select(l => new StockDocumentLineInput(l.ItemId, l.PackagingLevelId, l.QuantityInLevel, l.BatchId, l.NewBatchNumber, l.NewBatchExpiry,
                                                          CustomRecipeId: l.CustomRecipeId)).ToList(),
                Session.UserId, IsTransfer ? CounterWarehouse?.Id : null, NeedsParty ? PartyName : null,
                IsDamaged ? DamageReason?.Value : null, Notes, MoveToDamaged,
                DamageCause: IsDamaged ? DamageCause?.Value : null);
            var (result, doc) = await new WarehouseDocumentService(db).CreateAsync(request);
            if (!result.Success) { Dialogs.Error(result.ErrorMessage!); return; }
            LastDocument = doc;
            StatusMessage = $"تم حفظ المستند {doc!.DocumentNumber}";
            if (Dialogs.Confirm($"تم حفظ المستند {doc.DocumentNumber}.\n\nطباعة المستند الآن؟")) await PrintDocumentAsync(doc.Id);
            ClearForm();
            await LoadKpisAndBalancesAsync();
            await LoadReportsAsync();
            await RefreshBatchesAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// كمية غير معتادة: أكثر من 3 أضعاف معدل آخر 20 مستندًا للصنف في هذا المخزن أو أقل من ثلثه
    /// (لا يُطبَّق قبل وجود 5 مستندات سابقة). يعيد وصف أول سطر غير معتاد أو null.
    /// </summary>
    private async Task<string?> UnusualQuantityAsync()
    {
        await using var db = Session.NewDb();
        var type = Operation.Value;
        foreach (var g in Lines.GroupBy(l => l.ItemId))
        {
            var pieces = g.Sum(l => l.Pieces);
            var history = await db.StockDocumentLines.AsNoTracking()
                .Where(l => l.ItemId == g.Key && l.StockDocument.WarehouseId == WarehouseId && l.StockDocument.DocumentType == type && !l.StockDocument.IsVoided)
                .GroupBy(l => l.StockDocumentId).Select(x => new { Id = x.Key, Qty = x.Sum(l => l.QuantityBaseUnits) })
                .OrderByDescending(x => x.Id).Take(20).ToListAsync();
            if (history.Count < 5) continue;
            var avg = history.Average(x => x.Qty);
            if (avg <= 0 || (pieces <= avg * 3 && pieces >= avg / 3)) continue;
            return $"{g.First().ItemName}: {_packs.Of(g.Key, pieces)} والمعتاد نحو {_packs.Of(g.Key, Math.Round(avg))}";
        }
        return null;
    }

    private async Task VoidDocumentAsync(StockDocumentRow row)
    {
        if (row.IsVoided) { Dialogs.Error("المستند ملغى مسبقًا"); return; }
        if (!Require(CanVoidDocuments, "إلغاء المستندات المرحّلة")) return;
        if (string.IsNullOrWhiteSpace(VoidReason)) { Dialogs.Error("اكتب سبب الإلغاء في الخانة أعلى القائمة أولًا"); return; }
        if (!Dialogs.Confirm($"إلغاء المستند {row.DocumentNumber} ({row.QuantityText})؟\n\nيُعكس أثره على الرصيد ويبقى ظاهرًا بحالة «ملغى».\nالسبب: {VoidReason!.Trim()}")) return;
        await using var db = Session.NewDb();
        var result = await new WarehouseDocumentService(db).VoidAsync(row.Id, VoidReason!, Session.UserId);
        if (!result.Success) { Dialogs.Error(result.ErrorMessage!); return; }
        VoidReason = null;
        StatusMessage = $"أُلغي المستند {row.DocumentNumber}";
        await LoadKpisAndBalancesAsync();
        await LoadReportsAsync();
    }

    private async Task CopyDocumentAsync(StockDocumentRow row)
    {
        await using var db = Session.NewDb();
        var d = await new WarehouseDocumentService(db).GetDocumentAsync(row.Id);
        if (d is null) { Dialogs.Error("المستند غير موجود"); return; }
        var op = OperationOptions.FirstOrDefault(o => o.Value == d.DocumentType);
        if (op is null || d.WarehouseId != WarehouseId) { Dialogs.Error("يُنسخ المستند من مخزنه المصدر وبنوع متاح في «عملية جديدة»"); return; }
        if (Lines.Count > 0 && !Dialogs.Confirm("في «عملية جديدة» سطور غير محفوظة — استبدالها بسطور المستند المنسوخ؟")) return;
        Lines.Clear();
        Operation = op;
        Lines.Clear();
        var names = await ProductNames.LoadAsync(db, d.Lines.Select(l => l.ItemId));
        foreach (var l in d.Lines)
        {
            var recipe = l.CustomRecipeId ?? l.Batch?.CustomRecipeId;
            Lines.Add(new DocumentLineDraft
            {
                ItemId = l.ItemId, ItemCode = l.Item.ItemCode,
                ItemName = recipe is null ? l.Item.ItemName : $"{l.Item.ItemName} — {names.Variant(recipe)}", CustomRecipeId = recipe,
                PackagingLevelId = l.PackagingLevelId, LevelName = l.PackagingLevel.LevelName, QuantityInLevel = l.QuantityInLevel, Pieces = l.QuantityBaseUnits,
                BatchId = l.BatchId, BatchLabel = l.Batch?.BatchNumber ?? (IsReceipt ? "—" : "تلقائي")
            });
        }
        PartyName = d.PartyName;
        Notes = $"نسخة من {d.DocumentNumber}" + (string.IsNullOrWhiteSpace(d.Notes) ? "" : $" — {d.Notes}");
        if (d.CounterWarehouseId is int cw) CounterWarehouse = OtherWarehouses.FirstOrDefault(w => w.Id == cw);
        if (d.DamageReason is { } dr) DamageReason = ReasonOptions.FirstOrDefault(o => o.Value == dr) ?? DamageReason;
        if (d.DamageCause is { } dc) DamageCause = CauseOptions.FirstOrDefault(o => o.Value == dc);
        OnPropertyChanged(nameof(LinesTotalText));
        SelectedTabIndex = 0;
        StatusMessage = $"نُسخ {d.DocumentNumber} إلى «عملية جديدة» — عدّل ثم احفظ" + (d.IsVoided ? "" : " (ألغِ الأصل إن كان خطأ)");
    }

    private void ClearForm()
    {
        Lines.Clear();
        PartyName = null;
        Notes = null;
        DamageCause = null;
        LineQuantity = 1;
        LineNewBatch = null;
        LineExpiry = null;
        OnPropertyChanged(nameof(LinesTotalText));
    }

    // ============================ الطباعة ============================
    private ReportDocument NewReport(string title, string? stamp = null) =>
        new() { CompanyName = Session.ProjectName, Title = title, Stamp = stamp, PrintedBy = Session.Username };

    public async Task PrintDocumentAsync(int documentId)
    {
        await using var db = Session.NewDb();
        var d = await new WarehouseDocumentService(db).GetDocumentAsync(documentId);
        if (d is null) { Dialogs.Error("المستند غير موجود"); return; }
        Dialogs.ShowReport(await BuildDocumentReportAsync(db, d));
    }

    public static async Task<ReportDocument> DocumentReportAsync(AppSession session, Data.ProjectDb.ProjectDbContext db, StockDocument d)
    {
        var ids = d.Lines.Select(l => l.ItemId).Distinct().ToList();
        return DocumentReports.StockDocumentReport(session, d, await ProductNames.LoadAsync(db, ids), await PackFormatter.LoadAsync(db, ids));
    }

    private Task<ReportDocument> BuildDocumentReportAsync(Data.ProjectDb.ProjectDbContext db, StockDocument d) => DocumentReportAsync(Session, db, d);

    /// <summary>مجموع بالاسم الكامل لكل (منتج، متغير)؛ قبل تحميل الأسماء يرجع للمجموع العادي.</summary>
    private string NamedTotal(IEnumerable<(int ItemId, int? RecipeId, decimal Pieces)> q) =>
        _names is null ? _packs.Total(q.Select(x => (x.ItemId, x.Pieces))) : _packs.TotalNamed(q, _names);

    public ReportDocument BuildBalancesReport()
    {
        var r = NewReport($"أرصدة {Title}");
        r.Field("المخزن", Title).Field("النوع", WarehouseTypeLabel).Field("حتى تاريخ", DateTime.Now.ToString("yyyy/MM/dd HH:mm"));
        if (HasSearch) r.Field("تصفية البحث", SearchText.Trim());
        // الاسم الموحد للمنتج التام (يحمل العبوة)، والكمية رقم؛ الكود عمود صغير للمرجع في آخر الجدول
        r.Columns.AddRange(new[] { "#", "الصنف", "التشغيلة", "الصلاحية", "الرصيد", "بالقطعة", "الكود" });
        var i = 0;
        foreach (var b in VisibleBalances)
            r.Rows.Add(new[] { (++i).ToString(), b.DisplayName + (b.BelowAlert ? " ⚠" : ""), b.BatchNumber ?? "—",
                               b.ExpiryDate?.ToString("yyyy/MM/dd") ?? "", b.QuantityText, $"{b.Quantity:N0}", b.ItemCode });
        if (HasSearch)
            r.Total("عدد الأصناف في النتائج", VisibleBalances.Select(b => b.ItemId).Distinct().Count().ToString())
             .Total("إجمالي النتائج", NamedTotal(VisibleBalances.Where(b => b.IsFinished).Select(b => (b.ItemId, b.RecipeId, b.Quantity))), true);
        else
        {
            r.Total("عدد الأصناف", ItemsCount.ToString()).Total(TotalLabel, TotalText, true);
            if (LowCount > 0) r.Total("أصناف عند حد التنبيه", LowCount.ToString());
        }
        r.Signatures.AddRange(new[] { "أمين المخزن", "المدقق" });
        return r;
    }

    public ReportDocument BuildSummaryReport()
    {
        var r = NewReport($"تقرير حركة {Title}");
        r.Field("المخزن", Title).Field("الفترة", $"{ReportFrom:yyyy/MM/dd} — {ReportTo:yyyy/MM/dd}");
        r.Columns.AddRange(new[] { "الصنف", "أول المدة", "وارد", "صادر", "تالف", "مجاني", "المتبقي", "الكود" });
        string Q(StockSummaryRow s, decimal pieces) => s.IsFinished ? _packs.Count(s.ItemId, pieces) : _packs.Of(s.ItemId, pieces);
        foreach (var s in Summary)
            r.Rows.Add(new[] { s.DisplayName, Q(s, s.Opening), Q(s, s.In), Q(s, s.Out), Q(s, s.Damaged), Q(s, s.Free), Q(s, s.Closing), s.ItemCode });
        string Sum(Func<StockSummaryRow, decimal> f) => NamedTotal(Summary.Where(s => s.IsFinished).Select(s => (s.ItemId, s.RecipeId, f(s))));
        r.Total("إجمالي الوارد", Sum(s => s.In))
         .Total("إجمالي الصادر", Sum(s => s.Out))
         .Total("إجمالي التالف", Sum(s => s.Damaged))
         .Total("إجمالي المجاني", Sum(s => s.Free))
         .Total("المتبقي آخر المدة", Sum(s => s.Closing), true);
        r.Signatures.AddRange(new[] { "أمين المخزن", "المدقق", "المدير" });
        return r;
    }

    public ReportDocument BuildLedgerReport()
    {
        var r = NewReport($"كشف حركة {Title}");
        r.Field("المخزن", Title).Field("الصنف", LedgerItem?.ItemName ?? "كل الأصناف").Field("الفترة", $"{ReportFrom:yyyy/MM/dd} — {ReportTo:yyyy/MM/dd}");
        r.Columns.AddRange(LedgerItem is null
            ? new[] { "التاريخ", "الصنف", "الحركة", "المستند", "التشغيلة", "وارد", "صادر" }
            : new[] { "التاريخ", "الحركة", "المستند", "التشغيلة", "الجهة", "وارد", "صادر", "الرصيد" });
        foreach (var x in Ledger)
            r.Rows.Add(LedgerItem is null
                ? new[] { x.Date.ToString("yyyy/MM/dd"), x.DisplayName.Length > 0 ? x.DisplayName : x.ItemName, x.TypeLabel, x.Reference, x.BatchNumber ?? "", x.InText, x.OutText }
                : new[] { x.Date.ToString("yyyy/MM/dd"), x.TypeLabel, x.Reference, x.BatchNumber ?? "", x.Party ?? "", x.InText, x.OutText, x.BalanceText });
        r.Total("إجمالي الوارد", NamedTotal(Ledger.Where(x => x.ItemId is not null && x.IsFinished).Select(x => (x.ItemId!.Value, x.RecipeId, x.In))))
         .Total("إجمالي الصادر", NamedTotal(Ledger.Where(x => x.ItemId is not null && x.IsFinished).Select(x => (x.ItemId!.Value, x.RecipeId, x.Out))));
        if (LedgerItem is not null) r.Total("الرصيد آخر المدة", Ledger.FirstOrDefault()?.BalanceText ?? "0", true);
        return r;
    }
}
