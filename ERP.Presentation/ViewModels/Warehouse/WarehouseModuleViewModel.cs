using System.Collections.ObjectModel;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Warehouse;

public class WarehouseModuleViewModel : ModuleViewModel
{
    /// <summary>إظهار شاشة التسوية القديمة في الوحدة (true يعيدها كما كانت).</summary>
    public static bool ShowLegacyAdjustment { get; set; } = false;

    /// <summary>الشاشة القديمة ما زالت تعمل (للاختبارات ولإعادة إظهارها)، لكنها خارج التبويبات.</summary>
    public StockAdjustmentSectionViewModel LegacyAdjustment { get; }
    public StocktakeSectionViewModel Stocktake { get; }

    private readonly AppSession _session;
    private readonly IDialogService _dialogs;

    public WarehouseModuleViewModel(AppSession s, IDialogService d)
        : base("المخازن", Icons.Warehouse, ModuleColors.Warehouse)
    {
        UseDashboard(s, d, ModuleCode.Warehouse, ModuleDashboardViewModel.Warehouse);
        _session = s;
        _dialogs = d;
        Add(new ItemsSectionViewModel(s, d));
        Add(new WarehousesSectionViewModel(s, d, RefreshWorkspacesAsync));
        Add(new PackagingSectionViewModel(s, d));
        Add(new LocationsSectionViewModel(s, d));
        Add(new CurrentStockSectionViewModel(s, d));
        Stocktake = Add(new StocktakeSectionViewModel(s, d));
        Add(new LossesSectionViewModel(s, d));
        Add(new BeneficiariesSectionViewModel(s, d));
        // شاشة "تسوية المخزون" القديمة مخفية مؤقتًا (حلّت محلها واجهات المخازن). لا تُحذف قبل التأكد من الشاشات الجديدة.
        LegacyAdjustment = new StockAdjustmentSectionViewModel(s, d);
        if (ShowLegacyAdjustment) Add(LegacyAdjustment);
        Add(new ManufacturingRequirementSectionViewModel(s, d));
        Add(new BomSectionViewModel(s, d));
        Add(new PackagingTemplatesSectionViewModel(s, d));
        Add(new StockAlertsSectionViewModel(s, d));
        DamagedSales = Add(new DamagedSalesSectionViewModel(s, d));
        Background(RefreshWorkspacesAsync());
    }

    public DamagedSalesSectionViewModel DamagedSales { get; }

    /// <summary>واجهة مستقلة لكل مخزن فعّال (مواد أولية، منتج تام، كاش فان، تالف، وأي مخزن جديد).</summary>
    public IReadOnlyList<WarehouseWorkspaceSectionViewModel> Workspaces => Tabs.OfType<WarehouseWorkspaceSectionViewModel>().ToList();

    public WarehouseWorkspaceSectionViewModel Workspace(int warehouseId) => Workspaces.Single(w => w.WarehouseId == warehouseId);

    /// <summary>يضيف تبويبًا لكل مخزن جديد ويزيل تبويب المخزن الموقوف أو المحذوف (وتغيير الاسم/النوع يعيد بناء تبويبه).</summary>
    public async Task RefreshWorkspacesAsync()
    {
        await using var db = _session.NewDb();
        var warehouses = (await db.Warehouses.AsNoTracking().Where(w => w.IsActive && w.WarehouseType != WarehouseType.WorkInProcess).OrderBy(w => w.Name).ToListAsync())
            // ترتيب ثابت: المنتج التام، المواد الأولية، ثم بقية المخازن، والكاش فان أخيرًا
            .OrderBy(w => w.WarehouseType switch { WarehouseType.FinishedGoods => 0, WarehouseType.RawMaterial => 1, WarehouseType.RepVan => 3, _ => 2 })
            .ThenBy(w => w.Name).ToList();

        foreach (var ws in Workspaces)
        {
            var w = warehouses.FirstOrDefault(x => x.Id == ws.WarehouseId);
            if (w is null || w.Name != ws.Title || w.WarehouseType != ws.WarehouseType) RemoveSection(ws);
        }
        var index = 1;
        foreach (var w in warehouses)
        {
            var existing = Workspaces.FirstOrDefault(x => x.WarehouseId == w.Id);
            if (existing is null) InsertSection(index, new WarehouseWorkspaceSectionViewModel(_session, _dialogs, w));
            else if (Tabs.IndexOf(existing) != index) { Tabs.Remove(existing); InsertSection(index, existing); }
            index++;
        }
    }
}

