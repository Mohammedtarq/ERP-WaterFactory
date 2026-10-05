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
        Daily = Add(new DailyProductionSectionViewModel(s, d));
        Add(new ProductionMonthSectionViewModel(s, d));
        Add(new VariantStockSectionViewModel(s, d));
        Add(new PendingProductionSectionViewModel(s, d));
        Orders = Add(new ProductionOrdersSectionViewModel(s, d));
        Qc = Add(new QcSectionViewModel(s, d));
        Packing = Add(new PackingSectionViewModel(s, d));
        Wip = Add(new MachineWipSectionViewModel(s, d));
        Machines = Add(new MachinesSectionViewModel(s, d));
        Add(new QualityTestsSectionViewModel(s, d));
        Add(new CustomRecipesSectionViewModel(s, d));
    }

    public DailyProductionSectionViewModel Daily { get; }
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
    public string UsedBy { get; init; } = "";
    public bool IsShared { get; init; }
    public bool IsSufficient => Available >= Required;
}

/// <summary>مكوّن في سطر أمر (للاستبدال): المادة وكميتها المطلوبة.</summary>
public record ComponentOption(int ItemId, string Name, decimal Quantity)
{
    public string Display => $"{Name} — {Quantity:N0}";
}

/// <summary>صنف مضاف لأمر إنتاج قيد الإنشاء.</summary>
public class OrderLineDraft
{
    public int FinishedItemId { get; init; }
    public string ItemName { get; init; } = "";
    public int? RecipeId { get; init; }
    public string? RecipeName { get; init; }
    public decimal Quantity { get; init; }
    public string BatchNumber { get; init; } = "";
    public ProductionLineInput ToInput() => new(FinishedItemId, Quantity, RecipeId, BatchNumber);
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
            ComposeLines.Clear();
            await using var db = Session.NewDb();
            BatchNumber = await new ProductionService(db).NextBatchNumberAsync();
            IsComposing = true;
        });
        EditBatchCommand = new AsyncRelayCommand(p => p is ProductionOrderRow r ? OpenBatchAsync(r) : Task.CompletedTask);
        SaveBatchCommand = new AsyncRelayCommand(SaveBatchAsync);
        CloseBatchCommand = new RelayCommand(() => BatchOrder = null);
        OverrideCommand = new AsyncRelayCommand(p => p is ProductionOrderRow r ? OpenOverrideAsync(r) : Task.CompletedTask);
        SaveOverrideCommand = new AsyncRelayCommand(SaveOverrideAsync);
        CloseOverrideCommand = new RelayCommand(() => OverrideOrder = null);
        CancelComposeCommand = new RelayCommand(() => IsComposing = false);
        AddLineCommand = new AsyncRelayCommand(AddLineAsync);
        RemoveLineCommand = new AsyncRelayCommand(async p => { if (p is OrderLineDraft l) { ComposeLines.Remove(l); await PreviewAsync(); } });
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
    public AsyncRelayCommand AddLineCommand { get; }
    public AsyncRelayCommand RemoveLineCommand { get; }
    /// <summary>أصناف الأمر المضافة (أمر متعدد الأصناف). الصنف المختار حاليًا يُضاف تلقائيًا عند الإنشاء.</summary>
    public ObservableCollection<OrderLineDraft> ComposeLines { get; } = new();
    public ObservableCollection<ProductionLineRow> BatchLines { get; } = new();
    private ProductionLineRow? _batchLine;
    public ProductionLineRow? BatchLine
    {
        get => _batchLine;
        set { if (SetProperty(ref _batchLine, value)) NewBatchNumber = value?.OutputBatch ?? ""; }
    }
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
        private set { if (SetProperty(ref _batchOrder, value)) { OnPropertyChanged(nameof(IsEditingBatch)); if (value is not null) { IsComposing = false; OverrideOrder = null; } } }
    }
    public bool IsEditingBatch => BatchOrder is not null;
    public string NewBatchNumber { get => _newBatchNumber; set => SetProperty(ref _newBatchNumber, value); }
    public string BatchReason { get => _batchReason; set => SetProperty(ref _batchReason, value); }
    public ObservableCollection<string> BatchHistory { get; } = new();
    protected override bool HasPendingInput => IsComposing || IsEditingBatch || IsOverriding;

    // ---------------- استبدال مكوّن في أمر واحد ----------------
    private ProductionOrderRow? _overrideOrder;
    private ProductionLineRow? _overrideLine;
    private ComponentOption? _overrideOriginal;
    private Item? _overrideReplacement;
    private string _overrideReason = "";
    public AsyncRelayCommand OverrideCommand { get; }
    public AsyncRelayCommand SaveOverrideCommand { get; }
    public RelayCommand CloseOverrideCommand { get; }
    public ProductionOrderRow? OverrideOrder
    {
        get => _overrideOrder;
        private set
        {
            if (!SetProperty(ref _overrideOrder, value)) return;
            OnPropertyChanged(nameof(IsOverriding));
            if (value is not null) { IsComposing = false; BatchOrder = null; }
        }
    }
    public bool IsOverriding => OverrideOrder is not null;
    public ObservableCollection<ProductionLineRow> OverrideLines { get; } = new();
    public ObservableCollection<ComponentOption> OverrideComponents { get; } = new();
    public ObservableCollection<Item> ReplacementItems { get; } = new();
    public ObservableCollection<string> OverrideHistory { get; } = new();
    public ProductionLineRow? OverrideLine { get => _overrideLine; set { if (SetProperty(ref _overrideLine, value)) Background(LoadOverrideComponentsAsync()); } }
    public ComponentOption? OverrideOriginal { get => _overrideOriginal; set => SetProperty(ref _overrideOriginal, value); }
    public Item? OverrideReplacement { get => _overrideReplacement; set => SetProperty(ref _overrideReplacement, value); }
    public string OverrideReason { get => _overrideReason; set => SetProperty(ref _overrideReason, value); }

    private async Task OpenOverrideAsync(ProductionOrderRow row)
    {
        if (!Require(CanEdit, "استبدال مكوّن في أمر الإنتاج")) return;
        if (row.Status != ProductionOrderStatus.Draft) { Dialogs.Error("الاستبدال يكون قبل بدء التشغيل (الأمر مسودة)"); return; }
        OverrideOrder = row;
        OverrideReason = "";
        OverrideReplacement = null;
        await using var db = Session.NewDb();
        if (ReplacementItems.Count == 0)
            foreach (var i in await db.Items.AsNoTracking().Where(i => i.IsActive && i.SourcingMethod != SourcingMethod.Manufactured).OrderBy(i => i.ItemName).ToListAsync())
                ReplacementItems.Add(i);
        OverrideLines.Clear();
        foreach (var l in row.Lines) OverrideLines.Add(l);
        // تعيين الحقل مباشرة ثم تحميل واحد (الضبط عبر الخاصية يحمّل في الخلفية أيضًا فيتكرر)
        _overrideLine = OverrideLines.FirstOrDefault();
        OnPropertyChanged(nameof(OverrideLine));
        await LoadOverrideComponentsAsync();
        await LoadOverrideHistoryAsync(db, row.Id);
    }

    private async Task LoadOverrideComponentsAsync()
    {
        OverrideComponents.Clear();
        if (OverrideLine is null) return;
        await using var db = Session.NewDb();
        var lineId = OverrideLine.LineId;
        foreach (var c in await db.ProductionOrderConsumptions.AsNoTracking().Where(c => c.ProductionOrderLineId == lineId)
                     .OrderBy(c => c.RawMaterialItem.ItemName)
                     .Select(c => new ComponentOption(c.RawMaterialItemId, c.RawMaterialItem.ItemName, c.QuantityRequired)).ToListAsync())
            OverrideComponents.Add(c);
        OverrideOriginal = OverrideComponents.FirstOrDefault();
    }

    private async Task LoadOverrideHistoryAsync(Data.ProjectDb.ProjectDbContext db, int orderId)
    {
        OverrideHistory.Clear();
        foreach (var o in await new PackagingTemplateService(db).GetOrderOverridesAsync(orderId))
            OverrideHistory.Add($"{o.ChangedAt.ToLocalTime():yyyy/MM/dd HH:mm} — {o.ChangedByUser.Username}: {o.Line.FinishedItem.ItemName}: {o.OriginalItem.ItemName} ← {o.ReplacementItem.ItemName} ({o.Quantity:N0}) — {o.Reason}");
    }

    private async Task SaveOverrideAsync()
    {
        if (OverrideOrder is null || !Require(CanEdit, "استبدال مكوّن في أمر الإنتاج")) return;
        if (OverrideLine is null || OverrideOriginal is null || OverrideReplacement is null) { Dialogs.Error("اختر الصنف والمكوّن الأصلي والبديل"); return; }
        await using var db = Session.NewDb();
        var (lineId, original, replacement) = (OverrideLine.LineId, OverrideOriginal.ItemId, OverrideReplacement.Id);
        if (await RunOperationAsync(() => new PackagingTemplateService(db).OverrideOrderComponentAsync(lineId, original, replacement, OverrideReason, Session.UserId),
                                    $"استُبدل {OverrideOriginal.Name} بـ {OverrideReplacement.ItemName} في {OverrideOrder.MONumber} فقط"))
        {
            OverrideOrder = null;
            await LoadAsync();
        }
    }
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
        // المنتجات ذات قائمة المواد تُحدَّث في كل تحميل (قد تُنشأ قائمة مواد جديدة من المخازن أو بقالب تعبئة)
        var withBom = db.BillOfMaterials.Where(b => b.IsActive).Select(b => b.FinishedItemId);
        var finished = await db.Items.AsNoTracking().Where(i => withBom.Contains(i.Id)).OrderBy(i => i.ItemName).ToListAsync();
        if (!finished.Select(i => i.Id).SequenceEqual(FinishedItems.Select(i => i.Id)))
        {
            var selectedId = FinishedItem?.Id;
            FinishedItems.Clear();
            foreach (var i in finished) FinishedItems.Add(i);
            _finishedItem = FinishedItems.FirstOrDefault(i => i.Id == selectedId);
            OnPropertyChanged(nameof(FinishedItem));
        }
        if (RawWarehouses.Count == 0)
        {
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

    /// <summary>أصناف الأمر: المضافة + الصنف المختار حاليًا (إن وُجد).</summary>
    private List<ProductionLineInput> CollectLines()
    {
        var lines = ComposeLines.Select(l => l.ToInput()).ToList();
        if (FinishedItem is not null && Quantity > 0 && lines.All(l => l.FinishedItemId != FinishedItem.Id))
            lines.Add(new ProductionLineInput(FinishedItem.Id, Quantity, Recipe?.Id, BatchNumber));
        return lines;
    }

    /// <summary>يثبّت الصنف المختار كسطر في الأمر ويجهّز إدخال صنف آخر برقم الدفعة التالي.</summary>
    private async Task AddLineAsync()
    {
        if (FinishedItem is null || Quantity <= 0) { Dialogs.Error("اختر المنتج وأدخل كمية أكبر من صفر"); return; }
        if (ComposeLines.Any(l => l.FinishedItemId == FinishedItem.Id)) { Dialogs.Error("الصنف مضاف للأمر مسبقًا"); return; }
        if (!string.IsNullOrWhiteSpace(BatchNumber) && ComposeLines.Any(l => l.BatchNumber == BatchNumber.Trim()))
        { Dialogs.Error("رقم الدفعة مستخدم لصنف آخر في الأمر"); return; }
        ComposeLines.Add(new OrderLineDraft
        {
            FinishedItemId = FinishedItem.Id, ItemName = FinishedItem.ItemName, RecipeId = Recipe?.Id, RecipeName = Recipe?.Name,
            Quantity = Quantity, BatchNumber = BatchNumber.Trim()
        });
        BatchNumber = NextSuggested(BatchNumber);
        FinishedItem = null;
        await PreviewAsync();
    }

    /// <summary>B261002-004 ← B261002-005 (أو فارغ = تلقائي إن لم يكن بنمط التسلسل).</summary>
    public static string NextSuggested(string current)
    {
        var dash = current.LastIndexOf('-');
        if (dash < 0 || !int.TryParse(current[(dash + 1)..], out var n)) return "";
        return $"{current[..(dash + 1)]}{(n + 1).ToString(new string('0', current.Length - dash - 1))}";
    }

    /// <summary>معاينة المواد المطلوبة لكل أصناف الأمر (المشتركة مجمَّعة) مقابل المتاح قبل إنشاء الأمر.</summary>
    private async Task PreviewAsync()
    {
        Preview.Clear();
        var lines = CollectLines();
        if (lines.Count == 0 || RawWarehouse is null) { OnPropertyChanged(nameof(AllSufficient)); return; }
        await using var db = Session.NewDb();
        if (FinishedItem is not null)
        {
            var (_, error) = await new ProductionService(db).MergeRecipeAsync(FinishedItem.Id, Recipe?.Id);
            if (error is not null) { StatusMessage = error; OnPropertyChanged(nameof(AllSufficient)); return; }
        }
        // نفس محرك التوفر المستخدم في "احتياجات التصنيع" وفي بدء التشغيل
        foreach (var r in await new ManufacturingRequirementService(db).CalculateForLinesAsync(lines, RawWarehouse.Id))
            Preview.Add(new RequirementPreview { RawMaterialName = r.RawMaterialName, Required = r.QuantityRequired, Available = r.QuantityAvailable,
                                                 WhereText = r.WhereText, UsedBy = r.UsedBy, IsShared = r.IsShared });
        OnPropertyChanged(nameof(AllSufficient));
    }

    private async Task CreateAsync()
    {
        var lines = CollectLines();
        if (lines.Count == 0 || RawWarehouse is null) { Dialogs.Error("اختر المنتج ومخزن المواد الأولية"); return; }
        if (Machine is null) { Dialogs.Error("اختر الماكينة (أضفها من تبويب الماكينات إن لم توجد)"); return; }
        await using var db = Session.NewDb();
        if (await RunOperationAsync(async () => (await new ProductionService(db).CreateOrderAsync(lines, RawWarehouse.Id, Machine.Id, Session.UserId)).result,
                                    lines.Count > 1 ? $"تم إنشاء أمر إنتاج بـ {lines.Count} أصناف — ابدأ تشغيله من الجدول" : "تم إنشاء أمر الإنتاج — ابدأ تشغيله من الجدول"))
        {
            IsComposing = false;
            ComposeLines.Clear();
            await LoadAsync();
        }
    }

    private async Task OpenBatchAsync(ProductionOrderRow row)
    {
        if (!Require(CanEdit, "تعديل رقم الدفعة")) return;
        BatchOrder = row;
        BatchLines.Clear();
        foreach (var l in row.Lines) BatchLines.Add(l);
        BatchLine = BatchLines.FirstOrDefault();
        NewBatchNumber = BatchLine?.OutputBatch ?? "";
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
        if (BatchLine is null) { Dialogs.Error("اختر الصنف"); return; }
        var lineId = BatchLine.LineId;
        if (await RunOperationAsync(() => new ProductionService(db).ChangeLineBatchNumberAsync(lineId, NewBatchNumber, BatchReason, Session.UserId),
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
    private ProductionLineRow? _order;
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
    public ObservableCollection<ProductionLineRow> InProgressOrders { get; } = new();
    public ObservableCollection<QcLine> Lines { get; } = new();
    public ObservableCollection<QcHistoryRow> History { get; } = new();

    public ProductionLineRow? Order { get => _order; set { if (SetProperty(ref _order, value)) Background(LoadTestsAsync()); } }
    public string? LastResult { get => _lastResult; private set => SetProperty(ref _lastResult, value); }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand PrintCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var lineId = Order?.LineId;
        InProgressOrders.Clear();
        // كل دفعة (صنف) في أمر قيد التشغيل لم تُعبّأ بالكامل بعد
        foreach (var o in (await new ProductionService(db).GetLinesAsync()).Where(o => o.Status == ProductionOrderStatus.InProgress && o.PackedQuantity < o.QuantityToProduce))
            InProgressOrders.Add(o);
        _order = InProgressOrders.FirstOrDefault(o => o.LineId == lineId) ?? InProgressOrders.FirstOrDefault();
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
        foreach (var t in await new ProductionService(db).GetApplicableTestsAsync(Order.FinishedItemId))
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
    private ProductionLineRow? _order;
    private ItemPackagingLevel? _level;
    private decimal _units;
    private Data.ProjectDb.Entities.Warehouse? _warehouse;
    private int _levelsLoad;

    public PackingSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Production, "أوامر التعبئة", Icons.Layers, "#0EA5E9", "تحويل الناتج المعتمد إلى كراتين/شرنك في مخزن المنتج التام")
    {
        PackCommand = new AsyncRelayCommand(PackAsync);
        PrintCommand = new AsyncRelayCommand(p => p is PackingRow r ? PrintAsync(db => DocumentReports.PackingAsync(Session, db, r.OrderId)) : Task.CompletedTask);
    }

    protected override bool ReloadOnActivate => true;
    public ObservableCollection<ProductionLineRow> ReadyOrders { get; } = new();
    public ObservableCollection<ItemPackagingLevel> Levels { get; } = new();
    public ObservableCollection<Data.ProjectDb.Entities.Warehouse> Warehouses { get; } = new();
    public ObservableCollection<PackingRow> History { get; } = new();

    public ProductionLineRow? Order { get => _order; set { if (SetProperty(ref _order, value)) { OnPropertyChanged(nameof(RemainingText)); Background(LoadLevelsAsync()); } } }
    public ItemPackagingLevel? Level
    {
        get => _level;
        set
        {
            if (!SetProperty(ref _level, value)) return;
            OnPropertyChanged(nameof(PiecesText));
            OnPropertyChanged(nameof(RemainingText));
            Units = DefaultUnits();
        }
    }
    public decimal Units { get => _units; set { if (SetProperty(ref _units, value)) OnPropertyChanged(nameof(PiecesText)); } }
    private decimal RemainingPieces => Order is null ? 0 : Order.QuantityToProduce - Order.PackedQuantity;
    /// <summary>الافتراضي: كل المتبقي بالوحدة المختارة (العدد الصحيح منها).</summary>
    private decimal DefaultUnits() => Level is { EquivalentBaseUnits: > 0 } l ? Math.Floor(RemainingPieces / l.EquivalentBaseUnits) : 0;
    public Data.ProjectDb.Entities.Warehouse? Warehouse { get => _warehouse; set => SetProperty(ref _warehouse, value); }
    public string RemainingText => Order is null ? "" : $"المطلوب {Order.QuantityToProduce:N0} — المعبّأ {Order.PackedQuantity:N0} — المتبقي {RemainingPieces:N0} قطعة"
        + (Level is { EquivalentBaseUnits: > 1 } l ? $" (= {Math.Floor(RemainingPieces / l.EquivalentBaseUnits):N0} {l.LevelName}"
           + (RemainingPieces % l.EquivalentBaseUnits is var rest and > 0 ? $" + {rest:N0} قطعة)" : ")") : "");
    public string PiecesText => Level is null ? "" : $"= {Units * Level.EquivalentBaseUnits:N0} قطعة";
    public AsyncRelayCommand PackCommand { get; }
    public AsyncRelayCommand PrintCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var lineId = Order?.LineId;
        ReadyOrders.Clear();
        // كل دفعة (صنف) ناجحة بالمختبر ولم تُعبّأ كاملة
        foreach (var o in (await new ProductionService(db).GetLinesAsync())
                     .Where(o => o.Status == ProductionOrderStatus.InProgress && o.LastQc == QCOverallResult.Passed && o.PackedQuantity < o.QuantityToProduce)) ReadyOrders.Add(o);
        if (Warehouses.Count == 0)
        {
            var candidates = await db.Warehouses.AsNoTracking()
                .Where(w => w.IsActive && w.IsSellableStock && w.WarehouseType != WarehouseType.RepVan).ToListAsync();
            foreach (var w in candidates.OrderBy(w => w.WarehouseType != WarehouseType.FinishedGoods).ThenBy(w => w.Name)) Warehouses.Add(w);
            Warehouse = Warehouses.FirstOrDefault();
        }
        _order = ReadyOrders.FirstOrDefault(o => o.LineId == lineId) ?? ReadyOrders.FirstOrDefault();
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

    /// <summary>
    /// قد يُطلب التحميل مرتين متزامنتين (فتح الشاشة + إعادة تعبئة قائمة الأوامر)؛ يُعتمد آخر طلب فقط حتى لا تعرض القائمة
    /// وحدة («قطعة») بينما الحساب يجري بوحدة أخرى («شرنك»). ويُحتفظ باختيار المستخدم إن بقي متاحًا.
    /// </summary>
    private async Task LoadLevelsAsync()
    {
        var token = ++_levelsLoad;
        var keepId = Level?.Id;
        var itemId = Order?.FinishedItemId;
        List<ItemPackagingLevel> levels = new();
        if (itemId is not null)
        {
            await using var db = Session.NewDb();
            levels = await db.ItemPackagingLevels.AsNoTracking().Where(l => l.ItemId == itemId).OrderByDescending(l => l.EquivalentBaseUnits).ToListAsync();
        }
        if (token != _levelsLoad) return;           // طلب أحدث سيتولى التعبئة
        Levels.Clear();
        foreach (var l in levels) Levels.Add(l);
        _level = null;
        Level = Levels.FirstOrDefault(l => l.Id == keepId) ?? Levels.FirstOrDefault();
    }

    private async Task PackAsync()
    {
        if (!Require(CanAdd || CanEdit, "التعبئة")) return;
        if (Order is null || Level is null || Warehouse is null) { Dialogs.Error("اختر الأمر ووحدة التعبئة والمخزن"); return; }
        if (Units <= 0) { Dialogs.Error("عدد الوحدات يجب أن يكون أكبر من صفر"); return; }
        if (Units * Level.EquivalentBaseUnits > RemainingPieces)
        {
            Dialogs.Error($"{Units:N0} {Level.LevelName} = {Units * Level.EquivalentBaseUnits:N0} قطعة، والمتبقي من الأمر {RemainingPieces:N0} قطعة فقط"
                          + (Level.EquivalentBaseUnits > 1 ? $" (أي {Math.Floor(RemainingPieces / Level.EquivalentBaseUnits):N0} {Level.LevelName} كحد أقصى)." : "."));
            return;
        }
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new ProductionService(db).PackAsync(Order.OrderId, Level.Id, Units, Warehouse.Id, Session.UserId),
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

/// <summary>دور في معالج المتغير: يبقى أساسيًا، أو يُستبدل بصنف موجود، أو بصنف جديد يُنشأ باسمه.</summary>
public class VariantRoleRow : ObservableObject
{
    private Item? _choice;
    private bool _createNew;
    private string _newItemName = "";
    public VariantRoleRow(string role, string baseName) { Role = role; BaseName = baseName; }
    public string Role { get; }
    public string BaseName { get; }
    public Item? Choice { get => _choice; set { if (SetProperty(ref _choice, value) && value is { Id: > 0 }) CreateNew = false; } }
    public bool CreateNew { get => _createNew; set { if (SetProperty(ref _createNew, value) && value) Choice = CustomRecipesSectionViewModel.KeepBase; } }
    public string NewItemName { get => _newItemName; set => SetProperty(ref _newItemName, value); }

    /// <summary>اسم مقترح للصنف الجديد: «ليبل مطعم الحسون».</summary>
    internal void SuggestName(string variant)
    {
        if (!string.IsNullOrWhiteSpace(variant)) NewItemName = $"{Role} {variant.Trim()}";
    }
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
        SetVariantCommand = new AsyncRelayCommand(SetVariantAsync);
        OpenWizardCommand = new RelayCommand(OpenWizard);
        CloseWizardCommand = new RelayCommand(() => IsWizardOpen = false);
        CreateVariantCommand = new AsyncRelayCommand(CreateVariantAsync);
    }

    // ---------------- متغير جديد بخطوة واحدة ----------------
    /// <summary>خيار «بلا عميل» في المعالج: ملصق مناسبة يُباع لمن يطلبه.</summary>
    public static readonly Customer NoCustomer = new() { Id = 0, Name = "— مناسبة عامة (بلا عميل) —" };
    /// <summary>خيار «بلا تغيير» لدور يبقى على مادته الأساسية.</summary>
    public static readonly Item KeepBase = new() { Id = 0, ItemName = "— بلا تغيير —" };

    private bool _isWizardOpen;
    private Item? _wizardProduct;
    private Customer? _wizardCustomer;
    private string _wizardName = "";
    public bool IsWizardOpen { get => _isWizardOpen; set => SetProperty(ref _isWizardOpen, value); }
    public ObservableCollection<Customer> WizardCustomers { get; } = new();
    public ObservableCollection<Item> WizardItems { get; } = new();
    public ObservableCollection<VariantRoleRow> WizardRoles { get; } = new();
    public Item? WizardProduct { get => _wizardProduct; set { if (SetProperty(ref _wizardProduct, value)) Background(LoadWizardRolesAsync()); } }
    public Customer? WizardCustomer
    {
        get => _wizardCustomer;
        set
        {
            if (!SetProperty(ref _wizardCustomer, value)) return;
            if (value is { Id: > 0 } c) WizardName = c.Name;
        }
    }
    public string WizardName
    {
        get => _wizardName;
        set
        {
            if (!SetProperty(ref _wizardName, value)) return;
            foreach (var r in WizardRoles.Where(r => r.NewItemName.Length == 0 || r.NewItemName.StartsWith(r.Role + " "))) r.SuggestName(value);
        }
    }
    public RelayCommand OpenWizardCommand { get; }
    public RelayCommand CloseWizardCommand { get; }
    public AsyncRelayCommand CreateVariantCommand { get; }

    private void OpenWizard()
    {
        if (!Require(CanAdd, "إضافة متغير")) return;
        Editor = null;   // لوحة واحدة في الجانب
        WizardCustomers.Clear();
        WizardCustomers.Add(NoCustomer);
        foreach (var c in Customers) WizardCustomers.Add(c);
        WizardItems.Clear();
        WizardItems.Add(KeepBase);
        foreach (var i in RawItems) WizardItems.Add(i);
        WizardName = "";
        WizardCustomer = NoCustomer;
        WizardProduct = FinishedItems.Count == 1 ? FinishedItems[0] : null;
        IsWizardOpen = true;
    }

    private async Task LoadWizardRolesAsync()
    {
        WizardRoles.Clear();
        if (WizardProduct is null) return;
        await using var db = Session.NewDb();
        foreach (var r in await new PackagingTemplateService(db).GetRolesAsync(WizardProduct.Id))
        {
            var row = new VariantRoleRow(r.ComponentRole!, r.RawMaterialItem.ItemName) { Choice = KeepBase };
            row.SuggestName(WizardName);
            WizardRoles.Add(row);
        }
        if (WizardRoles.Count == 0) StatusMessage = "قائمة مواد المنتج بلا أدوار (غطاء، لاصق) — طبّق قالب التعبئة عليه أولًا";
    }

    private async Task CreateVariantAsync()
    {
        if (!Require(CanAdd, "إضافة متغير")) return;
        if (WizardProduct is null) { Dialogs.Error("اختر المنتج"); return; }
        var roles = WizardRoles.Select(r => new VariantRoleInput(r.Role, r.Choice is { Id: > 0 } i ? i.Id : null, r.CreateNew ? r.NewItemName : null)).ToList();
        await using var db = Session.NewDb();
        var name = WizardName.Trim();
        if (await RunOperationAsync(async () => (await new PackagingTemplateService(db).CreateVariantAsync(new NewVariantRequest(
                WizardProduct.Id, WizardCustomer is { Id: > 0 } c ? c.Id : null, WizardName, roles))).result,
                $"أُضيف المتغير «{name}» — يظهر الآن في إنتاج اليوم والبيع والتحميل"))
        {
            IsWizardOpen = false;
            await LoadAsync();
            SelectedRecipe = Items.FirstOrDefault(r => r.Name == name && r.FinishedItemId == WizardProduct.Id);
        }
    }

    // ---------------- بديل العميل حسب الدور (من قالب التعبئة) ----------------
    private BOMLine? _selectedRole;
    private Item? _variantItem;
    public ObservableCollection<BOMLine> RoleOptions { get; } = new();
    public BOMLine? SelectedRole { get => _selectedRole; set => SetProperty(ref _selectedRole, value); }
    public Item? VariantItem { get => _variantItem; set => SetProperty(ref _variantItem, value); }
    public AsyncRelayCommand SetVariantCommand { get; }

    /// <summary>بديل العميل: يستبدل مكوّن الدور المختار بصنف مخزني خاص بالعميل (بنفس النسبة) في هذه الوصفة فقط.</summary>
    private async Task SetVariantAsync()
    {
        if (!Require(CanEdit, "تعديل الوصفات")) return;
        if (SelectedRecipe is null || SelectedRole?.ComponentRole is null || VariantItem is null)
        { Dialogs.Error("اختر الوصفة من الجدول، ثم الدور، ثم الصنف البديل"); return; }
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new PackagingTemplateService(db).SetCustomerVariantAsync(SelectedRecipe.Id, SelectedRole.ComponentRole, VariantItem.Id),
                                    $"بديل {SelectedRole.ComponentRole} في {SelectedRecipe.Name}: {VariantItem.ItemName}"))
        {
            VariantItem = null;
            await LoadLinesAsync();
        }
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
    protected override void OnEditorChanged() { if (IsEditing) IsWizardOpen = false; }

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
        return null;
    }

    private async Task LoadLinesAsync()
    {
        Lines.Clear();
        RoleOptions.Clear();
        if (SelectedRecipe is null) return;
        await using var db = Session.NewDb();
        foreach (var r in await new PackagingTemplateService(db).GetRolesAsync(SelectedRecipe.FinishedItemId)) RoleOptions.Add(r);
        SelectedRole = RoleOptions.FirstOrDefault();
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
