using System.Collections.ObjectModel;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Sales;

public class SalesModuleViewModel : ModuleViewModel
{
    public SalesModuleViewModel(AppSession s, IDialogService d)
        : base("المبيعات", Icons.Sales, ModuleColors.Sales)
    {
        UseDashboard(s, d, ModuleCode.Sales, ModuleDashboardViewModel.Sales);
        Invoice = Add(new SalesInvoiceSectionViewModel(s, d));
        InvoiceList = Add(new SalesInvoiceListSectionViewModel(s, d, OpenInvoiceAsync));
        Statement = Add(new CustomerStatementSectionViewModel(s, d));
        Deposits = Add(new CustomerDepositsSectionViewModel(s, d));
        Add(new CustomersSectionViewModel(s, d));
        Add(new AgentPricesSectionViewModel(s, d));
        Add(new LoadingSettingsSectionViewModel(s, d));
        // صندوق موظف المبيعات: تدخله مبيعاته النقدية، ويسلّم منه للصندوق الرئيسي
        MyBox = Add(new Finance.CashBoxesSectionViewModel(s, d, ModuleCode.Sales, "صندوقي"));
    }

    public Finance.CashBoxesSectionViewModel MyBox { get; }

    public SalesInvoiceSectionViewModel Invoice { get; }
    public SalesInvoiceListSectionViewModel InvoiceList { get; }
    public CustomerStatementSectionViewModel Statement { get; }
    public CustomerDepositsSectionViewModel Deposits { get; }

    /// <summary>فتح فاتورة من القائمة داخل تبويب الفاتورة (مسودة للتعديل، مرحّلة للعرض فقط).</summary>
    public async Task OpenInvoiceAsync(int invoiceId)
    {
        SelectedTab = Invoice;
        await LastActivation;
        await Invoice.LoadInvoiceAsync(invoiceId);
    }
}

// ================================================================
//                         فاتورة المبيعات
// ================================================================
public class InvoiceLineDraft : ObservableObject
{
    private decimal _quantityInLevel;
    private decimal _unitPrice;
    private readonly Action _changed;

    public InvoiceLineDraft(Action changed) => _changed = changed;

    public int ItemId { get; init; }
    public string ItemCode { get; init; } = "";
    public string ItemName { get; init; } = "";
    public int PackagingLevelId { get; init; }
    public string LevelName { get; init; } = "";
    public decimal BaseUnitsPerLevel { get; init; }
    public int? BatchId { get; init; }
    public string BatchLabel { get; init; } = "تلقائي (الأقرب انتهاءً)";

    /// <summary>true = السعر عُدّل يدويًا، فلا يُعاد تسعيره عند تغيير العميل.</summary>
    public bool IsManualPrice { get; set; }

    public decimal QuantityInLevel
    {
        get => _quantityInLevel;
        set { if (SetProperty(ref _quantityInLevel, value)) { OnPropertyChanged(nameof(BaseUnits)); OnPropertyChanged(nameof(LineTotal)); _changed(); } }
    }

    public decimal UnitPrice
    {
        get => _unitPrice;
        set
        {
            if (!SetProperty(ref _unitPrice, value)) return;
            IsManualPrice = true;
            OnPropertyChanged(nameof(LineTotal));
            _changed();
        }
    }

    internal void SetSuggestedPrice(decimal price)
    {
        _unitPrice = price;
        IsManualPrice = false;
        OnPropertyChanged(nameof(UnitPrice));
        OnPropertyChanged(nameof(LineTotal));
    }

    public decimal BaseUnits => QuantityInLevel * BaseUnitsPerLevel;
    public decimal LineTotal => Math.Round(QuantityInLevel * UnitPrice, 2);
}

public class BatchOption
{
    public int? BatchId { get; init; }
    public string Label { get; init; } = "";
    public override string ToString() => Label;
}

/// <summary>نوع البيع في رأس الفاتورة: يحدد المخزن ومصدر الأصناف.</summary>
public enum SaleMode { Direct, Rep, RawMaterials }

public class SalesInvoiceSectionViewModel : SectionViewModel
{
    /// <summary>فاتورة قيد الإدخال (سطور أو مسودة مفتوحة): لا تُعاد تعبئة القوائم تحتها.</summary>
    protected override bool HasPendingInput => Lines.Count > 0 || InvoiceId is not null || Customer is not null;

    private int? _invoiceId;
    private string _invoiceNumber = "فاتورة جديدة";
    private bool _isReadOnly;
    private Customer? _customer;
    private Data.ProjectDb.Entities.Warehouse? _warehouse;
    private DateTime _invoiceDate = DateTime.Today;
    private Option<InvoicePaymentMethod> _paymentMethod;
    private decimal _amountPaidNow;
    private bool _taxEnabled;
    private decimal _taxRate = 14;
    private bool _loadingEnabled;
    private decimal _loadingRate;
    private bool _useAgentPricing = true;
    private bool _isFreeSale;
    private string? _freeSaleRecipient;
    private string? _notes;
    private Item? _lineItem;
    private ItemPackagingLevel? _lineLevel;
    private BatchOption? _lineBatch;
    private decimal _lineQuantity = 1;
    private decimal _linePrice;
    private decimal? _lineAvailable;
    private bool _suppressReprice;

