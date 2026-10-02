using System.Collections.ObjectModel;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Production;

public class ProductionModuleViewModel : ModuleViewModel
{
    public ProductionModuleViewModel(AppSession s, IDialogService d)
        : base("الإنتاج والمختبر", Icons.Production, ModuleColors.Production)
    {
        UseDashboard(s, d, ModuleCode.Production, ModuleDashboardViewModel.Production);
        Orders = Add(new ProductionOrdersSectionViewModel(s, d));
        Qc = Add(new QcSectionViewModel(s, d));
        Packing = Add(new PackingSectionViewModel(s, d));
        Wip = Add(new MachineWipSectionViewModel(s, d));
        Machines = Add(new MachinesSectionViewModel(s, d));
        Add(new QualityTestsSectionViewModel(s, d));
        Add(new CustomRecipesSectionViewModel(s, d));
    }

    public ProductionOrdersSectionViewModel Orders { get; }
    public QcSectionViewModel Qc { get; }
    public PackingSectionViewModel Packing { get; }
    public MachineWipSectionViewModel Wip { get; }
    public MachinesSectionViewModel Machines { get; }
}

// ============================ أوامر الإنتاج ============================
public class RequirementPreview
{
    public string RawMaterialName { get; init; } = "";
    public decimal Required { get; init; }
    public decimal Available { get; init; }
    public string WhereText { get; init; } = "";
    public bool IsSufficient => Available >= Required;
}

public class ProductionOrdersSectionViewModel : SectionViewModel
{
    private bool _isComposing;
    private Item? _finishedItem;
    private CustomRecipe? _recipe;
    private decimal _quantity = 1000;
    private Data.ProjectDb.Entities.Warehouse? _rawWarehouse;
    private Machine? _machine;

