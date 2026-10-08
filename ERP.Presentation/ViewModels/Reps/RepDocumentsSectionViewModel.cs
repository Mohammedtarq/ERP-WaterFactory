using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Reps;

/// <summary>سطر مستند مندوب قيد الإدخال (في الإرجاع: سليم أو تالف ميداني).</summary>
public class RepLineDraft
{
    public int ItemId { get; init; }
    public string ItemName { get; init; } = "";
    public int PackagingLevelId { get; init; }
    public string LevelName { get; init; } = "";
    public decimal QuantityInLevel { get; init; }
    public decimal BaseUnits { get; init; }
    public bool IsDamaged { get; init; }
    public string StateText => IsDamaged ? "تلف ميداني" : "سليم";
}

// ============================ مستندات المندوبين: إسناد حمولة / إرجاع ============================
public class RepDocumentsSectionViewModel : SectionViewModel
{
    private Option<StockDocumentType> _documentType;
    private Data.ProjectDb.Entities.Warehouse? _van;
    private Data.ProjectDb.Entities.Warehouse? _store;
    private DateTime _date = DateTime.Today;
    private Item? _lineItem;
    private ItemPackagingLevel? _lineLevel;
    private decimal _lineQuantity = 1;
    private bool _lineDamaged;
    private string? _notes;
    private DateTime _from = DateTime.Today.AddDays(-30);
    private DateTime _to = DateTime.Today;
    private PackFormatter _packs = new(Array.Empty<ItemPackagingLevel>());