    public SalesInvoiceSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Sales, "فاتورة مبيعات", Icons.Invoice, "#10B981",
               "بيع بتسعير هرمي، رسوم تحميل، بيع مجاني، وترحيل تلقائي للمخزون والقيد")
    {
        _paymentMethod = PaymentOptions[0];
        NewInvoiceCommand = new RelayCommand(() => { if (ConfirmDiscard()) ResetForm(); });
        AddLineCommand = new AsyncRelayCommand(AddLineAsync);
        RemoveLineCommand = new RelayCommand(p => { if (p is InvoiceLineDraft l && !IsReadOnly) { Lines.Remove(l); RaiseTotals(); } });
        SaveDraftCommand = new AsyncRelayCommand(async () => { if (await SaveDraftAsync()) StatusMessage = $"تم حفظ المسودة {InvoiceNumber}"; });
        PostCommand = new AsyncRelayCommand(PostAsync);
        PrintCommand = new RelayCommand(() =>
        {
            if (Lines.Count == 0) { Dialogs.Error("لا توجد سطور للطباعة"); return; }
            Dialogs.ShowReport(BuildReport());
        });
    }

    // ---------------- القوائم ----------------
    public IReadOnlyList<Option<InvoicePaymentMethod>> PaymentOptions { get; } = ArabicLabels.OptionsOf<InvoicePaymentMethod>();
    public ObservableCollection<Customer> Customers { get; } = new();
    public ObservableCollection<Data.ProjectDb.Entities.Warehouse> Warehouses { get; } = new();
    public ObservableCollection<Item> ItemsLookup { get; } = new();
    public ObservableCollection<ItemPackagingLevel> LevelOptions { get; } = new();
    public ObservableCollection<BatchOption> BatchOptions { get; } = new();
    public ObservableCollection<InvoiceLineDraft> Lines { get; } = new();

    // ---------------- الرأس ----------------
    public int? InvoiceId { get => _invoiceId; private set => SetProperty(ref _invoiceId, value); }
    public string InvoiceNumber { get => _invoiceNumber; private set => SetProperty(ref _invoiceNumber, value); }

    /// <summary>فاتورة مرحّلة مفتوحة للعرض — كل الحقول مقفلة.</summary>
    public bool IsReadOnly
    {
        get => _isReadOnly;
        private set { if (SetProperty(ref _isReadOnly, value)) { OnPropertyChanged(nameof(IsEditable)); OnPropertyChanged(nameof(IsWarehouseSelectable)); } }
    }
    public bool IsEditable => !IsReadOnly;

    public Customer? Customer
    {
        get => _customer;
        set
        {
            if (!SetProperty(ref _customer, value)) return;
            OnPropertyChanged(nameof(PricingTypeLabel));
            OnPropertyChanged(nameof(PricingTypeColor));
            OnPropertyChanged(nameof(IsAgentOrSub));
            if (!_suppressReprice) Background(RepriceAsync());
        }
    }

    public Data.ProjectDb.Entities.Warehouse? Warehouse
    {
        get => _warehouse;
        set
        {
            if (!SetProperty(ref _warehouse, value)) return;
            OnPropertyChanged(nameof(IsVanSale));
            RebuildItems();
            Background(RefreshLineStockAsync());
        }
    }

    // ---------------- نوع البيع وتصفية الأصناف ----------------
    private readonly List<Data.ProjectDb.Entities.Warehouse> _allWarehouses = new();
    private readonly List<Item> _allItems = new();
    private Dictionary<(int wh, int item), decimal> _stock = new();
    private Dictionary<int, HashSet<string>> _itemLevels = new();
    private Option<SaleMode>? _saleMode;
    private string _packFilter = AllPacks;
    private const string AllPacks = "الكل";

    /// <summary>بيع مباشر (مخزن المنتج التام فقط)، أو من سيارة مندوب، أو مواد أولية (بصلاحية خاصة).</summary>
    public ObservableCollection<Option<SaleMode>> SaleModes { get; } = new();
    public Option<SaleMode>? SaleModeOption
    {
        get => _saleMode;
        set
        {
            if (value is null || !SetProperty(ref _saleMode, value)) return;
            OnPropertyChanged(nameof(IsWarehouseSelectable));
            OnPropertyChanged(nameof(WarehouseHint));
            ApplyModeWarehouses();
        }
    }
    public SaleMode Mode => _saleMode?.Value ?? SaleMode.Direct;

    /// <summary>البيع المباشر من مخزن المنتج التام دون اختيار؛ الاختيار للمندوب والمواد الأولية أو عند وجود أكثر من مخزن تام.</summary>
    public bool IsWarehouseSelectable => IsEditable && (Mode != SaleMode.Direct || Warehouses.Count > 1);
    public string WarehouseHint => Mode switch
    {
        SaleMode.Direct => "البيع المباشر من مخزن المنتج التام — تظهر الأصناف المتوفرة فقط",
        SaleMode.Rep => "اختر سيارة المندوب — تظهر أصناف حمولتها فقط",
        _ => "اختر مخزن المواد الأولية ثم المادة"
    };

    /// <summary>أزرار تصفية الأصناف حسب صيغة التعبئة (الكل، شرنك، كارتون...).</summary>
    public ObservableCollection<string> PackFilters { get; } = new();
    public string PackFilter { get => _packFilter; set { if (SetProperty(ref _packFilter, value ?? AllPacks)) RebuildItems(); } }

    private void ApplyModeWarehouses()
    {
        var keep = Warehouse;
        Warehouses.Clear();
        var type = Mode switch { SaleMode.Rep => WarehouseType.RepVan, SaleMode.RawMaterials => WarehouseType.RawMaterial, _ => WarehouseType.FinishedGoods };
        foreach (var w in _allWarehouses.Where(w => w.WarehouseType == type)) Warehouses.Add(w);
        Warehouse = Warehouses.FirstOrDefault(w => w.Id == keep?.Id) ?? Warehouses.FirstOrDefault();
        OnPropertyChanged(nameof(IsWarehouseSelectable));
    }

    /// <summary>
    /// الأصناف المعروضة = ما رصيده أكبر من صفر في المخزن المختار، مع تصفية الصيغة.
    /// من يملك صلاحية "البيع بانتظار الإنتاج" يرى كل المنتجات المصنّعة في البيع المباشر حتى بلا رصيد.
    /// </summary>
    private void RebuildItems()
    {
        var keep = LineItem;
        ItemsLookup.Clear();
        if (Warehouse is not null)
        {
            var pending = Mode == SaleMode.Direct && Has(SpecialPermission.SellPendingProduction);
            foreach (var i in _allItems)
            {
                var inStock = _stock.GetValueOrDefault((Warehouse.Id, i.Id)) > 0;
                if (!inStock && !(pending && i.SourcingMethod == SourcingMethod.Manufactured)) continue;
                if (PackFilter != AllPacks && !(_itemLevels.TryGetValue(i.Id, out var lv) && lv.Contains(PackFilter))) continue;
                ItemsLookup.Add(i);
            }
        }
        if (keep is not null && !ItemsLookup.Any(i => i.Id == keep.Id)) LineItem = null;
    }

    public bool IsVanSale => Warehouse?.WarehouseType == WarehouseType.RepVan;

    private bool _handOverNow;
    /// <summary>فاتورة سيارة مندوب: النقد المقبوض يُسلَّم للصندوق فور الترحيل بدل بقائه في محفظة المندوب.</summary>
    public bool HandOverNow { get => _handOverNow; set => SetProperty(ref _handOverNow, value); }

    public DateTime InvoiceDate
    {
        get => _invoiceDate;
        set { if (SetProperty(ref _invoiceDate, value)) Background(RefreshLoadingRateAsync()); }
    }

    public Option<InvoicePaymentMethod> PaymentMethod
    {
        get => _paymentMethod;
        set { if (SetProperty(ref _paymentMethod, value)) { OnPropertyChanged(nameof(IsPartial)); RaiseTotals(); } }
    }
    public bool IsPartial => PaymentMethod.Value == InvoicePaymentMethod.Partial;
    public decimal AmountPaidNow { get => _amountPaidNow; set { if (SetProperty(ref _amountPaidNow, value)) RaiseTotals(); } }

    public bool TaxEnabled { get => _taxEnabled; set { if (SetProperty(ref _taxEnabled, value)) RaiseTotals(); } }
    public decimal TaxRate { get => _taxRate; set { if (SetProperty(ref _taxRate, value)) RaiseTotals(); } }
    public bool LoadingEnabled { get => _loadingEnabled; set { if (SetProperty(ref _loadingEnabled, value)) RaiseTotals(); } }
    public decimal LoadingRate { get => _loadingRate; private set { if (SetProperty(ref _loadingRate, value)) RaiseTotals(); } }

    public bool UseAgentPricing
    {
        get => _useAgentPricing;
        set
        {
            if (!SetProperty(ref _useAgentPricing, value)) return;
            OnPropertyChanged(nameof(PricingTypeLabel));
            if (!_suppressReprice) Background(RepriceAsync());
        }
    }

    public bool IsFreeSale
    {
        get => _isFreeSale;
        set { if (SetProperty(ref _isFreeSale, value)) { OnPropertyChanged(nameof(FreeSaleNotice)); RaiseTotals(); } }
    }
    public string? FreeSaleRecipient { get => _freeSaleRecipient; set => SetProperty(ref _freeSaleRecipient, value); }
    public string FreeSaleNotice => IsFreeSale ? "بيع مجاني: يُخصم من المخزون باسم الجهة المستفيدة، بلا قيد مالي وبلا أثر على رصيد العميل" : "";
    public string? Notes { get => _notes; set => SetProperty(ref _notes, value); }

    public bool IsAgentOrSub => Customer?.CustomerType is CustomerType.Agent or CustomerType.SubCustomer;

    /// <summary>نوع التسعير المطبّق يُعرض تلقائيًا بمجرد اختيار العميل.</summary>
    public string PricingTypeLabel => Customer switch
    {
        null => "اختر العميل لعرض نوع التسعير",
        { CustomerType: CustomerType.Agent } when UseAgentPricing => "وكيل — يُطبَّق سعر الوكيل الخاص (أو السعر العادي للصنف بلا سعر خاص)",
        { CustomerType: CustomerType.SubCustomer } when UseAgentPricing =>
            $"عميل فرعي تابع لـ \"{Customer.ParentAgent?.Name}\" — يرث سعر وكيله",
        { CustomerType: CustomerType.Direct } => "عميل مباشر — سعر البيع العادي",
        _ => $"{ArabicLabels.Of(Customer.CustomerType)} — تم إلغاء تسعير الوكيل: سعر البيع العادي"
    };

    public string PricingTypeColor => Customer?.CustomerType switch
    {
        CustomerType.Agent => "#8B5CF6",
        CustomerType.SubCustomer => "#0EA5E9",
        CustomerType.Direct => "#10B981",
        _ => "#94A3B8"
    };

    // ---------------- إدخال سطر ----------------
    public Item? LineItem
    {
        get => _lineItem;
        set { if (SetProperty(ref _lineItem, value)) Background(OnLineItemChangedAsync()); }
    }
    public ItemPackagingLevel? LineLevel
    {
        get => _lineLevel;
        set { if (SetProperty(ref _lineLevel, value)) { Background(RefreshLinePriceAsync()); OnPropertyChanged(nameof(LineBaseUnitsText)); OnPropertyChanged(nameof(LineAvailableText)); } }
    }
    public BatchOption? LineBatch { get => _lineBatch; set { if (SetProperty(ref _lineBatch, value)) Background(RefreshLineStockAsync()); } }
    public decimal LineQuantity { get => _lineQuantity; set { if (SetProperty(ref _lineQuantity, value)) OnPropertyChanged(nameof(LineBaseUnitsText)); } }

    /// <summary>السعر المقترح من الخدمة (قابل للتعديل قبل الإضافة).</summary>
    public decimal LinePrice { get => _linePrice; set => SetProperty(ref _linePrice, value); }
    public decimal? LineAvailable { get => _lineAvailable; private set { if (SetProperty(ref _lineAvailable, value)) OnPropertyChanged(nameof(LineAvailableText)); } }
    public string LineAvailableText => LineAvailable is null ? "" :
        LineLevel is { EquivalentBaseUnits: > 1 } lv
            ? $"المتاح للبيع: {LineAvailable:N0} قطعة = {Math.Floor(LineAvailable.Value / lv.EquivalentBaseUnits):N0} {lv.LevelName}"
            : $"المتاح للبيع: {LineAvailable:N0} قطعة";
    public string LineBaseUnitsText => LineLevel is null ? "" : $"= {LineQuantity * LineLevel.EquivalentBaseUnits:N0} قطعة";

    // ---------------- المجاميع (نفس معادلات sp_Sales_PostInvoice) ----------------
    public decimal TotalPieces => Lines.Sum(l => l.BaseUnits);
    public decimal SubTotal => IsFreeSale ? 0 : Lines.Sum(l => l.LineTotal);
    public decimal TaxAmount => IsFreeSale || !TaxEnabled ? 0 : Math.Round(SubTotal * TaxRate / 100m, 2);
    public decimal LoadingAmount => IsFreeSale || !LoadingEnabled ? 0 : Math.Round(TotalPieces * LoadingRate, 2);
    public decimal Total => SubTotal + TaxAmount + LoadingAmount;
    public decimal PaidNow => IsFreeSale ? 0 : PaymentMethod.Value switch
    {
        InvoicePaymentMethod.Cash or InvoicePaymentMethod.Electronic => Total,
        InvoicePaymentMethod.Partial => AmountPaidNow,
        _ => 0
    };
    public decimal AmountDue => Total - PaidNow;

    public RelayCommand NewInvoiceCommand { get; }
    public AsyncRelayCommand AddLineCommand { get; }
    public RelayCommand RemoveLineCommand { get; }
    public AsyncRelayCommand SaveDraftCommand { get; }
    public AsyncRelayCommand PostCommand { get; }
    public RelayCommand PrintCommand { get; }

    /// <summary>آخر ملخص ترحيل (للعرض وللاختبارات).</summary>
    public SalesPostingSummary? LastPosted { get; private set; }

    private void RaiseTotals()
    {
        foreach (var n in new[] { nameof(TotalPieces), nameof(SubTotal), nameof(TaxAmount), nameof(LoadingAmount),
                                  nameof(Total), nameof(PaidNow), nameof(AmountDue) })
            OnPropertyChanged(n);
    }

    // ---------------- التحميل ----------------
    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var selectedCustomerId = Customer?.Id;
        var selectedWarehouseId = Warehouse?.Id;

        Customers.Clear();
        foreach (var c in await db.Customers.AsNoTracking().Include(c => c.ParentAgent).Where(c => c.IsActive).OrderBy(c => c.Name).ToListAsync())
            Customers.Add(c);

        _allWarehouses.Clear();
        _allWarehouses.AddRange(await db.Warehouses.AsNoTracking()
            .Where(w => w.IsActive && (w.IsSellableStock || w.WarehouseType == WarehouseType.RepVan || w.WarehouseType == WarehouseType.RawMaterial))
            .OrderBy(w => w.Name).ToListAsync());
        _allItems.Clear();
        _allItems.AddRange(await db.Items.AsNoTracking().Where(i => i.IsActive).OrderBy(i => i.ItemName).ToListAsync());
        var whIds = _allWarehouses.Select(w => w.Id).ToList();
        _stock = (await db.StockTransactions.Where(t => whIds.Contains(t.WarehouseId))
                .GroupBy(t => new { t.WarehouseId, t.ItemId }).Select(g => new { g.Key.WarehouseId, g.Key.ItemId, Qty = g.Sum(t => t.QuantityBaseUnits) })
                .ToListAsync())
            .ToDictionary(x => (x.WarehouseId, x.ItemId), x => x.Qty);
        _itemLevels = (await db.ItemPackagingLevels.AsNoTracking().Where(l => l.IsSellableUnit && l.EquivalentBaseUnits > 1)
                .Select(l => new { l.ItemId, l.LevelName }).ToListAsync())
            .GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Select(l => l.LevelName.Trim()).ToHashSet());

        var keepFilter = PackFilter;
        PackFilters.Clear();
        PackFilters.Add(AllPacks);
        foreach (var name in _itemLevels.Values.SelectMany(v => v).Distinct().OrderBy(n => n)) PackFilters.Add(name);
        _packFilter = PackFilters.Contains(keepFilter) ? keepFilter : AllPacks;
        OnPropertyChanged(nameof(PackFilter));

        var keepMode = _saleMode?.Value ?? SaleMode.Direct;
        SaleModes.Clear();
        SaleModes.Add(new Option<SaleMode>(SaleMode.Direct, "بيع مباشر"));
        if (_allWarehouses.Any(w => w.WarehouseType == WarehouseType.RepVan)) SaleModes.Add(new Option<SaleMode>(SaleMode.Rep, "بيع من سيارة مندوب"));
        if (Has(SpecialPermission.SellRawMaterials)) SaleModes.Add(new Option<SaleMode>(SaleMode.RawMaterials, "بيع مواد أولية"));
        var selectedWh = _allWarehouses.FirstOrDefault(w => w.Id == selectedWarehouseId);
        var mode = selectedWh is null ? keepMode : ModeOf(selectedWh);
        _saleMode = SaleModes.FirstOrDefault(m => m.Value == mode) ?? SaleModes[0];
        OnPropertyChanged(nameof(SaleModeOption));
        OnPropertyChanged(nameof(WarehouseHint));

        _suppressReprice = true;
        Customer = Customers.FirstOrDefault(c => c.Id == selectedCustomerId);
        _suppressReprice = false;
        ApplyModeWarehouses();
        if (selectedWh is not null) Warehouse = Warehouses.FirstOrDefault(w => w.Id == selectedWh.Id) ?? Warehouse;
        RebuildItems();
        await RefreshLoadingRateAsync();
    }

    private static SaleMode ModeOf(Data.ProjectDb.Entities.Warehouse w) => w.WarehouseType switch
    {
        WarehouseType.RepVan => SaleMode.Rep,
        WarehouseType.RawMaterial => SaleMode.RawMaterials,
        _ => SaleMode.Direct
    };

    private async Task RefreshLoadingRateAsync()
    {
        await using var db = Session.NewDb();
        LoadingRate = await new SalesService(db).GetLoadingRateAsync(InvoiceDate);
    }

    private async Task OnLineItemChangedAsync()
    {
        LevelOptions.Clear();
        BatchOptions.Clear();
        if (LineItem is null) { LineLevel = null; LineBatch = null; return; }

        await using var db = Session.NewDb();
        foreach (var l in await db.ItemPackagingLevels.AsNoTracking().Where(l => l.ItemId == LineItem.Id && l.IsSellableUnit)
                     .OrderByDescending(l => l.EquivalentBaseUnits).ToListAsync())
            LevelOptions.Add(l);

        BatchOptions.Add(new BatchOption { BatchId = null, Label = "تلقائي (الأقرب انتهاءً)" });
        if (Warehouse is not null)
        {
            var batches = await db.StockTransactions.Where(t => t.ItemId == LineItem.Id && t.WarehouseId == Warehouse.Id && t.BatchId != null)
                .GroupBy(t => new { t.BatchId, t.Batch!.BatchNumber, t.Batch.ExpiryDate })
                .Select(g => new { g.Key.BatchId, g.Key.BatchNumber, g.Key.ExpiryDate, Qty = g.Sum(t => t.QuantityBaseUnits) })
                .Where(x => x.Qty > 0).OrderBy(x => x.ExpiryDate).ToListAsync();
            foreach (var b in batches)
                BatchOptions.Add(new BatchOption { BatchId = b.BatchId, Label = $"{b.BatchNumber} — ينتهي {b.ExpiryDate:yyyy/MM/dd} — {b.Qty:N0} قطعة" });
        }
        LineBatch = BatchOptions[0];
        LineLevel = LevelOptions.FirstOrDefault();
        await RefreshLineStockAsync();
    }

    private async Task RefreshLinePriceAsync()
    {
        if (LineItem is null || LineLevel is null) { LinePrice = 0; return; }
        LinePrice = await SuggestPriceAsync(LineItem.Id, LineLevel.Id);
    }

    private async Task<decimal> SuggestPriceAsync(int itemId, int levelId)
    {
        await using var db = Session.NewDb();
        var svc = new SalesService(db);
        if (Customer is null)
        {
            var level = await db.ItemPackagingLevels.AsNoTracking().FirstAsync(l => l.Id == levelId);
            var item = await db.Items.AsNoTracking().FirstAsync(i => i.Id == itemId);
            return Math.Round(item.SalePrice * level.EquivalentBaseUnits, 2);
        }
        return await svc.GetSuggestedUnitPriceAsync(Customer.Id, itemId, levelId, UseAgentPricing && IsAgentOrSub);
    }

    private async Task RefreshLineStockAsync()
    {
        if (LineItem is null || Warehouse is null) { LineAvailable = null; return; }
        await using var db = Session.NewDb();
        LineAvailable = await new SalesService(db).GetAvailableQuantityAsync(LineItem.Id, Warehouse.Id, LineBatch?.BatchId);
    }

    /// <summary>عند تغيير العميل أو خيار تسعير الوكيل: إعادة تسعير السطور غير المعدّلة يدويًا.</summary>
    private async Task RepriceAsync()
    {
        await RefreshLinePriceAsync();
        foreach (var l in Lines.Where(l => !l.IsManualPrice).ToList())
            l.SetSuggestedPrice(await SuggestPriceAsync(l.ItemId, l.PackagingLevelId));
        RaiseTotals();
    }

    // ---------------- السطور ----------------
    private async Task AddLineAsync()
    {
        if (IsReadOnly) return;
        if (LineItem is null || LineLevel is null) { Dialogs.Error("اختر الصنف ووحدة البيع"); return; }
        if (LineQuantity <= 0) { Dialogs.Error("الكمية يجب أن تكون أكبر من صفر"); return; }
        if (LinePrice < 0) { Dialogs.Error("السعر لا يمكن أن يكون سالبًا"); return; }

        var needed = LineQuantity * LineLevel.EquivalentBaseUnits
                     + Lines.Where(l => l.ItemId == LineItem.Id && l.BatchId == LineBatch?.BatchId).Sum(l => l.BaseUnits);
        if (LineAvailable is decimal available && needed > available &&
            !Dialogs.Confirm($"الكمية المطلوبة ({needed:N0} قطعة) أكبر من المتاح ({available:N0}). سيُرفض الترحيل ما لم يتوفر الرصيد. إضافة السطر على أي حال؟"))
            return;

        var suggested = await SuggestPriceAsync(LineItem.Id, LineLevel.Id);
        var line = new InvoiceLineDraft(RaiseTotals)
        {
            ItemId = LineItem.Id, ItemCode = LineItem.ItemCode, ItemName = LineItem.ItemName,
            PackagingLevelId = LineLevel.Id, LevelName = LineLevel.LevelName, BaseUnitsPerLevel = LineLevel.EquivalentBaseUnits,
            BatchId = LineBatch?.BatchId, BatchLabel = LineBatch?.BatchId is null ? "تلقائي (الأقرب انتهاءً)" : LineBatch.Label.Split(" — ")[0],
            QuantityInLevel = LineQuantity
        };
        line.SetSuggestedPrice(LinePrice);
        line.IsManualPrice = LinePrice != suggested;
        Lines.Add(line);
        RaiseTotals();

        LineQuantity = 1;
        LineItem = null;
    }

    // ---------------- الحفظ والترحيل ----------------
    private SalesInvoiceHeaderInput BuildHeader() => new(
        Customer!.Id, Warehouse!.Id, InvoiceDate, PaymentMethod.Value,
        AmountPaidNow: IsPartial ? AmountPaidNow : 0,
        TaxEnabled: TaxEnabled, TaxRate: TaxRate, LoadingSuppliesEnabled: LoadingEnabled,
        IsAgentPricing: IsAgentOrSub ? UseAgentPricing : false,
        IsFreeSale: IsFreeSale, FreeSaleRecipient: IsFreeSale ? FreeSaleRecipient : null, Notes: Notes);

    private string? ValidateForm()
    {
        if (Customer is null) return "اختر العميل";
        if (Warehouse is null) return "اختر المخزن";
        if (Lines.Count == 0) return "أضف سطرًا واحدًا على الأقل";
        if (Lines.Any(l => l.QuantityInLevel <= 0)) return "كل السطور يجب أن تكون بكمية أكبر من صفر";
        if (Lines.Any(l => l.UnitPrice < 0)) return "لا يُسمح بسعر سالب";
        if (IsFreeSale && string.IsNullOrWhiteSpace(FreeSaleRecipient)) return "البيع المجاني يتطلب تحديد الجهة المستفيدة";
        if (IsPartial && !IsFreeSale && (AmountPaidNow <= 0 || AmountPaidNow >= Total))
            return "في الدفع الجزئي يجب أن يكون المدفوع أكبر من صفر وأقل من إجمالي الفاتورة";
        return null;
    }

    /// <summary>
    /// يحفظ الفاتورة كمسودة في قاعدة البيانات: إنشاء (أو تعديل رأس المسودة الحالية)
    /// ثم استبدال كل سطورها بسطور الشاشة. آمن للتكرار.
    /// </summary>
    public async Task<bool> SaveDraftAsync()
    {
        if (IsReadOnly) return false;
        if (!Require(InvoiceId is null ? CanAdd : CanEdit, InvoiceId is null ? "إضافة الفواتير" : "تعديل الفواتير")) return false;
        var error = ValidateForm();
        if (error is not null) { Dialogs.Error(error); return false; }

        IsBusy = true;
        try
        {
            await using var db = Session.NewDb();
            var svc = new SalesService(db);
            var header = BuildHeader();

            if (InvoiceId is null)
            {
                var (r, id) = await svc.CreateInvoiceAsync(header, Session.UserId);
                if (!r.Success) { Dialogs.Error(r.ErrorMessage!); return false; }
                InvoiceId = id;
                InvoiceNumber = await db.SalesInvoices.Where(i => i.Id == id).Select(i => i.InvoiceNumber).FirstAsync();
            }
            else
            {
                var r = await svc.UpdateDraftHeaderAsync(InvoiceId.Value, header, Session.UserId);
                if (!r.Success) { Dialogs.Error(r.ErrorMessage!); return false; }
                foreach (var oldLineId in await db.SalesInvoiceLines.Where(l => l.SalesInvoiceId == InvoiceId).Select(l => l.Id).ToListAsync())
                {
                    var dr = await svc.DeleteLineAsync(oldLineId, Session.UserId);
                    if (!dr.Success) { Dialogs.Error(dr.ErrorMessage!); return false; }
                }
            }

            foreach (var l in Lines)
            {
                var r = await svc.AddLineAsync(InvoiceId!.Value,
                    new SalesInvoiceLineInput(l.ItemId, l.PackagingLevelId, l.QuantityInLevel, l.UnitPrice, l.BatchId), Session.UserId);
                if (!r.Success) { Dialogs.Error($"{l.ItemName}: {r.ErrorMessage}"); return false; }
            }
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PostAsync()
    {
        if (!Require(CanPost, "ترحيل الفواتير")) return;
        if (IsReadOnly) { Dialogs.Error("هذه الفاتورة مرحّلة مسبقًا"); return; }

        var confirm = IsFreeSale
            ? $"ترحيل بيع مجاني إلى \"{FreeSaleRecipient}\" ({TotalPieces:N0} قطعة)؟ سيُخصم من المخزون فورًا."
            : $"ترحيل الفاتورة بإجمالي {Total:N0} د.ع؟ سيُخصم المخزون ويُنشأ القيد المحاسبي، ولا يمكن التعديل بعدها.";
        if (!Dialogs.Confirm(confirm)) return;

        if (!await SaveDraftAsync()) return;

        IsBusy = true;
        try
        {
            await using var db = Session.NewDb();
            var (r, summary) = await new SalesService(db).PostInvoiceAsync(InvoiceId!.Value, Session.UserId, IsVanSale && HandOverNow);
            if (!r.Success)
            {
                Dialogs.Error(r.ErrorMessage + "\nالفاتورة محفوظة كمسودة، ويمكن تعديلها وإعادة المحاولة.");
                return;
            }
            LastPosted = summary;
            var msg = IsFreeSale
                ? $"تم ترحيل البيع المجاني {summary!.InvoiceNumber} وخصم الكميات من المخزون."
                : $"تم ترحيل الفاتورة {summary!.InvoiceNumber}\nالإجمالي: {summary.TotalAmount:N0} د.ع\n" +
                  $"المقبوض: {summary.AmountPaidNow:N0} د.ع — المتبقي على العميل: {summary.AmountDue:N0} د.ع" +
                  (summary.HandedOverToBox > 0 ? $"\nسُلّم {summary.HandedOverToBox:N0} د.ع للصندوق فورًا" : "") +
                  (summary.HandoverError is { } he ? $"\nتعذّر التسليم الفوري: {he} — بقي النقد في محفظة المندوب" : "");
            StatusMessage = msg.Replace('\n', ' ');
            if (Dialogs.Confirm(msg + "\n\nطباعة الفاتورة الآن؟"))
            {
                InvoiceNumber = summary.InvoiceNumber;
                IsReadOnly = true;
                Dialogs.ShowReport(BuildReport());
            }
            ResetForm();
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------------- فتح فاتورة موجودة ----------------
    public async Task LoadInvoiceAsync(int invoiceId)
    {
        await using var db = Session.NewDb();
        var inv = await new SalesService(db).GetInvoiceAsync(invoiceId);
        if (inv is null) { Dialogs.Error("الفاتورة غير موجودة"); return; }

        _suppressReprice = true;
        try
        {
            InvoiceId = inv.Id;
            InvoiceNumber = inv.InvoiceNumber;
            IsReadOnly = inv.Status != DocumentStatus.Draft;
            Customer = Customers.FirstOrDefault(c => c.Id == inv.CustomerId) ?? inv.Customer;
            var invWh = _allWarehouses.FirstOrDefault(w => w.Id == inv.WarehouseId) ?? inv.Warehouse;
            if (SaleModes.FirstOrDefault(m => m.Value == ModeOf(invWh)) is { } invMode) SaleModeOption = invMode;
            Warehouse = Warehouses.FirstOrDefault(w => w.Id == inv.WarehouseId) ?? inv.Warehouse;
            InvoiceDate = inv.InvoiceDate;
            PaymentMethod = PaymentOptions.First(o => o.Value == inv.PaymentMethod);
            AmountPaidNow = inv.AmountPaidNow;
            TaxEnabled = inv.TaxEnabled;
            TaxRate = inv.TaxRate;
            LoadingEnabled = inv.LoadingSuppliesEnabled;
            UseAgentPricing = inv.IsAgentPricing || !IsAgentOrSub;
            IsFreeSale = inv.IsFreeSale;
            FreeSaleRecipient = inv.FreeSaleRecipient;
            Notes = inv.Notes;

            Lines.Clear();
            foreach (var l in inv.Lines.OrderBy(l => l.Id))
            {
                var draft = new InvoiceLineDraft(RaiseTotals)
                {
                    ItemId = l.ItemId, ItemCode = l.Item.ItemCode, ItemName = l.Item.ItemName,
                    PackagingLevelId = l.PackagingLevelId, LevelName = l.PackagingLevel.LevelName,
                    BaseUnitsPerLevel = l.PackagingLevel.EquivalentBaseUnits, BatchId = l.BatchId,
                    BatchLabel = l.Batch?.BatchNumber ?? "تلقائي (الأقرب انتهاءً)", QuantityInLevel = l.QuantityInLevel
                };
                draft.SetSuggestedPrice(l.UnitPrice);
                draft.IsManualPrice = true;   // سعر محفوظ: لا يُعاد تسعيره تلقائيًا
                Lines.Add(draft);
            }
        }
        finally
        {
            _suppressReprice = false;
        }
        RaiseTotals();
        StatusMessage = IsReadOnly ? $"عرض الفاتورة المرحّلة {InvoiceNumber} (للقراءة فقط)" : $"تعديل المسودة {InvoiceNumber}";
    }

    /// <summary>الفاتورة كما تظهر على الشاشة، بصيغة قابلة للطباعة.</summary>
    public ReportDocument BuildReport()
    {
        var r = new ReportDocument
        {
            CompanyName = Session.ProjectName,
            Title = IsFreeSale ? "إذن صرف — بيع مجاني" : "فاتورة مبيعات",
            Stamp = IsReadOnly ? null : "مسودة — غير مرحّلة",
            Notes = Notes,
            PrintedBy = Session.FullName,
            Key = "SalesInvoice", ReceiptCapable = true,
            ReceiptColumns = IsFreeSale ? new[] { 1, 2, 3 } : new[] { 1, 3, 5, 6 }   // الصنف، الكمية، السعر، المبلغ
        };
        r.Field("رقم الفاتورة", InvoiceId is null ? "—" : InvoiceNumber)
         .Field("التاريخ", InvoiceDate.ToString("yyyy/MM/dd"))
         .Field(IsFreeSale ? "الجهة المستفيدة" : "العميل", IsFreeSale ? FreeSaleRecipient : Customer?.Name)
         .Field("نوع العميل", Customer is null || IsFreeSale ? null : ArabicLabels.Of(Customer.CustomerType))
         .Field("طريقة الدفع", IsFreeSale ? null : PaymentMethod.Label)
         .Field("المخزن", Warehouse?.Name);

        r.Columns.AddRange(IsFreeSale
            ? new[] { "#", "الصنف", "الوحدة", "الكمية", "القطع" }
            : new[] { "#", "الصنف", "الوحدة", "الكمية", "القطع", "السعر", "المبلغ" });
        var i = 0;
        foreach (var l in Lines)
        {
            var row = new List<string> { (++i).ToString(), $"{l.ItemName} ({l.ItemCode})", l.LevelName, $"{l.QuantityInLevel:N0}", $"{l.BaseUnits:N0}" };
            if (!IsFreeSale) row.AddRange(new[] { $"{l.UnitPrice:N0}", $"{l.LineTotal:N0}" });
            r.Rows.Add(row);
        }

        r.Total("إجمالي القطع", $"{TotalPieces:N0}");
        if (!IsFreeSale)
        {
            r.Total("المجموع", $"{SubTotal:N0} د.ع");
            if (TaxAmount != 0) r.Total($"الضريبة ({TaxRate:0.##}%)", $"{TaxAmount:N0} د.ع");
            if (LoadingAmount != 0) r.Total("رسوم التحميل", $"{LoadingAmount:N0} د.ع");
            r.Total("الإجمالي", $"{Total:N0} د.ع", emphasis: true);
            r.Total("المدفوع", $"{PaidNow:N0} د.ع");
            r.Total("المتبقي على العميل", $"{AmountDue:N0} د.ع", emphasis: AmountDue > 0);
        }
        r.Signatures.AddRange(new[] { "المستلم", "أمين المخزن", "المحاسب" });
        return r;
    }

    private bool ConfirmDiscard() =>
        IsReadOnly || Lines.Count == 0 || Dialogs.Confirm("سيتم تفريغ الشاشة. أي تعديلات غير محفوظة ستُفقد. متابعة؟");

    public void ResetForm()
    {
        _suppressReprice = true;
        InvoiceId = null;
        InvoiceNumber = "فاتورة جديدة";
        IsReadOnly = false;
        Customer = null;
        InvoiceDate = DateTime.Today;
        PaymentMethod = PaymentOptions[0];
        AmountPaidNow = 0;
        TaxEnabled = false;
        TaxRate = 14;
        LoadingEnabled = false;
        UseAgentPricing = true;
        IsFreeSale = false;
        FreeSaleRecipient = null;
        Notes = null;
        Lines.Clear();
        LineItem = null;
        LineQuantity = 1;
        _suppressReprice = false;
        RaiseTotals();
    }
}

// ================================================================
//                         قائمة الفواتير
// ================================================================
public class SalesInvoiceListSectionViewModel : SectionViewModel
{
    private readonly Func<int, Task> _open;
    private DateTime? _fromDate = DateTime.Today.AddDays(-30);
    private DateTime? _toDate;
    private Customer? _customerFilter;

    public SalesInvoiceListSectionViewModel(AppSession s, IDialogService d, Func<int, Task> open)
        : base(s, d, ModuleCode.Sales, "قائمة الفواتير", Icons.List, "#6366F1", "البحث في الفواتير وفتح المسودات أو حذفها")
    {
        _open = open;
        OpenCommand = new AsyncRelayCommand(p => p is SalesInvoiceListRow r ? _open(r.Id) : Task.CompletedTask);
        DeleteCommand = new AsyncRelayCommand(p => p is SalesInvoiceListRow r ? DeleteAsync(r) : Task.CompletedTask);
        PrintCommand = new AsyncRelayCommand(p => p is SalesInvoiceListRow r ? PrintAsync(db => DocumentReports.SalesInvoiceAsync(Session, db, r.Id)) : Task.CompletedTask);
        VoidCommand = new AsyncRelayCommand(p => p is SalesInvoiceListRow r ? VoidAsync(r) : Task.CompletedTask);
        ClearFiltersCommand = new AsyncRelayCommand(async () => { _fromDate = null; _toDate = null; _customerFilter = null; RaiseFilters(); await LoadAsync(); });
    }

    protected override bool ReloadOnActivate => true;

    public ObservableCollection<SalesInvoiceListRow> Rows { get; } = new();
    public ObservableCollection<Customer> Customers { get; } = new();
    public DateTime? FromDate { get => _fromDate; set { if (SetProperty(ref _fromDate, value)) Background(LoadAsync()); } }
    public DateTime? ToDate { get => _toDate; set { if (SetProperty(ref _toDate, value)) Background(LoadAsync()); } }
    public Customer? CustomerFilter { get => _customerFilter; set { if (SetProperty(ref _customerFilter, value)) Background(LoadAsync()); } }

    public decimal PostedTotal => Rows.Where(r => r.Status == "Posted").Sum(r => r.TotalAmount);
    public int DraftCount => Rows.Count(r => r.Status == "Draft");

    public AsyncRelayCommand OpenCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand VoidCommand { get; }
    public AsyncRelayCommand ClearFiltersCommand { get; }

    private string? _voidReason;
    /// <summary>سبب إلغاء الفاتورة المرحّلة (إلزامي).</summary>
    public string? VoidReason { get => _voidReason; set => SetProperty(ref _voidReason, value); }
    public bool CanVoid => CanDelete || Has(SpecialPermission.VoidPosted);
    public AsyncRelayCommand PrintCommand { get; }

    private void RaiseFilters()
    {
        OnPropertyChanged(nameof(FromDate));
        OnPropertyChanged(nameof(ToDate));
        OnPropertyChanged(nameof(CustomerFilter));
    }

    public override async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            await using var db = Session.NewDb();
            if (Customers.Count == 0)
                foreach (var c in await db.Customers.AsNoTracking().OrderBy(c => c.Name).ToListAsync()) Customers.Add(c);
            var rows = await new SalesService(db).GetInvoiceListAsync(FromDate, ToDate, CustomerFilter?.Id);
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
            OnPropertyChanged(nameof(PostedTotal));
            OnPropertyChanged(nameof(DraftCount));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteAsync(SalesInvoiceListRow row)
    {
        if (row.Status != "Draft") { Dialogs.Error("الفاتورة المرحّلة لا تُحذف — تُلغى بزر الإلغاء (قيد عكسي) مع كتابة السبب"); return; }
        if (!Require(CanDelete, "الحذف")) return;
        if (!Dialogs.Confirm($"حذف المسودة {row.InvoiceNumber}؟")) return;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new SalesService(db).DeleteDraftInvoiceAsync(row.Id, Session.UserId), $"تم حذف {row.InvoiceNumber}"))
            await LoadAsync();
    }

    private async Task VoidAsync(SalesInvoiceListRow row)
    {
        if (row.Status == "Draft") { Dialogs.Error("المسودة تُحذف ولا تُلغى"); return; }
        if (row.Status == "Voided") { Dialogs.Error("الفاتورة ملغاة مسبقًا"); return; }
        if (!Require(CanVoid, "إلغاء الفواتير المرحّلة")) return;
        if (string.IsNullOrWhiteSpace(VoidReason)) { Dialogs.Error("اكتب سبب الإلغاء في الخانة أعلى القائمة أولًا"); return; }
        if (!Dialogs.Confirm($"إلغاء الفاتورة {row.InvoiceNumber} ({row.TotalAmount:N0} د.ع)؟ تعود البضاعة للمخزن، ويُنشأ قيد عكسي، وتُلغى حركة الصندوق، وتبقى الفاتورة ظاهرة بحالة ملغاة.")) return;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new SalesService(db).VoidInvoiceAsync(row.Id, VoidReason!, Session.UserId), $"أُلغيت الفاتورة {row.InvoiceNumber}"))
        {
            VoidReason = null;
            await LoadAsync();
        }
    }
}