    public ProductionOrdersSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Production, "أوامر الإنتاج", Icons.Factory, "#14B8A6", "إنشاء الأمر من قائمة المواد، بدء التشغيل، والإغلاق")
    {
        NewOrderCommand = new AsyncRelayCommand(async () =>
        {
            if (!Require(CanAdd, "إنشاء أوامر الإنتاج")) return;
            BatchOrder = null;
            await using var db = Session.NewDb();
            BatchNumber = await new ProductionService(db).NextBatchNumberAsync();
            IsComposing = true;
        });
        EditBatchCommand = new AsyncRelayCommand(p => p is ProductionOrderRow r ? OpenBatchAsync(r) : Task.CompletedTask);
        SaveBatchCommand = new AsyncRelayCommand(SaveBatchAsync);
        CloseBatchCommand = new RelayCommand(() => BatchOrder = null);
        CancelComposeCommand = new RelayCommand(() => IsComposing = false);
        PreviewCommand = new AsyncRelayCommand(PreviewAsync);
        CreateCommand = new AsyncRelayCommand(CreateAsync);
        StartCommand = new AsyncRelayCommand(p => p is ProductionOrderRow r ? StartAsync(r) : Task.CompletedTask);
        CompleteCommand = new AsyncRelayCommand(p => p is ProductionOrderRow r ? CompleteAsync(r) : Task.CompletedTask);
        CancelOrderCommand = new AsyncRelayCommand(p => p is ProductionOrderRow r ? CancelAsync(r) : Task.CompletedTask);
        PrintCommand = new AsyncRelayCommand(p => p is ProductionOrderRow r ? PrintAsync(db => DocumentReports.ProductionOrderAsync(Session, db, r.Id)) : Task.CompletedTask);
        PrintPackingCommand = new AsyncRelayCommand(p => p is ProductionOrderRow r ? PrintAsync(db => DocumentReports.PackingAsync(Session, db, r.Id)) : Task.CompletedTask);
    }

    protected override bool ReloadOnActivate => true;

    public ObservableCollection<ProductionOrderRow> Orders { get; } = new();
    public ObservableCollection<Item> FinishedItems { get; } = new();
    public ObservableCollection<CustomRecipe> Recipes { get; } = new();
    public ObservableCollection<Data.ProjectDb.Entities.Warehouse> RawWarehouses { get; } = new();
    public ObservableCollection<RequirementPreview> Preview { get; } = new();
    public ObservableCollection<Machine> Machines { get; } = new();
    public Machine? Machine { get => _machine; set => SetProperty(ref _machine, value); }

    public bool IsComposing { get => _isComposing; private set => SetProperty(ref _isComposing, value); }
    public Item? FinishedItem { get => _finishedItem; set { if (SetProperty(ref _finishedItem, value)) Background(LoadRecipesAsync()); } }
    public CustomRecipe? Recipe { get => _recipe; set { if (SetProperty(ref _recipe, value)) Background(PreviewAsync()); } }
    public decimal Quantity { get => _quantity; set => SetProperty(ref _quantity, value); }
    public Data.ProjectDb.Entities.Warehouse? RawWarehouse { get => _rawWarehouse; set => SetProperty(ref _rawWarehouse, value); }
    public bool AllSufficient => Preview.Count > 0 && Preview.All(p => p.IsSufficient);

    public AsyncRelayCommand NewOrderCommand { get; }
    public AsyncRelayCommand EditBatchCommand { get; }
    public AsyncRelayCommand SaveBatchCommand { get; }
    public RelayCommand CloseBatchCommand { get; }

    // ---------------- رقم الدفعة ----------------
    private string _batchNumber = "";
    private ProductionOrderRow? _batchOrder;
    private string _newBatchNumber = "";
    private string _batchReason = "";
    /// <summary>رقم الدفعة للأمر الجديد: مولَّد تلقائيًا وقابل للتعديل قبل الإنشاء.</summary>
    public string BatchNumber { get => _batchNumber; set => SetProperty(ref _batchNumber, value); }
    /// <summary>الأمر المفتوح لتعديل رقم دفعته (لوحة جانبية).</summary>
    public ProductionOrderRow? BatchOrder
    {
        get => _batchOrder;
        private set { if (SetProperty(ref _batchOrder, value)) { OnPropertyChanged(nameof(IsEditingBatch)); if (value is not null) IsComposing = false; } }
    }
    public bool IsEditingBatch => BatchOrder is not null;
    public string NewBatchNumber { get => _newBatchNumber; set => SetProperty(ref _newBatchNumber, value); }
    public string BatchReason { get => _batchReason; set => SetProperty(ref _batchReason, value); }
    public ObservableCollection<string> BatchHistory { get; } = new();
    protected override bool HasPendingInput => IsComposing || IsEditingBatch;
    public RelayCommand CancelComposeCommand { get; }
    public AsyncRelayCommand PreviewCommand { get; }
    public AsyncRelayCommand CreateCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand CompleteCommand { get; }
    public AsyncRelayCommand CancelOrderCommand { get; }
    public AsyncRelayCommand PrintCommand { get; }
    public AsyncRelayCommand PrintPackingCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        if (FinishedItems.Count == 0)
        {
            var withBom = db.BillOfMaterials.Where(b => b.IsActive).Select(b => b.FinishedItemId);
            foreach (var i in await db.Items.AsNoTracking().Where(i => withBom.Contains(i.Id)).OrderBy(i => i.ItemName).ToListAsync()) FinishedItems.Add(i);
            // الترتيب في الذاكرة: نوع المخزن مخزّن نصًا، ومقارنته داخل ORDER BY لا تُترجم لـ SQL
            var candidates = await db.Warehouses.AsNoTracking()
                .Where(w => w.IsActive && w.WarehouseType != WarehouseType.RepVan && w.WarehouseType != WarehouseType.Damaged
                         && w.WarehouseType != WarehouseType.WorkInProcess && w.WarehouseType != WarehouseType.FinishedGoods).ToListAsync();
            foreach (var w in candidates.OrderBy(w => w.WarehouseType != WarehouseType.RawMaterial).ThenBy(w => w.Name)) RawWarehouses.Add(w);
            RawWarehouse ??= RawWarehouses.FirstOrDefault();
        }
        // الماكينات تُحدَّث في كل تحميل (قد تُضاف ماكينة من تبويب الماكينات)
        var machineId = Machine?.Id;
        Machines.Clear();
        foreach (var m in await new MachineService(db).GetAllAsync(activeOnly: true)) Machines.Add(m);
        _machine = Machines.FirstOrDefault(m => m.Id == machineId) ?? Machines.FirstOrDefault();
        OnPropertyChanged(nameof(Machine));
        Orders.Clear();
        foreach (var o in await new ProductionService(db).GetOrdersAsync()) Orders.Add(o);
    }

    private async Task LoadRecipesAsync()
    {
        Recipes.Clear();
        _recipe = null;
        OnPropertyChanged(nameof(Recipe));
        if (FinishedItem is not null)
        {
            await using var db = Session.NewDb();
            foreach (var r in await db.CustomRecipes.AsNoTracking().Include(r => r.Customer)
                         .Where(r => r.FinishedItemId == FinishedItem.Id && r.IsActive).OrderBy(r => r.Name).ToListAsync()) Recipes.Add(r);
        }
        await PreviewAsync();
    }

    /// <summary>معاينة المواد المطلوبة مقابل المتاح في مخزن المواد قبل إنشاء الأمر.</summary>
    private async Task PreviewAsync()
    {
        Preview.Clear();
        if (FinishedItem is null || RawWarehouse is null || Quantity <= 0) { OnPropertyChanged(nameof(AllSufficient)); return; }
        await using var db = Session.NewDb();
        var service = new ManufacturingRequirementService(db);
        var (_, error) = await new ProductionService(db).MergeRecipeAsync(FinishedItem.Id, Recipe?.Id);
        if (error is not null) { StatusMessage = error; OnPropertyChanged(nameof(AllSufficient)); return; }
        // نفس محرك التوفر المستخدم في "احتياجات التصنيع" وفي بدء التشغيل
        foreach (var r in await service.CalculateAsync(FinishedItem.Id, Quantity, Recipe?.Id, RawWarehouse.Id))
            Preview.Add(new RequirementPreview { RawMaterialName = r.RawMaterialName, Required = r.QuantityRequired, Available = r.QuantityAvailable, WhereText = r.WhereText });
        OnPropertyChanged(nameof(AllSufficient));
    }

    private async Task CreateAsync()
    {
        if (FinishedItem is null || RawWarehouse is null) { Dialogs.Error("اختر المنتج ومخزن المواد الأولية"); return; }
        if (Machine is null) { Dialogs.Error("اختر الماكينة (أضفها من تبويب الماكينات إن لم توجد)"); return; }
        await using var db = Session.NewDb();
        if (await RunOperationAsync(async () => (await new ProductionService(db).CreateOrderAsync(FinishedItem.Id, Quantity, Recipe?.Id, RawWarehouse.Id, Machine.Id, Session.UserId, BatchNumber)).result,
                                    "تم إنشاء أمر الإنتاج — ابدأ تشغيله من الجدول"))
        {
            IsComposing = false;
            await LoadAsync();
        }
    }

    private async Task OpenBatchAsync(ProductionOrderRow row)
    {
        if (!Require(CanEdit, "تعديل رقم الدفعة")) return;
        BatchOrder = row;
        NewBatchNumber = row.OutputBatch ?? "";
        BatchReason = "";
        await LoadBatchHistoryAsync();
    }

    private async Task LoadBatchHistoryAsync()
    {
        BatchHistory.Clear();
        if (BatchOrder is null) return;
        await using var db = Session.NewDb();
        foreach (var c in await new ProductionService(db).GetBatchHistoryAsync(BatchOrder.Id))
            BatchHistory.Add($"{c.ChangedAt.ToLocalTime():yyyy/MM/dd HH:mm} — {c.ChangedByUser.Username}: {c.OldNumber} ← {c.NewNumber}"
                             + (c.Reason is null ? "" : $" ({c.Reason})"));
    }

    private async Task SaveBatchAsync()
    {
        if (BatchOrder is null || !Require(CanEdit, "تعديل رقم الدفعة")) return;
        await using var db = Session.NewDb();
        var id = BatchOrder.Id;
        if (await RunOperationAsync(() => new ProductionService(db).ChangeBatchNumberAsync(id, NewBatchNumber, BatchReason, Session.UserId),
                                    $"تم تعديل رقم الدفعة إلى {NewBatchNumber.Trim()}"))
        {
            BatchOrder = null;
            await LoadAsync();
        }
    }

    private async Task StartAsync(ProductionOrderRow row)
    {
        if (!Require(CanEdit, "بدء التشغيل")) return;
        if (!Dialogs.Confirm($"بدء تشغيل {row.MONumber}؟ ستُصرف المواد الأولية من المخزن إلى تحت تصنيع الماكينة {row.MachineName} فورًا.")) return;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new ProductionService(db).StartAsync(row.Id, Session.UserId), $"بدأ تشغيل {row.MONumber} — أرسل عينة للمختبر"))
            await LoadAsync();
    }

    private async Task CompleteAsync(ProductionOrderRow row)
    {
        if (!Require(CanPost, "إغلاق أوامر الإنتاج")) return;
        if (!Dialogs.Confirm($"إغلاق {row.MONumber} بما عُبّئ ({row.PackedQuantity:N0} من {row.QuantityToProduce:N0})؟ الفرق يُعتبر هدرًا.")) return;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new ProductionService(db).CompleteAsync(row.Id), $"أُغلق {row.MONumber}"))
            await LoadAsync();
    }

    private async Task CancelAsync(ProductionOrderRow row)
    {
        if (!Require(CanDelete, "إلغاء أوامر الإنتاج")) return;
        var warn = row.Status == ProductionOrderStatus.InProgress ? " المواد المصروفة لن تُعاد (تُحسب هدرًا)." : "";
        if (!Dialogs.Confirm($"إلغاء {row.MONumber}؟{warn}")) return;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new ProductionService(db).CancelAsync(row.Id), $"أُلغي {row.MONumber}"))
            await LoadAsync();
    }
}