// ============================ الأصناف ============================
public class ItemsSectionViewModel : CrudSectionViewModel<Item>
{
    public ItemsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Warehouse, "الأصناف", Icons.Item, "#0EA5E9", "الأصناف والباركود وسعر البيع وحد التنبيه") { }

    public IReadOnlyList<Option<SourcingMethod>> SourcingOptions { get; } = ArabicLabels.OptionsOf<SourcingMethod>();
    /// <summary>سعر الكلفة معلومة حساسة: يظهر فقط لمن يملك صلاحية "رؤية الكلفة والأرباح".</summary>
    public bool CanSeeCost => Has(SpecialPermission.CostAndProfit);

    protected override int GetId(Item e) => e.Id;
    protected override string Describe(Item e) => $"{e.ItemCode} — {e.ItemName}";
    protected override bool Matches(Item e, string t) => base.Matches(e, t) || (e.BarCode?.Contains(t) ?? false);
    protected override Task<List<Item>> QueryAsync(ProjectDbContext db) =>
        db.Items.AsNoTracking().OrderBy(i => i.ItemCode).ToListAsync();

    protected override string? Validate(Item e)
    {
        if (string.IsNullOrWhiteSpace(e.ItemCode)) return "أدخل رمز الصنف";
        if (string.IsNullOrWhiteSpace(e.ItemName)) return "أدخل اسم الصنف";
        if (e.SalePrice < 0) return "سعر البيع لا يمكن أن يكون سالبًا";
        if (e.MinStockAlertLevel < 0) return "حد التنبيه لا يمكن أن يكون سالبًا";
        if (e.UnitWeightGrams < 0) return "وزن القطعة لا يمكن أن يكون سالبًا";
        if (e.LeadTimeDays < 0) return "مدة التجهيز لا يمكن أن تكون سالبة";
        return null;
    }

    protected override async Task BeforeSaveAsync(ProjectDbContext db, Item e)
    {
        e.ItemCode = e.ItemCode.Trim();
        e.ItemName = e.ItemName.Trim();
        e.BarCode = string.IsNullOrWhiteSpace(e.BarCode) ? null : e.BarCode.Trim();
        // متوسط الكلفة يحسبه محرك الكلفة مع كل وارد: تعديل بيانات الصنف لا يمسّه (يُدخل يدويًا للصنف الجديد فقط)
        if (e.Id != 0)
            e.CostPrice = await db.Items.Where(i => i.Id == e.Id).Select(i => i.CostPrice).FirstOrDefaultAsync();

        // كل صنف جديد يحصل تلقائيًا على مستوى التعبئة الأساسي (القطعة) حتى يمكن بيعه فورًا
        if (e.Id == 0)
            db.ItemPackagingLevels.Add(new ItemPackagingLevel { Item = e, LevelName = e.BaseUnitName, ContainsQuantity = 1, EquivalentBaseUnits = 1 });
        await Task.CompletedTask;
    }
}

// ============================ المخازن ============================
public class WarehousesSectionViewModel : CrudSectionViewModel<Data.ProjectDb.Entities.Warehouse>
{
    private readonly Func<Task>? _changed;

    public WarehousesSectionViewModel(AppSession s, IDialogService d, Func<Task>? changed = null)
        : base(s, d, ModuleCode.Warehouse, "تعريف المخازن", Icons.Store, "#6366F1", "إضافة المخازن الرئيسية والفرعية والكاش فان — لكل مخزن واجهة خاصة")
    {
        _changed = changed;
    }

    /// <summary>كل مخزن جديد يظهر فورًا كتبويب مستقل في وحدة المخازن.</summary>
    protected override async Task<string?> AfterSaveAsync(Data.ProjectDb.Entities.Warehouse entity, bool isNew)
    {
        if (_changed is not null) await _changed();
        return null;
    }

    // مخزن "تحت التصنيع" يُنشأ تلقائيًا مع كل ماكينة ولا يُضاف يدويًا
    public IReadOnlyList<Option<WarehouseType>> TypeOptions { get; } =
        ArabicLabels.OptionsOf<WarehouseType>().Where(o => o.Value != WarehouseType.WorkInProcess).ToList();
    public ObservableCollection<Branch> Branches { get; } = new();
    public ObservableCollection<Employee> Employees { get; } = new();

    protected override int GetId(Data.ProjectDb.Entities.Warehouse e) => e.Id;
    protected override string Describe(Data.ProjectDb.Entities.Warehouse e) => e.Name;
    protected override Task<List<Data.ProjectDb.Entities.Warehouse>> QueryAsync(ProjectDbContext db) =>
        db.Warehouses.AsNoTracking().Include(w => w.Branch).Include(w => w.OwnerEmployee).Where(w => w.WarehouseType != WarehouseType.WorkInProcess).OrderBy(w => w.Name).ToListAsync();