    public RepDocumentsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Reps, "مستندات المندوبين", Icons.Truck, "#F97316",
               "إسناد حمولة لمندوب (من المنتج التام للسيارة) والإرجاع منه (السليم للمخزن، والتالف الميداني بسببه) — مستندات مرقمة قابلة للطباعة")
    {
        _documentType = DocumentTypes[0];
        AddLineCommand = new RelayCommand(AddLine);
        RemoveLineCommand = new RelayCommand(p => { if (p is RepLineDraft l) Lines.Remove(l); });
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        PrintCommand = new AsyncRelayCommand(p => p is StockDocumentRow r ? PrintDocumentAsync(r.Id) : Task.CompletedTask);
        LoadDocumentsCommand = new AsyncRelayCommand(LoadDocumentsAsync);
        PrintStockCommand = new RelayCommand(PrintVanStock);
    }

    protected override bool ReloadOnActivate => true;
    protected override bool HasPendingInput => Lines.Count > 0;

    protected override void ResetInput()
    {
        Lines.Clear();
        Date = DateTime.Today;
        LineItem = null;
        LineQuantity = 1;
        LineDamaged = false;
        Notes = null;
    }

    public IReadOnlyList<Option<StockDocumentType>> DocumentTypes { get; } = new[]
    {
        new Option<StockDocumentType>(StockDocumentType.RepLoad, ArabicLabels.Of(StockDocumentType.RepLoad)),
        new Option<StockDocumentType>(StockDocumentType.RepReturn, ArabicLabels.Of(StockDocumentType.RepReturn)),
    };
    public ObservableCollection<Data.ProjectDb.Entities.Warehouse> Vans { get; } = new();
    public ObservableCollection<Data.ProjectDb.Entities.Warehouse> Stores { get; } = new();
    public ObservableCollection<Item> ItemsLookup { get; } = new();
    public ObservableCollection<ItemPackagingLevel> LevelOptions { get; } = new();
    public ObservableCollection<RepLineDraft> Lines { get; } = new();
    public ObservableCollection<StockDocumentRow> Documents { get; } = new();
    public ObservableCollection<CurrentStockRow> VanStock { get; } = new();

    public Option<StockDocumentType> DocumentType
    {
        get => _documentType;
        set
        {
            if (!SetProperty(ref _documentType, value)) return;
            OnPropertyChanged(nameof(IsReturn));
            OnPropertyChanged(nameof(StoreLabel));
            OnPropertyChanged(nameof(TypeHint));
            OnPropertyChanged(nameof(DirectionText));
            OnPropertyChanged(nameof(LineVanText));
            if (!IsReturn) LineDamaged = false;
        }
    }
    public bool IsReturn => DocumentType.Value == StockDocumentType.RepReturn;
    public string StoreLabel => IsReturn ? "إلى مخزن (السليم يعود إليه)" : "من مخزن المنتج التام";
    /// <summary>شرح مختصر لنوع المستند المختار.</summary>
    public string TypeHint => IsReturn
        ? "إرجاع من المندوب: ما بقي في السيارة يعود إلى المخزن. علّم «تلف ميداني» للعبوات التالفة فتُسجَّل تالفًا ولا تعود رصيدًا سليمًا."
        : "إسناد حمولة: تُنقل البضاعة من المخزن إلى سيارة المندوب وتصبح في عهدته. (طلبات التحميل المجهَّزة تُنشئ هذا المستند تلقائيًا.)";
    /// <summary>اتجاه الحركة بوضوح: من ← إلى.</summary>
    public string DirectionText => Van is null || Store is null ? "" :
        IsReturn ? $"من: {Van.Name}  ←  إلى: {Store.Name}" : $"من: {Store.Name}  ←  إلى: {Van.Name}";
    public Data.ProjectDb.Entities.Warehouse? Van
    {
        get => _van;
        set { if (SetProperty(ref _van, value)) { OnPropertyChanged(nameof(RepName)); OnPropertyChanged(nameof(DirectionText)); Background(LoadVanStockAsync()); } }
    }
    public string RepName => Van?.OwnerEmployee?.FullName is { } n ? $"المندوب: {n}" : "السيارة بلا مندوب — حدّده من تعريف المخازن";
    public Data.ProjectDb.Entities.Warehouse? Store { get => _store; set { if (SetProperty(ref _store, value)) OnPropertyChanged(nameof(DirectionText)); } }
    public DateTime Date { get => _date; set => SetProperty(ref _date, value); }
    public Item? LineItem { get => _lineItem; set { if (SetProperty(ref _lineItem, value)) { OnPropertyChanged(nameof(LineVanText)); Background(LoadLevelsAsync()); } } }
    /// <summary>رصيد الصنف المختار في السيارة الآن (بالعبوات).</summary>
    public string LineVanText => LineItem is null || Van is null ? "" :
        $"في السيارة الآن: {_packs.Of(LineItem.Id, VanStock.Where(r => r.ItemId == LineItem.Id).Sum(r => r.QuantityBaseUnits))}";
    public ItemPackagingLevel? LineLevel { get => _lineLevel; set => SetProperty(ref _lineLevel, value); }
    public decimal LineQuantity { get => _lineQuantity; set => SetProperty(ref _lineQuantity, value); }
    /// <summary>في الإرجاع: السطر تالف ميدانيًا (يُسجَّل بسبب "تلف ميداني" ولا يعود رصيدًا سليمًا).</summary>
    public bool LineDamaged { get => _lineDamaged; set => SetProperty(ref _lineDamaged, value); }
    public string? Notes { get => _notes; set => SetProperty(ref _notes, value); }
    public DateTime From { get => _from; set => SetProperty(ref _from, value); }
    public DateTime To { get => _to; set => SetProperty(ref _to, value); }
    public decimal VanTotalPieces => VanStock.Sum(r => r.QuantityBaseUnits);
    /// <summary>رصيد السيارة بالعبوات: «12 شرنك + 5 كارتون».</summary>
    public string VanTotalText => VanStock.Count == 0 ? "رصيد السيارة الحالي: فارغة" : $"رصيد السيارة الحالي: {_packs.Total(VanStock.Select(r => (r.ItemId, r.QuantityBaseUnits)))}";
    public string LinesTotalText => Lines.Count == 0 ? "" :
        $"{Lines.Count} سطر — {_packs.Total(Lines.Select(l => (l.ItemId, l.BaseUnits)))}"
        + (Lines.Any(l => l.IsDamaged) ? $" (منها تلف ميداني {_packs.Total(Lines.Where(l => l.IsDamaged).Select(l => (l.ItemId, l.BaseUnits)))})" : "");

    public RelayCommand AddLineCommand { get; }
    public RelayCommand RemoveLineCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand PrintCommand { get; }
    public AsyncRelayCommand LoadDocumentsCommand { get; }
    public RelayCommand PrintStockCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var vanId = Van?.Id;
        var storeId = Store?.Id;
        Vans.Clear();
        foreach (var v in await db.Warehouses.AsNoTracking().Include(w => w.OwnerEmployee)
                     .Where(w => w.IsActive && w.WarehouseType == WarehouseType.RepVan).OrderBy(w => w.Name).ToListAsync()) Vans.Add(v);
        // نوع المخزن مخزّن نصًا — الترتيب في الذاكرة: المنتج التام أولًا
        Stores.Clear();
        foreach (var w in (await db.Warehouses.AsNoTracking()
                     .Where(w => w.IsActive && w.IsSellableStock && w.WarehouseType != WarehouseType.RepVan && w.WarehouseType != WarehouseType.WorkInProcess).ToListAsync())
                     .OrderBy(w => w.WarehouseType != WarehouseType.FinishedGoods).ThenBy(w => w.Name)) Stores.Add(w);
        _packs = await PackFormatter.LoadAsync(db);
        if (ItemsLookup.Count == 0)
            foreach (var i in await db.Items.AsNoTracking().Where(i => i.IsActive && i.SourcingMethod != SourcingMethod.Purchased).OrderBy(i => i.ItemName).ToListAsync())
                ItemsLookup.Add(i);
        _van = Vans.FirstOrDefault(v => v.Id == vanId) ?? Vans.FirstOrDefault();
        OnPropertyChanged(nameof(Van));
        OnPropertyChanged(nameof(RepName));
        _store = Stores.FirstOrDefault(w => w.Id == storeId) ?? Stores.FirstOrDefault();
        OnPropertyChanged(nameof(Store));
        OnPropertyChanged(nameof(DirectionText));
        await LoadVanStockAsync();
        await LoadDocumentsAsync();
    }

    private async Task LoadVanStockAsync()
    {
        VanStock.Clear();
        if (Van is { } van)
        {
            await using var db = Session.NewDb();
            var stock = await new StockQueryService(db).GetCurrentStockAsync(van.Id);
            if (!ReferenceEquals(Van, van)) return;
            VanStock.Clear();
            foreach (var r in stock) VanStock.Add(r);
        }
        OnPropertyChanged(nameof(VanTotalPieces));
        OnPropertyChanged(nameof(VanTotalText));
        OnPropertyChanged(nameof(LineVanText));
    }

    private async Task LoadDocumentsAsync()
    {
        await using var db = Session.NewDb();
        Documents.Clear();
        foreach (var d in await new WarehouseDocumentService(db).GetRepDocumentsAsync(From, To)) Documents.Add(d);
    }

    private async Task LoadLevelsAsync()
    {
        LevelOptions.Clear();
        if (LineItem is null) return;
        await using var db = Session.NewDb();
        foreach (var l in await db.ItemPackagingLevels.AsNoTracking().Where(l => l.ItemId == LineItem.Id).OrderByDescending(l => l.EquivalentBaseUnits).ToListAsync())
            LevelOptions.Add(l);
        LineLevel = LevelOptions.FirstOrDefault();
    }

    private void AddLine()
    {
        if (LineItem is null || LineLevel is null) { Dialogs.Error("اختر الصنف ووحدة التعبئة"); return; }
        if (LineQuantity <= 0) { Dialogs.Error("الكمية يجب أن تكون أكبر من صفر"); return; }
        Lines.Add(new RepLineDraft
        {
            ItemId = LineItem.Id, ItemName = LineItem.ItemName, PackagingLevelId = LineLevel.Id, LevelName = LineLevel.LevelName,
            QuantityInLevel = LineQuantity, BaseUnits = LineQuantity * LineLevel.EquivalentBaseUnits, IsDamaged = IsReturn && LineDamaged
        });
        LineQuantity = 1;
        LineDamaged = false;
        OnPropertyChanged(nameof(LinesTotalText));
    }

    private async Task SaveAsync()
    {
        if (!Require(CanAdd, "مستندات المندوبين")) return;
        if (Van is null || Store is null) { Dialogs.Error("اختر السيارة والمخزن"); return; }
        if (Lines.Count == 0) { Dialogs.Error("أضف صنفًا واحدًا على الأقل"); return; }
        var lines = Lines.Select(l => new StockDocumentLineInput(l.ItemId, l.PackagingLevelId, l.QuantityInLevel, IsDamaged: l.IsDamaged)).ToList();
        // الإسناد: من المخزن إلى السيارة؛ الإرجاع: من السيارة إلى المخزن
        var (source, target) = IsReturn ? (Van.Id, Store.Id) : (Store.Id, Van.Id);
        var request = new StockDocumentRequest(DocumentType.Value, source, Date, lines, Session.UserId, CounterWarehouseId: target, Notes: Notes);
        await using var db = Session.NewDb();
        StockDocument? document = null;
        if (await RunOperationAsync(async () =>
            {
                var (r, doc) = await new WarehouseDocumentService(db).CreateAsync(request);
                document = doc;
                return r;
            }, IsReturn ? $"تم الإرجاع من {Van.Name}" : $"أُسندت الحمولة إلى {Van.Name}"))
        {
            StatusMessage = $"{StatusMessage} — المستند {document!.DocumentNumber}";
            Lines.Clear();
            Notes = null;
            OnPropertyChanged(nameof(LinesTotalText));
            await LoadVanStockAsync();
            await LoadDocumentsAsync();
        }
    }

    public async Task PrintDocumentAsync(int documentId)
    {
        await using var db = Session.NewDb();
        var d = await new WarehouseDocumentService(db).GetDocumentAsync(documentId);
        if (d is null) { Dialogs.Error("المستند غير موجود"); return; }
        Dialogs.ShowReport(DocumentReports.StockDocumentReport(Session, d));
    }

    private void PrintVanStock()
    {
        if (Van is null) { Dialogs.Error("اختر السيارة"); return; }
        var r = new ReportDocument { Key = "van-stock", CompanyName = Session.ProjectName, Title = $"جرد سيارة — {Van.Name}", PrintedBy = Session.FullName };
        r.Field("السيارة", Van.Name).Field("المندوب", Van.OwnerEmployee?.FullName).Field("حتى تاريخ", DateTime.Now.ToString("yyyy/MM/dd HH:mm"));
        r.Columns.AddRange(new[] { "الكود", "الصنف", "التشغيلة", "الصلاحية", "الكمية", "بالقطعة" });
        foreach (var x in VanStock)
            r.Rows.Add(new[] { x.ItemCode, x.ItemName, x.BatchNumber ?? "", x.ExpiryDate?.ToString("yyyy/MM/dd") ?? "", x.Breakdown, $"{x.QuantityBaseUnits:N0}" });
        r.Total("رصيد السيارة", _packs.Total(VanStock.Select(x => (x.ItemId, x.QuantityBaseUnits))), true);
        r.Signatures.AddRange(new[] { "المندوب", "أمين المخزن" });
        Dialogs.ShowReport(r);
    }
}