// ============================ فحص المختبر ============================
public class QcLine : ObservableObject
{
    private string _measured = "";
    private Option<bool?> _manual;

    public QcLine(Option<bool?> auto) => _manual = auto;

    public int TestId { get; init; }
    public string TestName { get; init; } = "";
    public string StandardText { get; init; } = "";
    public string Measured { get => _measured; set => SetProperty(ref _measured, value); }
    public Option<bool?> Manual { get => _manual; set => SetProperty(ref _manual, value); }
    public string ResultLabel { get; set; } = "";
}

public class QcHistoryRow
{
    public int Id { get; init; }
    public DateTime TestDate { get; init; }
    public string MONumber { get; init; } = "";
    public string BatchNumber { get; init; } = "";
    public string ResultLabel { get; init; } = "";
    public string TestedBy { get; init; } = "";
    public string Details { get; init; } = "";
}

public class QcSectionViewModel : SectionViewModel
{
    private ProductionOrderRow? _order;
    private string? _lastResult;

    public QcSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Production, "فحص المختبر", Icons.Star, "#8B5CF6", "نتائج اختبارات الدفعة برقمها — فشل اختبار واحد يرفضها")
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        PrintCommand = new AsyncRelayCommand(p => p is QcHistoryRow r ? PrintAsync(db => DocumentReports.QcResultAsync(Session, db, r.Id)) : Task.CompletedTask);
    }

    protected override bool ReloadOnActivate => true;

    public IReadOnlyList<Option<bool?>> ManualOptions { get; } = new[]
    {
        new Option<bool?>(null, "تلقائي من المعيار"), new Option<bool?>(true, "ناجح (قرار الفاحص)"), new Option<bool?>(false, "راسب (قرار الفاحص)")
    };
    public ObservableCollection<ProductionOrderRow> InProgressOrders { get; } = new();
    public ObservableCollection<QcLine> Lines { get; } = new();
    public ObservableCollection<QcHistoryRow> History { get; } = new();

    public ProductionOrderRow? Order { get => _order; set { if (SetProperty(ref _order, value)) Background(LoadTestsAsync()); } }
    public string? LastResult { get => _lastResult; private set => SetProperty(ref _lastResult, value); }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand PrintCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var orderId = Order?.Id;
        InProgressOrders.Clear();
        foreach (var o in (await new ProductionService(db).GetOrdersAsync()).Where(o => o.Status == ProductionOrderStatus.InProgress)) InProgressOrders.Add(o);
        _order = InProgressOrders.FirstOrDefault(o => o.Id == orderId) ?? InProgressOrders.FirstOrDefault();
        OnPropertyChanged(nameof(Order));
        await LoadTestsAsync();

        var results = await db.QCBatchResults.AsNoTracking().OrderByDescending(q => q.Id).Take(100)
            .Select(q => new
            {
                q.Id, q.TestDate, q.ProductionOrder.MONumber, q.Batch.BatchNumber, q.OverallResult, q.TestedByUser.Username,
                Lines = q.ResultLines.Select(l => l.QualityTest.TestName + ": " + l.MeasuredValue + (l.Result == QCLineResult.Pass ? " ✓" : " ✗"))
            }).ToListAsync();
        History.Clear();
        foreach (var r in results)
            History.Add(new QcHistoryRow
            {
                Id = r.Id, TestDate = r.TestDate.ToLocalTime(), MONumber = r.MONumber, BatchNumber = r.BatchNumber, TestedBy = r.Username,
                ResultLabel = r.OverallResult == QCOverallResult.Passed ? "ناجحة" : "مرفوضة", Details = string.Join(" · ", r.Lines)
            });
    }

    private async Task LoadTestsAsync()
    {
        Lines.Clear();
        if (Order is null) return;
        await using var db = Session.NewDb();
        var itemId = await db.ProductionOrders.Where(o => o.Id == Order.Id).Select(o => o.FinishedItemId).FirstAsync();
        foreach (var t in await new ProductionService(db).GetApplicableTestsAsync(itemId))
            Lines.Add(new QcLine(ManualOptions[0])
            {
                TestId = t.Id, TestName = t.TestName,
                StandardText = t.StandardMin is not null || t.StandardMax is not null
                    ? $"{t.StandardMin?.ToString("0.##") ?? "…"} – {t.StandardMax?.ToString("0.##") ?? "…"}"
                    : $"= {t.StandardText}"
            });
    }

    private async Task SaveAsync()
    {
        if (!Require(CanAdd || CanEdit, "تسجيل نتائج المختبر")) return;
        if (Order is null) { Dialogs.Error("اختر رقم الدفعة المطلوب فحصها"); return; }
        await using var db = Session.NewDb();
        QCOverallResult? overall = null;
        if (await RunOperationAsync(async () =>
            {
                // الربط بأمر الإنتاج عبر رقم الدفعة فقط
                var (r, o) = await new ProductionService(db).RecordQcByBatchAsync(Order.OutputBatch ?? "",
                    Lines.Select(l => new QcInput(l.TestId, l.Measured, l.Manual.Value)).ToList(), Session.UserId);
                overall = o;
                return r;
            }, "تم حفظ نتيجة الفحص"))
        {
            LastResult = overall == QCOverallResult.Passed
                ? $"الدفعة {Order.OutputBatch} ناجحة ✓ — يمكن تعبئتها الآن"
                : $"الدفعة {Order.OutputBatch} مرفوضة ✗ — لا يمكن تعبئتها (أعد الفحص أو ألغِ الأمر)";
            await LoadAsync();
        }
    }
}