    protected override async Task LoadLookupsAsync(ProjectDbContext db)
    {
        Branches.Clear();
        foreach (var b in await db.Branches.AsNoTracking().Where(b => b.IsActive).OrderBy(b => b.Name).ToListAsync()) Branches.Add(b);
        Employees.Clear();
        foreach (var e in await db.Employees.AsNoTracking().Where(e => e.IsActive).OrderBy(e => e.FullName).ToListAsync()) Employees.Add(e);
    }

    protected override Data.ProjectDb.Entities.Warehouse CreateNew() =>
        new() { BranchId = Branches.FirstOrDefault()?.Id ?? 0, WarehouseType = WarehouseType.Main, IsSellableStock = true };

    protected override string? Validate(Data.ProjectDb.Entities.Warehouse e)
    {
        if (string.IsNullOrWhiteSpace(e.Name)) return "أدخل اسم المخزن";
        if (e.BranchId == 0) return "اختر الفرع (أضف فرعًا من إعدادات النظام إن لم يوجد)";
        if (e.WarehouseType == WarehouseType.RepVan && e.OwnerEmployeeId is null) return "الكاش فان يحتاج تحديد المندوب صاحبه";
        if (e.WarehouseType == WarehouseType.WorkInProcess) return "مخزن تحت التصنيع يُنشأ تلقائيًا مع الماكينة (الإنتاج ← الماكينات)";
        return null;
    }

    protected override Task BeforeSaveAsync(ProjectDbContext db, Data.ProjectDb.Entities.Warehouse e)
    {
        e.Name = e.Name.Trim();
        // مخازن التالف والفحص والمرتجعات والمواد الأولية والطريق لا يُباع منها أبدًا
        if (e.WarehouseType is WarehouseType.Damaged or WarehouseType.UnderInspection or WarehouseType.Returns
            or WarehouseType.RawMaterial or WarehouseType.Transit or WarehouseType.WorkInProcess)
            e.IsSellableStock = false;
        if (e.WarehouseType != WarehouseType.RepVan) e.OwnerEmployeeId = null;
        return Task.CompletedTask;
    }
}

// ============================ هيكلية التعبئة ============================
public class PackagingSectionViewModel : CrudSectionViewModel<ItemPackagingLevel>
{
    private Item? _selectedItem;

    public PackagingSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Warehouse, "هيكلية التعبئة", Icons.Layers, "#14B8A6", "قطعة ← شرنك ← كارتون لكل صنف") { }

    public ObservableCollection<Item> ItemsLookup { get; } = new();
    public ObservableCollection<ItemPackagingLevel> ParentOptions { get; } = new();

    /// <summary>تصفية الجدول بصنف واحد (null = كل الأصناف).</summary>
    public Item? SelectedItem
    {
        get => _selectedItem;
        set { if (SetProperty(ref _selectedItem, value)) Background(LoadAsync()); }
    }

    protected override int GetId(ItemPackagingLevel e) => e.Id;
    protected override string Describe(ItemPackagingLevel e) => $"{e.Item?.ItemName} — {e.LevelName}";

    protected override async Task LoadLookupsAsync(ProjectDbContext db)
    {
        if (ItemsLookup.Count == 0)
            foreach (var i in await db.Items.AsNoTracking().Where(i => i.IsActive).OrderBy(i => i.ItemName).ToListAsync()) ItemsLookup.Add(i);
    }

    protected override Task<List<ItemPackagingLevel>> QueryAsync(ProjectDbContext db) =>
        db.ItemPackagingLevels.AsNoTracking().Include(p => p.Item).Include(p => p.ParentLevel)
          .Where(p => SelectedItem == null || p.ItemId == SelectedItem.Id)
          .OrderBy(p => p.Item.ItemName).ThenBy(p => p.EquivalentBaseUnits).ToListAsync();

    protected override ItemPackagingLevel CreateNew() => new() { ItemId = SelectedItem?.Id ?? 0, ContainsQuantity = 1, IsSellableUnit = true };

    protected override void OnEditorChanged()
    {
        ParentOptions.Clear();
        if (Editor is null) return;
        foreach (var p in Items.Where(p => p.ItemId == Editor.ItemId && p.Id != Editor.Id)) ParentOptions.Add(p);
    }

    protected override string? Validate(ItemPackagingLevel e)
    {
        if (e.ItemId == 0) return "اختر الصنف";
        if (string.IsNullOrWhiteSpace(e.LevelName)) return "أدخل اسم المستوى (مثال: كارتون)";
        if (e.ContainsQuantity <= 0) return "عدد الوحدات الأصغر يجب أن يكون أكبر من صفر";
        return null;
    }

    /// <summary>عدد القطع = عدد وحدات المستوى الأب × ما يحويه هذا المستوى (يُحسب تلقائيًا).</summary>
    protected override async Task BeforeSaveAsync(ProjectDbContext db, ItemPackagingLevel e)
    {
        e.LevelName = e.LevelName.Trim();
        decimal parentUnits = 1;
        if (e.ParentLevelId is int pid)
            parentUnits = await db.ItemPackagingLevels.Where(p => p.Id == pid).Select(p => p.EquivalentBaseUnits).FirstAsync();
        e.EquivalentBaseUnits = parentUnits * e.ContainsQuantity;
    }
}