// ================================================================
//                         كشف حساب العميل
// ================================================================
public class CustomerStatementSectionViewModel : SectionViewModel
{
    private Customer? _customer;

    public CustomerStatementSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Sales, "كشف حساب العميل", Icons.Statement, "#8B5CF6", "الفواتير والمدفوعات والرصيد التراكمي لكل عميل")
    {
        OpenCustomerCommand = new RelayCommand(p => { if (p is CustomerBalanceRow b) Customer = Customers.FirstOrDefault(c => c.Id == b.CustomerId); });
        PrintCommand = new RelayCommand(() =>
        {
            if (Customer is null) { Dialogs.Error("اختر العميل أولًا"); return; }
            Dialogs.ShowReport(BuildReport());
        });
        PrintInvoicesCommand = new RelayCommand(() =>
        {
            if (Customer is null) { Dialogs.Error("اختر العميل أولًا"); return; }
            Dialogs.ShowReport(BuildInvoicesReport());
        });
        AllocateCommand = new AsyncRelayCommand(AllocateAsync);
        ResetAutoCommand = new AsyncRelayCommand(p => p is CustomerPaymentRow r ? ResetAutoAsync(r) : Task.CompletedTask);
    }

    // ---------------- الفواتير والمدفوعات والتوزيع ----------------
    private CustomerPaymentRow? _allocVoucher;
    private CustomerInvoiceRow? _allocInvoice;
    private decimal _allocAmount;
    public ObservableCollection<CustomerInvoiceRow> Invoices { get; } = new();
    public ObservableCollection<CustomerPaymentRow> Payments { get; } = new();
    public CustomerPaymentRow? AllocVoucher { get => _allocVoucher; set => SetProperty(ref _allocVoucher, value); }
    public CustomerInvoiceRow? AllocInvoice { get => _allocInvoice; set { if (SetProperty(ref _allocInvoice, value) && value is not null && AllocAmount == 0) AllocAmount = value.Remaining; } }
    public decimal AllocAmount { get => _allocAmount; set => SetProperty(ref _allocAmount, value); }
    public RelayCommand PrintInvoicesCommand { get; }
    public AsyncRelayCommand AllocateCommand { get; }
    public AsyncRelayCommand ResetAutoCommand { get; }
    public decimal OpenInvoicesTotal => Invoices.Sum(i => i.Remaining);
    public decimal UnallocatedCredit => Payments.Sum(p => p.Unallocated);
    public string DebtHeadline => Customer is null ? "" :
        $"إجمالي الدين: {Math.Max(0, Balance):N0} د.ع — فواتير مفتوحة: {Invoices.Count(i => i.Remaining > 0)}" +
        (UnallocatedCredit > 0 ? $" — رصيد دفعات غير موزع: {UnallocatedCredit:N0} د.ع" : "");

    private async Task AllocateAsync()
    {
        if (!Require(CanEdit, "التوزيع اليدوي للدفعات")) return;
        if (AllocVoucher is null || AllocInvoice is null) { Dialogs.Error("اختر سند القبض والفاتورة"); return; }
        await using var db = Session.NewDb();
        var (voucher, invoice, amount) = (AllocVoucher, AllocInvoice, AllocAmount);
        if (await RunOperationAsync(() => new CustomerAccountService(db).AllocateManualAsync(voucher.VoucherId, invoice.InvoiceId, amount, Session.UserId),
                                    $"وُزّع {amount:N0} من {voucher.VoucherNumber} على {invoice.InvoiceNumber} يدويًا"))
        {
            AllocAmount = 0;
            await LoadStatementAsync();
        }
    }

    private async Task ResetAutoAsync(CustomerPaymentRow row)
    {
        if (!Require(CanEdit, "التوزيع اليدوي للدفعات")) return;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new CustomerAccountService(db).ResetToAutomaticAsync(row.VoucherId), $"أُعيد توزيع {row.VoucherNumber} تلقائيًا (الأقدم أولًا)"))
            await LoadStatementAsync();
    }

    public ReportDocument BuildInvoicesReport()
    {
        var r = new ReportDocument { Key = "customer-invoices", CompanyName = Session.ProjectName, Title = "كشف فواتير عميل (المدفوع والمتبقي)", PrintedBy = Session.FullName };
        r.Field("العميل", Customer?.Name).Field("الوكيل", Customer?.ParentAgent?.Name);
        r.Columns.AddRange(new[] { "رقم الفاتورة", "التاريخ", "القيمة", "المدفوع", "المتبقي", "الحالة" });
        foreach (var i in Invoices)
            r.Rows.Add(new[] { i.InvoiceNumber, i.InvoiceDate.ToString("yyyy/MM/dd"), $"{i.Total:N0}", $"{i.Paid:N0}", $"{i.Remaining:N0}", i.StatusText });
        r.Total("إجمالي الفواتير", $"{Invoices.Sum(i => i.Total):N0} د.ع")
         .Total("إجمالي المدفوع", $"{Invoices.Sum(i => i.Paid):N0} د.ع")
         .Total("إجمالي الدين", $"{Math.Max(0, Balance):N0} د.ع", true);
        r.Signatures.AddRange(new[] { "توقيع العميل", "المحاسب" });
        return r;
    }

    public RelayCommand PrintCommand { get; }

    public ReportDocument BuildReport()
    {
        var r = new ReportDocument { CompanyName = Session.ProjectName, Title = "كشف حساب عميل", PrintedBy = Session.FullName };
        r.Field("العميل", Customer?.Name)
         .Field("نوع العميل", Customer is null ? null : ArabicLabels.Of(Customer.CustomerType))
         .Field("الوكيل", Customer?.ParentAgent?.Name)
         .Field("رصيد التأمين (منفصل عن الدين)", DepositBalance > 0 ? $"{DepositBalance:N0} د.ع" : null)
         .Field("الفترة", Rows.Count == 0 ? "لا توجد حركات" : $"{Rows[0].TxDate:yyyy/MM/dd} — {Rows[^1].TxDate:yyyy/MM/dd}");
        r.Columns.AddRange(new[] { "التاريخ", "النوع", "رقم المستند", "البيان", "مدين", "دائن", "الرصيد" });
        foreach (var x in Rows)
            r.Rows.Add(new[] { x.TxDate.ToString("yyyy/MM/dd"), x.TxType, x.DocNumber, x.Description,
                               x.Debit == 0 ? "" : $"{x.Debit:N0}", x.Credit == 0 ? "" : $"{x.Credit:N0}", $"{x.RunningBalance:N0}" });
        r.Total("مجموع المدين", $"{TotalDebit:N0} د.ع")
         .Total("مجموع الدائن", $"{TotalCredit:N0} د.ع")
         .Total("الرصيد", BalanceText, emphasis: true);
        r.Signatures.AddRange(new[] { "توقيع العميل", "المحاسب" });
        return r;
    }

    protected override bool ReloadOnActivate => true;

    public ObservableCollection<Customer> Customers { get; } = new();
    public ObservableCollection<CustomerStatementRow> Rows { get; } = new();
    public ObservableCollection<CustomerBalanceRow> Balances { get; } = new();
    public RelayCommand OpenCustomerCommand { get; }

    public Customer? Customer
    {
        get => _customer;
        set { if (SetProperty(ref _customer, value)) { OnPropertyChanged(nameof(CustomerInfo)); Background(LoadStatementAsync()); } }
    }

    public string CustomerInfo => Customer is null ? "اختر عميلًا من القائمة أو من جدول الأرصدة" :
        $"{Customer.Name} — {ArabicLabels.Of(Customer.CustomerType)}" +
        (Customer.ParentAgent is null ? "" : $" (تابع للوكيل {Customer.ParentAgent.Name})") +
        (DepositBalance > 0 ? $" — تأمين قائم: {DepositBalance:N0} د.ع (أمانة منفصلة عن الدين)" : "");

    private decimal _depositBalance;
    /// <summary>رصيد تأمينات العميل — يُعرض للعلم فقط ولا يدخل في رصيد الدين.</summary>
    public decimal DepositBalance { get => _depositBalance; private set { if (SetProperty(ref _depositBalance, value)) OnPropertyChanged(nameof(CustomerInfo)); } }

    public decimal TotalDebit => Rows.Sum(r => r.Debit);
    public decimal TotalCredit => Rows.Sum(r => r.Credit);
    public decimal Balance => Rows.LastOrDefault()?.RunningBalance ?? 0;
    public string BalanceText => Balance switch
    {
        > 0 => $"الرصيد المستحق على العميل: {Balance:N0} د.ع",
        < 0 => $"رصيد دائن للعميل: {-Balance:N0} د.ع",
        _ => "الحساب متوازن (لا رصيد)"
    };

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var selectedId = Customer?.Id;
        Customers.Clear();
        foreach (var c in await db.Customers.AsNoTracking().Include(c => c.ParentAgent).OrderBy(c => c.Name).ToListAsync()) Customers.Add(c);
        Balances.Clear();
        foreach (var b in await new SalesService(db).GetCustomerBalancesAsync()) Balances.Add(b);

        _customer = Customers.FirstOrDefault(c => c.Id == selectedId);
        OnPropertyChanged(nameof(Customer));
        OnPropertyChanged(nameof(CustomerInfo));
        await LoadStatementAsync();
    }

    public async Task LoadStatementAsync()
    {
        Rows.Clear();
        Invoices.Clear();
        Payments.Clear();
        DepositBalance = 0;
        if (Customer is not null)
        {
            await using var db = Session.NewDb();
            var account = new CustomerAccountService(db);
            DepositBalance = await new CustomerDepositService(db).GetBalanceAsync(Customer.Id);
            await account.SyncAsync(Customer.Id);   // التوزيع الأقدم أولًا محدَّث دائمًا قبل العرض
            foreach (var r in await new SalesService(db).GetCustomerStatementAsync(Customer.Id))
            {
                r.TxType = ArabicLabels.Of(r.TxType);
                Rows.Add(r);
            }
            foreach (var i in await account.GetInvoicesAsync(Customer.Id)) Invoices.Add(i);
            foreach (var p in await account.GetPaymentsAsync(Customer.Id)) Payments.Add(p);
        }
        var voucherId = AllocVoucher?.VoucherId;
        var invoiceId = AllocInvoice?.InvoiceId;
        _allocVoucher = Payments.FirstOrDefault(p => p.VoucherId == voucherId);
        _allocInvoice = Invoices.FirstOrDefault(i => i.InvoiceId == invoiceId);
        OnPropertyChanged(nameof(AllocVoucher));
        OnPropertyChanged(nameof(AllocInvoice));
        OnPropertyChanged(nameof(OpenInvoicesTotal));
        OnPropertyChanged(nameof(UnallocatedCredit));
        OnPropertyChanged(nameof(DebtHeadline));
        OnPropertyChanged(nameof(TotalDebit));
        OnPropertyChanged(nameof(TotalCredit));
        OnPropertyChanged(nameof(Balance));
        OnPropertyChanged(nameof(BalanceText));
    }
}