// ============================ التعبئة ============================
public class PackingRow
{
    public int OrderId { get; init; }
    public DateTime PackingDate { get; init; }
    public string MONumber { get; init; } = "";
    public string LevelName { get; init; } = "";
    public decimal Units { get; init; }
    public decimal Pieces { get; init; }
    public string WarehouseName { get; init; } = "";
}

public class PackingSectionViewModel : SectionViewModel
{
    private ProductionOrderRow? _order;
    private ItemPackagingLevel? _level;
    private decimal _units;
    private Data.ProjectDb.Entities.Warehouse? _warehouse;

    public PackingSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Production, "أوامر التعبئة", Icons.Layers, "#0EA5E9", "تحويل الناتج المعتمد إلى كراتين/شرنك في مخزن المنتج التام")
    {
        PackCommand = new AsyncRelayCommand(PackAsync);
        PrintCommand = new AsyncRelayCommand(p => p is PackingRow r ? PrintAsync(db => DocumentReports.PackingAsync(Session, db, r.OrderId)) : Task.CompletedTask);
    }

    protected override bool ReloadOnActivate => true;
    public ObservableCollection<ProductionOrderRow> ReadyOrders { get; } = new();
    public ObservableCollection<ItemPackagingLevel> Levels { get; } = new();
    public ObservableCollection<Data.ProjectDb.Entities.Warehouse> Warehouses { get; } = new();
    public ObservableCollection<PackingRow> History { get; } = new();

    public ProductionOrderRow? Order { get => _order; set { if (SetProperty(ref _order, value)) { OnPropertyChanged(nameof(RemainingText)); Background(LoadLevelsAsync()); } } }
    public ItemPackagingLevel? Level { get => _level; set { if (SetProperty(ref _level, value)) OnPropertyChanged(nameof(PiecesText)); } }
    public decimal Units { get => _units; set { if (SetProperty(ref _units, value)) OnPropertyChanged(nameof(PiecesText)); } }
    public Data.ProjectDb.Entities.Warehouse? Warehouse { get => _warehouse; set => SetProperty(ref _warehouse, value); }
    public string RemainingText => Order is null ? "" : $"المطلوب {Order.QuantityToProduce:N0} — المعبّأ {Order.PackedQuantity:N0} — المتبقي {Order.QuantityToProduce - Order.PackedQuantity:N0} قطعة";
    public string PiecesText => Level is null ? "" : $"= {Units * Level.EquivalentBaseUnits:N0} قطعة";
    public AsyncRelayCommand PackCommand { get; }
    public AsyncRelayCommand PrintCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var orderId = Order?.Id;
        ReadyOrders.Clear();
        foreach (var o in (await new ProductionService(db).GetOrdersAsync())
                     .Where(o => o.Status == ProductionOrderStatus.InProgress && o.LastQc == QCOverallResult.Passed)) ReadyOrders.Add(o);
        if (Warehouses.Count == 0)
        {
            var candidates = await db.Warehouses.AsNoTracking()
                .Where(w => w.IsActive && w.IsSellableStock && w.WarehouseType != WarehouseType.RepVan).ToListAsync();
            foreach (var w in candidates.OrderBy(w => w.WarehouseType != WarehouseType.FinishedGoods).ThenBy(w => w.Name)) Warehouses.Add(w);
            Warehouse = Warehouses.FirstOrDefault();
        }
        _order = ReadyOrders.FirstOrDefault(o => o.Id == orderId) ?? ReadyOrders.FirstOrDefault();
        OnPropertyChanged(nameof(Order));
        OnPropertyChanged(nameof(RemainingText));
        await LoadLevelsAsync();

        var rows = await db.PackingOrders.AsNoTracking().OrderByDescending(p => p.Id).Take(100)
            .Select(p => new PackingRow
            {
                OrderId = p.ProductionOrderId, PackingDate = p.PackingDate, MONumber = p.ProductionOrder.MONumber, LevelName = p.PackagingLevel.LevelName, Units = p.UnitsPackaged,
                Pieces = p.UnitsPackaged * p.PackagingLevel.EquivalentBaseUnits, WarehouseName = p.ResultingFinishedGoodsWarehouse.Name
            }).ToListAsync();
        History.Clear();
        foreach (var r in rows) History.Add(r);
    }

    private async Task LoadLevelsAsync()
    {
        Levels.Clear();
        if (Order is null) return;
        await using var db = Session.NewDb();
        var itemId = await db.ProductionOrders.Where(o => o.Id == Order.Id).Select(o => o.FinishedItemId).FirstAsync();
        foreach (var l in await db.ItemPackagingLevels.AsNoTracking().Where(l => l.ItemId == itemId).OrderByDescending(l => l.EquivalentBaseUnits).ToListAsync())
            Levels.Add(l);
        Level = Levels.FirstOrDefault();
    }

    private async Task PackAsync()
    {
        if (!Require(CanAdd || CanEdit, "التعبئة")) return;
        if (Order is null || Level is null || Warehouse is null) { Dialogs.Error("اختر الأمر ووحدة التعبئة والمخزن"); return; }
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new ProductionService(db).PackAsync(Order.Id, Level.Id, Units, Warehouse.Id, Session.UserId),
                                    $"تمت تعبئة {Units:N0} {Level.LevelName} ودخولها {Warehouse.Name}"))
        {
            Units = 0;
            await LoadAsync();
        }
    }
}