// ============================ مواقع المخزن ============================
public class LocationsSectionViewModel : CrudSectionViewModel<WarehouseLocation>
{
    public LocationsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Warehouse, "مواقع المخزن", Icons.Location, "#F59E0B", "منطقة ← رف ← موقع دقيق") { }

    public IReadOnlyList<Option<LocationLevelType>> LevelOptions { get; } = ArabicLabels.OptionsOf<LocationLevelType>();
    public ObservableCollection<Data.ProjectDb.Entities.Warehouse> Warehouses { get; } = new();

    protected override int GetId(WarehouseLocation e) => e.Id;
    protected override string Describe(WarehouseLocation e) => $"{e.Warehouse?.Name} / {e.LocationName}";

    protected override async Task LoadLookupsAsync(ProjectDbContext db)
    {
        Warehouses.Clear();
        foreach (var w in await db.Warehouses.AsNoTracking().Where(w => w.IsActive && w.WarehouseType != WarehouseType.WorkInProcess).OrderBy(w => w.Name).ToListAsync()) Warehouses.Add(w);
    }

    protected override Task<List<WarehouseLocation>> QueryAsync(ProjectDbContext db) =>
        db.WarehouseLocations.AsNoTracking().Include(l => l.Warehouse).Include(l => l.ParentLocation)
          .OrderBy(l => l.Warehouse.Name).ThenBy(l => l.LocationName).ToListAsync();

    protected override WarehouseLocation CreateNew() => new() { WarehouseId = Warehouses.FirstOrDefault()?.Id ?? 0 };

    protected override string? Validate(WarehouseLocation e)
    {
        if (e.WarehouseId == 0) return "اختر المخزن";
        if (string.IsNullOrWhiteSpace(e.LocationName)) return "أدخل اسم الموقع";
        if (e.ParentLocationId == e.Id && e.Id != 0) return "الموقع لا يمكن أن يكون أبًا لنفسه";
        return null;
    }
}

// ============================ الرصيد الحالي ============================
public class CurrentStockSectionViewModel : SectionViewModel
{
    private Data.ProjectDb.Entities.Warehouse? _selectedWarehouse;

    public CurrentStockSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Warehouse, "الرصيد الحالي", Icons.Stock, "#10B981", "الكميات والقيمة لكل صنف ومخزن وتشغيلة") { }

    public ObservableCollection<Data.ProjectDb.Entities.Warehouse> Warehouses { get; } = new();
    public ObservableCollection<CurrentStockRow> Rows { get; } = new();

    public Data.ProjectDb.Entities.Warehouse? SelectedWarehouse
    {
        get => _selectedWarehouse;
        set { if (SetProperty(ref _selectedWarehouse, value)) Background(LoadAsync()); }
    }

    protected override bool ReloadOnActivate => true;
    public decimal TotalQuantity => Rows.Sum(r => r.QuantityBaseUnits);
    public decimal TotalValue => Rows.Sum(r => r.Value);

    public override async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            await using var db = Session.NewDb();
            if (Warehouses.Count == 0)
                foreach (var w in await db.Warehouses.AsNoTracking().OrderBy(w => w.Name).ToListAsync()) Warehouses.Add(w);

            Rows.Clear();
            foreach (var r in await new StockQueryService(db).GetCurrentStockAsync(SelectedWarehouse?.Id)) Rows.Add(r);
            OnPropertyChanged(nameof(TotalQuantity));
            OnPropertyChanged(nameof(TotalValue));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public RelayCommand ClearFilterCommand => new(() => SelectedWarehouse = null);
}