// ================================================================
//                العملاء / أسعار الوكلاء / رسوم التحميل
// ================================================================
public class CustomersSectionViewModel : CrudSectionViewModel<Customer>
{
    public CustomersSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Sales, "العملاء", Icons.People, "#0EA5E9", "الوكلاء والعملاء الفرعيون والمباشرون") { }

    public IReadOnlyList<Option<CustomerType>> TypeOptions { get; } = ArabicLabels.OptionsOf<CustomerType>();
    public ObservableCollection<Customer> Agents { get; } = new();

    protected override int GetId(Customer e) => e.Id;
    protected override string Describe(Customer e) => e.Name;
    protected override bool Matches(Customer e, string t) =>
        base.Matches(e, t) || (e.Phone?.Contains(t) ?? false) || (e.Province?.Contains(t) ?? false);

    protected override async Task LoadLookupsAsync(ProjectDbContext db)
    {
        Agents.Clear();
        foreach (var a in await db.Customers.AsNoTracking().Where(c => c.CustomerType == CustomerType.Agent && c.IsActive).OrderBy(c => c.Name).ToListAsync())
            Agents.Add(a);
    }

    protected override Task<List<Customer>> QueryAsync(ProjectDbContext db) =>
        db.Customers.AsNoTracking().Include(c => c.ParentAgent).OrderBy(c => c.Name).ToListAsync();

    protected override Customer CreateNew() => new() { CustomerType = CustomerType.Direct, Province = "البصرة" };

    protected override string? Validate(Customer e)
    {
        if (string.IsNullOrWhiteSpace(e.Name)) return "أدخل اسم العميل";
        if (e.CustomerType == CustomerType.SubCustomer && e.ParentAgentId is null) return "العميل الفرعي يجب ربطه بوكيل";
        if (e.CustomerType == CustomerType.SubCustomer && e.ParentAgentId == e.Id) return "العميل لا يمكن أن يكون وكيلًا لنفسه";
        return null;
    }

    protected override async Task BeforeSaveAsync(ProjectDbContext db, Customer e)
    {
        e.Name = e.Name.Trim();
        if (e.CustomerType != CustomerType.SubCustomer) e.ParentAgentId = null;
        // تحويل وكيل له عملاء فرعيون إلى نوع آخر يكسر التسعير الهرمي
        if (e.Id != 0 && e.CustomerType != CustomerType.Agent &&
            await db.Customers.AnyAsync(c => c.ParentAgentId == e.Id))
            throw new DbUpdateException("لا يمكن تغيير نوع هذا الوكيل لأن له عملاء فرعيين؛ انقلهم لوكيل آخر أولًا.");
    }
}