// ============================ اختبارات الجودة ============================
public class QualityTestsSectionViewModel : CrudSectionViewModel<QualityTest>
{
    public QualityTestsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Production, "اختبارات الجودة", Icons.List, "#EC4899", "تعريف الاختبارات وحدودها المقبولة") { }

    public ObservableCollection<Item> ItemsLookup { get; } = new();

    protected override int GetId(QualityTest e) => e.Id;
    protected override string Describe(QualityTest e) => e.TestName;

    protected override async Task LoadLookupsAsync(ProjectDbContext db)
    {
        ItemsLookup.Clear();
        foreach (var i in await db.Items.AsNoTracking().Where(i => i.IsActive && i.SourcingMethod != SourcingMethod.Purchased).OrderBy(i => i.ItemName).ToListAsync())
            ItemsLookup.Add(i);
    }

    protected override Task<List<QualityTest>> QueryAsync(ProjectDbContext db) =>
        db.QualityTests.AsNoTracking().Include(t => t.ApplicableItem).OrderBy(t => t.TestName).ToListAsync();

    protected override string? Validate(QualityTest e)
    {
        if (string.IsNullOrWhiteSpace(e.TestName)) return "أدخل اسم الاختبار";
        if (e.StandardMin is null && e.StandardMax is null && string.IsNullOrWhiteSpace(e.StandardText))
            return "حدّد حدًا أدنى/أعلى للاختبار الرقمي، أو النتيجة المقبولة للاختبار الوصفي";
        if (e.StandardMin > e.StandardMax) return "الحد الأدنى أكبر من الأعلى";
        return null;
    }
}