// ============================ تسوية المخزون ============================
public class StockMovementRow
{
    public DateTime TransactionDate { get; init; }
    public string ItemName { get; init; } = "";
    public string WarehouseName { get; init; } = "";
    public string? BatchNumber { get; init; }
    public decimal Quantity { get; init; }
    public string TypeLabel { get; init; } = "";
    public string? Notes { get; init; }
}

public class StockAdjustmentSectionViewModel : SectionViewModel
{
    private Option<StockAdjustmentKind> _kind;
    private Item? _item;
    private Data.ProjectDb.Entities.Warehouse? _warehouse;
    private ItemBatch? _batch;
    private decimal _quantity;
    private Option<DamageReason>? _reason;
    private string? _notes;
    private decimal? _availableBalance;

    public StockAdjustmentSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Warehouse, "تسوية المخزون", Icons.Adjust, "#EF4444", "تالف / إتلاف / إرجاع للمخزن")
    {
        _kind = KindOptions[0];
        SubmitCommand = new AsyncRelayCommand(SubmitAsync);
    }

    public IReadOnlyList<Option<StockAdjustmentKind>> KindOptions { get; } = ArabicLabels.OptionsOf<StockAdjustmentKind>();
    public IReadOnlyList<Option<DamageReason>> ReasonOptions { get; } = ArabicLabels.OptionsOf<DamageReason>();
    public ObservableCollection<Item> ItemsLookup { get; } = new();
    public ObservableCollection<Data.ProjectDb.Entities.Warehouse> Warehouses { get; } = new();
    public ObservableCollection<ItemBatch> Batches { get; } = new();
    public ObservableCollection<StockMovementRow> RecentAdjustments { get; } = new();

    public Option<StockAdjustmentKind> Kind
    {
        get => _kind;
        set { if (SetProperty(ref _kind, value)) OnPropertyChanged(nameof(NeedsReason)); }
    }
    public bool NeedsReason => Kind.Value == StockAdjustmentKind.Damaged;

    // ليس "Item": WPF يعامل خاصية بهذا الاسم كمفهرس عند كتابة null من القائمة المنسدلة فيرمي NullReferenceException
    public Item? AdjustItem { get => _item; set { if (SetProperty(ref _item, value)) Background(RefreshBatchesAsync()); } }
    public Data.ProjectDb.Entities.Warehouse? Warehouse { get => _warehouse; set { if (SetProperty(ref _warehouse, value)) Background(RefreshBalanceAsync()); } }
    public ItemBatch? Batch { get => _batch; set { if (SetProperty(ref _batch, value)) Background(RefreshBalanceAsync()); } }
    public decimal Quantity { get => _quantity; set => SetProperty(ref _quantity, value); }
    public Option<DamageReason>? Reason { get => _reason; set => SetProperty(ref _reason, value); }
    public string? Notes { get => _notes; set => SetProperty(ref _notes, value); }
    public decimal? AvailableBalance { get => _availableBalance; private set => SetProperty(ref _availableBalance, value); }

    public AsyncRelayCommand SubmitCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        if (ItemsLookup.Count == 0)
        {
            foreach (var i in await db.Items.AsNoTracking().Where(i => i.IsActive).OrderBy(i => i.ItemName).ToListAsync()) ItemsLookup.Add(i);
            foreach (var w in await db.Warehouses.AsNoTracking().Where(w => w.IsActive && w.WarehouseType != WarehouseType.WorkInProcess).OrderBy(w => w.Name).ToListAsync()) Warehouses.Add(w);
        }
        var types = new[] { StockTransactionType.Damaged, StockTransactionType.ReturnToWarehouse };
        var rows = await db.StockTransactions.AsNoTracking()
            .Where(t => t.ReferenceTable == "StockAdjustment" && types.Contains(t.TransactionType))
            .OrderByDescending(t => t.Id).Take(50)
            .Select(t => new { t.TransactionDate, t.Item.ItemName, WarehouseName = t.Warehouse.Name, BatchNumber = t.Batch != null ? t.Batch.BatchNumber : null,
                               t.QuantityBaseUnits, t.TransactionType, t.DamageReason, t.FreeIssueRecipient })
            .ToListAsync();
        RecentAdjustments.Clear();
        foreach (var r in rows)
            RecentAdjustments.Add(new StockMovementRow
            {
                TransactionDate = r.TransactionDate.ToLocalTime(), ItemName = r.ItemName, WarehouseName = r.WarehouseName,
                BatchNumber = r.BatchNumber, Quantity = r.QuantityBaseUnits,
                TypeLabel = ArabicLabels.Of(r.TransactionType) + (r.DamageReason is null ? "" : $" ({ArabicLabels.Of(r.DamageReason)})"),
                Notes = r.FreeIssueRecipient
            });
    }

    private async Task RefreshBatchesAsync()
    {
        Batches.Clear();
        Batch = null;
        if (AdjustItem is null) return;
        await using var db = Session.NewDb();
        foreach (var b in await db.ItemBatches.AsNoTracking().Where(b => b.ItemId == AdjustItem.Id).OrderBy(b => b.ExpiryDate).ToListAsync()) Batches.Add(b);
        await RefreshBalanceAsync();
    }

    private async Task RefreshBalanceAsync()
    {
        if (AdjustItem is null || Warehouse is null) { AvailableBalance = null; return; }
        await using var db = Session.NewDb();
        AvailableBalance = await new InventoryService(db).GetBalanceAsync(AdjustItem.Id, Warehouse.Id, Batch?.Id);
    }

    private async Task SubmitAsync()
    {
        if (!Require(Kind.Value == StockAdjustmentKind.Return ? CanAdd : CanEdit, "تسوية المخزون")) return;
        if (AdjustItem is null || Warehouse is null) { Dialogs.Error("اختر الصنف والمخزن"); return; }

        await using var db = Session.NewDb();
        var ok = await RunOperationAsync(
            () => new InventoryService(db).AdjustAsync(Kind.Value, AdjustItem.Id, Warehouse.Id, Batch?.Id, Quantity, Reason?.Value, Notes, Session.UserId),
            $"تم تسجيل {Kind.Label}: {Quantity:0.###} قطعة من {AdjustItem.ItemName}");
        if (!ok) return;
        Quantity = 0;
        Notes = null;
        await RefreshBalanceAsync();
        await LoadAsync();
    }
}