public class AgentPricesSectionViewModel : CrudSectionViewModel<AgentItemPrice>
{
    public AgentPricesSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Sales, "أسعار الوكلاء", Icons.Price, "#F59E0B", "سعر يدوي خاص لكل وكيل وصنف (بالقطعة)") { }

    public ObservableCollection<Customer> Agents { get; } = new();
    public ObservableCollection<Item> ItemsLookup { get; } = new();

    protected override int GetId(AgentItemPrice e) => e.Id;
    protected override string Describe(AgentItemPrice e) => $"{e.Customer?.Name} — {e.Item?.ItemName}";

    protected override async Task LoadLookupsAsync(ProjectDbContext db)
    {
        Agents.Clear();
        foreach (var a in await db.Customers.AsNoTracking().Where(c => c.CustomerType == CustomerType.Agent && c.IsActive).OrderBy(c => c.Name).ToListAsync())
            Agents.Add(a);
        ItemsLookup.Clear();
        foreach (var i in await db.Items.AsNoTracking().Where(i => i.IsActive).OrderBy(i => i.ItemName).ToListAsync()) ItemsLookup.Add(i);
    }

    protected override Task<List<AgentItemPrice>> QueryAsync(ProjectDbContext db) =>
        db.AgentItemPrices.AsNoTracking().Include(p => p.Customer).Include(p => p.Item)
          .OrderBy(p => p.Customer.Name).ThenBy(p => p.Item.ItemName).ToListAsync();

    protected override AgentItemPrice CreateNew() => new() { CustomerId = Agents.FirstOrDefault()?.Id ?? 0 };

    protected override string? Validate(AgentItemPrice e)
    {
        if (e.CustomerId == 0) return "اختر الوكيل";
        if (e.ItemId == 0) return "اختر الصنف";
        if (e.AgentPrice < 0) return "السعر لا يمكن أن يكون سالبًا";
        return null;
    }
}