// ============================ الوصفات المخصصة ============================
public class RecipeLineRow
{
    public int Id { get; init; }
    public string ComponentName { get; init; } = "";
    public string ComponentLabel { get; init; } = "";
    public decimal QuantityPerUnit { get; init; }
    public string ReplacesName { get; init; } = "";
}

public class CustomRecipesSectionViewModel : CrudSectionViewModel<CustomRecipe>
{
    private CustomRecipe? _selectedRecipe;
    private Item? _newComponent;
    private string _newLabel = "";
    private decimal _newQuantity = 1;
    private Item? _newReplaces;

    public CustomRecipesSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Production, "الوصفات المخصصة", Icons.Recipe, "#F59E0B", "وصفة باسم تجاري لعميل (غطاء/لاصق خاص يستبدل الأساسي)")
    {
        AddLineCommand = new AsyncRelayCommand(AddLineAsync);
        DeleteLineCommand = new AsyncRelayCommand(p => p is RecipeLineRow r ? DeleteLineAsync(r) : Task.CompletedTask);
    }

    public ObservableCollection<Item> FinishedItems { get; } = new();
    public ObservableCollection<Item> RawItems { get; } = new();
    public ObservableCollection<Customer> Customers { get; } = new();
    public ObservableCollection<RecipeLineRow> Lines { get; } = new();

    public CustomRecipe? SelectedRecipe { get => _selectedRecipe; set { if (SetProperty(ref _selectedRecipe, value)) Background(LoadLinesAsync()); } }
    public Item? NewComponent { get => _newComponent; set => SetProperty(ref _newComponent, value); }
    public string NewLabel { get => _newLabel; set => SetProperty(ref _newLabel, value); }
    public decimal NewQuantity { get => _newQuantity; set => SetProperty(ref _newQuantity, value); }
    public Item? NewReplaces { get => _newReplaces; set => SetProperty(ref _newReplaces, value); }
    public AsyncRelayCommand AddLineCommand { get; }
    public AsyncRelayCommand DeleteLineCommand { get; }

    protected override int GetId(CustomRecipe e) => e.Id;
    protected override string Describe(CustomRecipe e) => e.Name;

    protected override async Task LoadLookupsAsync(ProjectDbContext db)
    {
        FinishedItems.Clear();
        var withBom = db.BillOfMaterials.Where(b => b.IsActive).Select(b => b.FinishedItemId);
        foreach (var i in await db.Items.AsNoTracking().Where(i => withBom.Contains(i.Id)).OrderBy(i => i.ItemName).ToListAsync()) FinishedItems.Add(i);
        RawItems.Clear();
        foreach (var i in await db.Items.AsNoTracking().Where(i => i.IsActive && i.SourcingMethod != SourcingMethod.Manufactured).OrderBy(i => i.ItemName).ToListAsync())
            RawItems.Add(i);
        Customers.Clear();
        foreach (var c in await db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).ToListAsync()) Customers.Add(c);
    }

    protected override Task<List<CustomRecipe>> QueryAsync(ProjectDbContext db) =>
        db.CustomRecipes.AsNoTracking().Include(r => r.FinishedItem).Include(r => r.Customer).OrderBy(r => r.Name).ToListAsync();

    protected override string? Validate(CustomRecipe e)
    {
        if (string.IsNullOrWhiteSpace(e.Name)) return "أدخل اسم الوصفة";
        if (e.FinishedItemId == 0) return "اختر المنتج (يجب أن تكون له قائمة مواد)";
        if (e.CustomerId == 0) return "اختر العميل صاحب الاسم التجاري";
        return null;
    }

    private async Task LoadLinesAsync()
    {
        Lines.Clear();
        if (SelectedRecipe is null) return;
        await using var db = Session.NewDb();
        foreach (var l in await db.CustomRecipeLines.AsNoTracking().Where(l => l.CustomRecipeId == SelectedRecipe.Id)
                     .Select(l => new RecipeLineRow
                     {
                         Id = l.Id, ComponentName = l.ComponentItem.ItemName, ComponentLabel = l.ComponentLabel, QuantityPerUnit = l.QuantityPerUnit,
                         ReplacesName = l.ReplacesRawMaterialItem != null ? l.ReplacesRawMaterialItem.ItemName : "— مكوّن إضافي —"
                     }).ToListAsync())
            Lines.Add(l);
    }

    private async Task AddLineAsync()
    {
        if (!Require(CanEdit, "تعديل الوصفات")) return;
        if (SelectedRecipe is null) { Dialogs.Error("اختر الوصفة من الجدول أولًا"); return; }
        if (NewComponent is null || string.IsNullOrWhiteSpace(NewLabel) || NewQuantity <= 0)
        { Dialogs.Error("اختر المكوّن واكتب وصفه (مثل: لاصق أمامي) وكمية أكبر من صفر"); return; }
        await using var db = Session.NewDb();
        db.CustomRecipeLines.Add(new CustomRecipeLine { CustomRecipeId = SelectedRecipe.Id, ComponentItemId = NewComponent.Id, ComponentLabel = NewLabel.Trim(),
                                                        QuantityPerUnit = NewQuantity, ReplacesRawMaterialItemId = NewReplaces?.Id });
        await db.SaveChangesAsync();
        NewComponent = null;
        NewReplaces = null;
        NewLabel = "";
        NewQuantity = 1;
        await LoadLinesAsync();
    }

    private async Task DeleteLineAsync(RecipeLineRow row)
    {
        if (!Require(CanDelete, "الحذف")) return;
        await using var db = Session.NewDb();
        await db.CustomRecipeLines.Where(l => l.Id == row.Id).ExecuteDeleteAsync();
        await LoadLinesAsync();
    }
}