// ============================ احتياجات التصنيع ============================
public class ManufacturingRequirementSectionViewModel : SectionViewModel
{
    private Item? _finishedItem;
    private decimal _quantity = 1000;

    public ManufacturingRequirementSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Warehouse, "احتياجات التصنيع", Icons.Factory, "#8B5CF6", "المواد الأولية المطلوبة مقابل الرصيد المتاح")
    {
        CalculateCommand = new AsyncRelayCommand(CalculateAsync);
    }

    public ObservableCollection<Item> FinishedItems { get; } = new();
    public ObservableCollection<ManufacturingRequirementRow> Rows { get; } = new();
    public Item? FinishedItem { get => _finishedItem; set => SetProperty(ref _finishedItem, value); }
    public decimal Quantity { get => _quantity; set => SetProperty(ref _quantity, value); }
    public bool AllSufficient => Rows.Count > 0 && Rows.All(r => r.IsSufficient);
    public AsyncRelayCommand CalculateCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        FinishedItems.Clear();
        var withBom = db.BillOfMaterials.Where(b => b.IsActive).Select(b => b.FinishedItemId);
        foreach (var i in await db.Items.AsNoTracking().Where(i => withBom.Contains(i.Id)).OrderBy(i => i.ItemName).ToListAsync()) FinishedItems.Add(i);
    }

    private async Task CalculateAsync()
    {
        if (FinishedItem is null) { Dialogs.Error("اختر المنتج النهائي (يجب أن تكون له قائمة مواد)"); return; }
        if (Quantity <= 0) { Dialogs.Error("أدخل كمية إنتاج أكبر من صفر"); return; }
        await using var db = Session.NewDb();
        Rows.Clear();
        foreach (var r in await new ManufacturingRequirementService(db).CalculateAsync(FinishedItem.Id, Quantity)) Rows.Add(r);
        OnPropertyChanged(nameof(AllSufficient));
        StatusMessage = AllSufficient ? "كل المواد الأولية متوفرة" : "توجد مواد أولية غير كافية (باللون الأحمر)";
    }
}

// ============================ إعداد قوائم المواد (BOM) ============================
public class BomLineRow
{
    public int Id { get; init; }
    public int RawMaterialItemId { get; init; }
    public string RawMaterialName { get; init; } = "";
    public decimal QuantityPerUnit { get; init; }
    public string? ComponentRole { get; init; }
}

public class BomSectionViewModel : SectionViewModel
{
    private Item? _finishedItem;
    private BillOfMaterials? _bom;
    private Item? _newRawItem;
    private decimal _newQuantity;