public class LoadingSettingsSectionViewModel : CrudSectionViewModel<LoadingSuppliesSetting>
{
    public LoadingSettingsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Sales, "رسوم التحميل", Icons.Truck, "#F97316", "سعر مستلزمات التحميل للقطعة بتاريخ سريان") { }

    protected override int GetId(LoadingSuppliesSetting e) => e.Id;
    protected override string Describe(LoadingSuppliesSetting e) => $"{e.RatePerPiece:N2} د.ع للقطعة من {e.EffectiveDate:yyyy/MM/dd}";
    protected override Task<List<LoadingSuppliesSetting>> QueryAsync(ProjectDbContext db) =>
        db.LoadingSuppliesSettings.AsNoTracking().OrderByDescending(x => x.EffectiveDate).ToListAsync();
    protected override LoadingSuppliesSetting CreateNew() => new() { EffectiveDate = DateTime.Today };
    protected override string? Validate(LoadingSuppliesSetting e) => e.RatePerPiece < 0 ? "السعر لا يمكن أن يكون سالبًا" : null;

    /// <summary>السعر الساري اليوم (أحدث تاريخ سريان لا يتجاوز اليوم).</summary>
    public string CurrentRateText
    {
        get
        {
            var current = Items.Where(x => x.EffectiveDate <= DateTime.Today).OrderByDescending(x => x.EffectiveDate).FirstOrDefault();
            return current is null ? "لا يوجد سعر ساري — رسوم التحميل ستُحسب صفرًا" : $"السعر الساري اليوم: {current.RatePerPiece:N2} د.ع للقطعة";
        }
    }

    public override async Task LoadAsync()
    {
        await base.LoadAsync();
        OnPropertyChanged(nameof(CurrentRateText));
    }
}