    public BomSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Warehouse, "إعداد قوائم المواد", Icons.Recipe, "#EC4899", "الوصفة الأساسية لكل منتج نهائي")
    {
        AddLineCommand = new AsyncRelayCommand(AddLineAsync);
        DeleteLineCommand = new AsyncRelayCommand(p => p is BomLineRow r ? DeleteLineAsync(r) : Task.CompletedTask);
        ApplyTemplateCommand = new AsyncRelayCommand(ApplyTemplateAsync);
    }

    // ---------------- تطبيق قالب تعبئة ----------------
    private PackagingTemplate? _template;
    public ObservableCollection<PackagingTemplate> Templates { get; } = new();
    public ObservableCollection<TemplateRoleChoice> RoleChoices { get; } = new();
    public PackagingTemplate? Template
    {
        get => _template;
        set
        {
            if (!SetProperty(ref _template, value)) return;
            RoleChoices.Clear();
            foreach (var l in value?.Lines.OrderBy(l => l.Id) ?? Enumerable.Empty<PackagingTemplateLine>())
                RoleChoices.Add(new TemplateRoleChoice { Role = l.ComponentRole, RatioText = l.RatioText, ChosenItem = AllItems.FirstOrDefault(i => i.Id == l.DefaultItemId) });
        }
    }
    public AsyncRelayCommand ApplyTemplateCommand { get; }

    /// <summary>يملأ قائمة مواد الصنف من القالب (تُستبدل القائمة الحالية) — الصنف يبقى بوصفة ثابتة واحدة.</summary>
    private async Task ApplyTemplateAsync()
    {
        if (!Require(CanEdit || CanAdd, "تعديل قوائم المواد")) return;
        if (FinishedItem is null || Template is null) { Dialogs.Error("اختر المنتج النهائي والقالب"); return; }
        if (Lines.Count > 0 && !Dialogs.Confirm($"استبدال قائمة مواد {FinishedItem.ItemName} الحالية بمكونات القالب {Template.Name}؟")) return;
        await using var db = Session.NewDb();
        var choices = RoleChoices.Where(c => c.ChosenItem is not null).ToDictionary(c => c.Role, c => c.ChosenItem!.Id);
        if (await RunOperationAsync(() => new PackagingTemplateService(db).ApplyToItemAsync(FinishedItem.Id, Template.Id, choices),
                                    $"طُبّق القالب {Template.Name} على {FinishedItem.ItemName}"))
            await LoadBomAsync();
    }

    public ObservableCollection<Item> AllItems { get; } = new();
    public ObservableCollection<BomLineRow> Lines { get; } = new();

    public Item? FinishedItem { get => _finishedItem; set { if (SetProperty(ref _finishedItem, value)) Background(LoadBomAsync()); } }
    public string BomStatus => FinishedItem is null ? "اختر منتجًا نهائيًا" :
        _bom is null ? "لا توجد قائمة مواد بعد — ستُنشأ تلقائيًا عند إضافة أول مادة" : $"{_bom.Name} ({Lines.Count} مادة)";
    public Item? NewRawItem { get => _newRawItem; set => SetProperty(ref _newRawItem, value); }
    public decimal NewQuantity { get => _newQuantity; set => SetProperty(ref _newQuantity, value); }

    public AsyncRelayCommand AddLineCommand { get; }
    public AsyncRelayCommand DeleteLineCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        AllItems.Clear();
        foreach (var i in await db.Items.AsNoTracking().Where(i => i.IsActive).OrderBy(i => i.ItemName).ToListAsync()) AllItems.Add(i);
        var templateId = Template?.Id;
        Templates.Clear();
        foreach (var t in await new PackagingTemplateService(db).GetAllAsync(activeOnly: true)) Templates.Add(t);
        _template = null;
        Template = Templates.FirstOrDefault(t => t.Id == templateId);
        await LoadBomAsync();
    }

    private async Task LoadBomAsync()
    {
        Lines.Clear();
        _bom = null;
        if (FinishedItem is not null)
        {
            await using var db = Session.NewDb();
            _bom = await db.BillOfMaterials.AsNoTracking().Include(b => b.Lines).ThenInclude(l => l.RawMaterialItem)
                .FirstOrDefaultAsync(b => b.FinishedItemId == FinishedItem.Id && b.IsActive);
            foreach (var l in _bom?.Lines ?? Enumerable.Empty<BOMLine>())
                Lines.Add(new BomLineRow { Id = l.Id, RawMaterialItemId = l.RawMaterialItemId, RawMaterialName = l.RawMaterialItem.ItemName,
                                           QuantityPerUnit = l.QuantityPerUnit, ComponentRole = l.ComponentRole });
        }
        OnPropertyChanged(nameof(BomStatus));
    }

    private async Task AddLineAsync()
    {
        if (!Require(CanEdit || CanAdd, "تعديل قوائم المواد")) return;
        if (FinishedItem is null || NewRawItem is null) { Dialogs.Error("اختر المنتج النهائي والمادة الأولية"); return; }
        if (NewRawItem.Id == FinishedItem.Id) { Dialogs.Error("المنتج لا يمكن أن يكون مادة أولية لنفسه"); return; }
        if (NewQuantity <= 0) { Dialogs.Error("أدخل الكمية لكل وحدة منتج (أكبر من صفر)"); return; }
        if (Lines.Any(l => l.RawMaterialItemId == NewRawItem.Id)) { Dialogs.Error("هذه المادة موجودة في القائمة؛ احذفها وأعد إضافتها لتعديل الكمية"); return; }

        await using var db = Session.NewDb();
        var bomId = _bom?.Id ?? 0;
        if (bomId == 0)
        {
            var bom = new BillOfMaterials { FinishedItemId = FinishedItem.Id };
            db.BillOfMaterials.Add(bom);
            await db.SaveChangesAsync();
            bomId = bom.Id;
        }
        db.BOMLines.Add(new BOMLine { BOMId = bomId, RawMaterialItemId = NewRawItem.Id, QuantityPerUnit = NewQuantity });
        await db.SaveChangesAsync();
        StatusMessage = $"أُضيفت {NewRawItem.ItemName} إلى وصفة {FinishedItem.ItemName}";
        NewRawItem = null;
        NewQuantity = 0;
        await LoadBomAsync();
    }

    private async Task DeleteLineAsync(BomLineRow row)
    {
        if (!Require(CanDelete, "الحذف")) return;
        if (!Dialogs.Confirm($"حذف {row.RawMaterialName} من الوصفة؟")) return;
        await using var db = Session.NewDb();
        await db.BOMLines.Where(l => l.Id == row.Id).ExecuteDeleteAsync();
        await LoadBomAsync();
    }
}

// ============================ إعدادات التنبيهات ============================
public class StockAlertRow : ObservableObject
{
    private decimal? _minLevel;

    public int ItemId { get; init; }
    public string ItemCode { get; init; } = "";
    public string ItemName { get; init; } = "";
    public decimal Balance { get; init; }
    public decimal? MinLevel { get => _minLevel; set { if (SetProperty(ref _minLevel, value)) OnPropertyChanged(nameof(IsLow)); } }
    public bool IsLow => MinLevel is not null && Balance <= MinLevel;
    internal decimal? Original { get; set; }
}

public class StockAlertsSectionViewModel : SectionViewModel
{
    public StockAlertsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Warehouse, "إعدادات التنبيهات", Icons.Alert, "#F97316", "حد التنبيه الأدنى لكل صنف والأصناف المنخفضة")
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync);
    }

    public ObservableCollection<StockAlertRow> Rows { get; } = new();
    protected override bool ReloadOnActivate => true;
    public int LowCount => Rows.Count(r => r.IsLow);
    public AsyncRelayCommand SaveCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var balances = await db.StockTransactions.GroupBy(t => t.ItemId)
            .Select(g => new { g.Key, Qty = g.Sum(t => t.QuantityBaseUnits) }).ToDictionaryAsync(x => x.Key, x => x.Qty);
        Rows.Clear();
        foreach (var i in await db.Items.AsNoTracking().Where(i => i.IsActive).OrderBy(i => i.ItemCode).ToListAsync())
            Rows.Add(new StockAlertRow
            {
                ItemId = i.Id, ItemCode = i.ItemCode, ItemName = i.ItemName,
                Balance = balances.GetValueOrDefault(i.Id), MinLevel = i.MinStockAlertLevel, Original = i.MinStockAlertLevel
            });
        OnPropertyChanged(nameof(LowCount));
    }

    private async Task SaveAsync()
    {
        if (!Require(CanEdit, "التعديل")) return;
        var changed = Rows.Where(r => r.MinLevel != r.Original).ToList();
        if (changed.Any(r => r.MinLevel < 0)) { Dialogs.Error("حد التنبيه لا يمكن أن يكون سالبًا"); return; }

        await using var db = Session.NewDb();
        foreach (var r in changed)
            await db.Items.Where(i => i.Id == r.ItemId).ExecuteUpdateAsync(u => u.SetProperty(i => i.MinStockAlertLevel, r.MinLevel));
        StatusMessage = changed.Count == 0 ? "لا توجد تغييرات" : $"تم حفظ حدود التنبيه لـ {changed.Count} صنف";
        await LoadAsync();
    }
}
