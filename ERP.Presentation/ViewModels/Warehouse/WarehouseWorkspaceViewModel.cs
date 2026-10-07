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
}

public class BatchChoice
{
    public int? BatchId { get; init; }
    public string Label { get; init; } = "";
    public decimal? Available { get; init; }
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
    private bool _moveToDamaged = true;
    private string? _notes;
    private bool _showAllItems;
    private Item? _lineItem;
    private ItemPackagingLevel? _lineLevel;
    private BatchChoice? _lineBatch;
    private string? _lineNewBatch;
    private DateTime? _lineExpiry;
    private decimal _lineQuantity = 1;
    private DateTime _reportFrom = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _reportTo = DateTime.Today;
    private Item? _ledgerItem;
    private Option<StockDocumentType>? _documentFilter;
    private List<Item> _allItems = new();

    public WarehouseWorkspaceSectionViewModel(AppSession s, IDialogService d, WarehouseEntity warehouse)
        : base(s, d, ModuleCode.Warehouse, warehouse.Name, GlyphOf(warehouse.WarehouseType), ColorOf(warehouse.WarehouseType),
               $"{ArabicLabels.Of(warehouse.WarehouseType)} — إدخال، إخراج، مناقلة، تالف، مسحوب مجاني، وتقارير الحركة")
    {
        WarehouseId = warehouse.Id;
        WarehouseType = warehouse.WarehouseType;
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
        LoadReportsCommand = new AsyncRelayCommand(LoadReportsAsync);
        PrintBalancesCommand = new RelayCommand(() => Dialogs.ShowReport(BuildBalancesReport()));
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
    public int LowCount { get => _lowCount; private set => SetProperty(ref _lowCount, value); }

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
    public ObservableCollection<WarehouseEntity> OtherWarehouses { get; } = new();
    public ObservableCollection<Item> ItemsLookup { get; } = new();
    public ObservableCollection<ItemPackagingLevel> LevelOptions { get; } = new();
    public ObservableCollection<BatchChoice> BatchOptions { get; } = new();
    public ObservableCollection<DocumentLineDraft> Lines { get; } = new();

    public Option<StockDocumentType> Operation
    {
        get => _operation;
        set
        {
            if (!SetProperty(ref _operation, value)) return;
            foreach (var n in new[] { nameof(IsReceipt), nameof(IsOutgoing), nameof(IsTransfer), nameof(IsDamaged), nameof(NeedsParty), nameof(PartyLabel), nameof(OperationHint), nameof(SaveLabel) })
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
    public DateTime? LineExpiry { get => _lineExpiry; set => SetProperty(ref _lineExpiry, value); }
    public decimal LineQuantity { get => _lineQuantity; set { if (SetProperty(ref _lineQuantity, value)) OnPropertyChanged(nameof(LinePiecesText)); } }
    public string LinePiecesText => LineLevel is null ? "" : $"= {LineQuantity * LineLevel.EquivalentBaseUnits:N0} قطعة";
    public string LineAvailableText
    {
        get
        {
            if (LineItem is null || IsReceipt) return "";
            var total = BatchOptions.FirstOrDefault(b => b.BatchId == null)?.Available ?? 0;
            var chosen = LineBatch?.BatchId is null ? total : LineBatch.Available ?? 0;
            return $"المتاح: {chosen:N0} قطعة";
        }
    }
    public string LinesTotalText => Lines.Count == 0 ? "" : $"{Lines.Count} سطر — {Lines.Sum(l => l.Pieces):N0} قطعة";

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
    public ObservableCollection<StockBalanceRow> Balances { get; } = new();
    public ObservableCollection<StockSummaryRow> Summary { get; } = new();
    public ObservableCollection<StockLedgerRow> Ledger { get; } = new();

    public AsyncRelayCommand PrintDocumentCommand { get; }
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
        foreach (var i in items) ItemsLookup.Add(i);
        if (selected is not null && ItemsLookup.All(i => i.Id != selected)) LineItem = null;
    }

    private async Task LoadKpisAndBalancesAsync()
    {
        await using var db = Session.NewDb();
        var balances = await new WarehouseDocumentService(db).GetBalancesAsync(WarehouseId);
        Balances.Clear();
        foreach (var b in balances) Balances.Add(b);
        ItemsCount = balances.Select(b => b.ItemId).Distinct().Count();
        TotalPieces = balances.Sum(b => b.Quantity);
        LowCount = balances.Where(b => b.BelowAlert).Select(b => b.ItemId).Distinct().Count();

        var today = DateTime.Today.ToUniversalTime();
        var tomorrow = DateTime.Today.AddDays(1).ToUniversalTime();
        var todayTx = db.StockTransactions.Where(t => t.WarehouseId == WarehouseId && t.TransactionDate >= today && t.TransactionDate < tomorrow);
        TodayIn = await todayTx.Where(t => t.QuantityBaseUnits > 0).SumAsync(t => (decimal?)t.QuantityBaseUnits) ?? 0;
        TodayOut = -(await todayTx.Where(t => t.QuantityBaseUnits < 0).SumAsync(t => (decimal?)t.QuantityBaseUnits) ?? 0);

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
        foreach (var r in await new WarehouseDocumentService(db).GetLedgerAsync(WarehouseId, LedgerItem?.Id, ReportFrom, ReportTo)) Ledger.Add(r);
    }

    // ============================ إدخال السطور ============================
    private async Task OnLineItemChangedAsync()
    {
        LevelOptions.Clear();
        LineLevel = null;
        if (LineItem is null) { BatchOptions.Clear(); OnPropertyChanged(nameof(LineAvailableText)); return; }
        await using var db = Session.NewDb();
        foreach (var l in await db.ItemPackagingLevels.AsNoTracking().Where(l => l.ItemId == LineItem.Id).OrderByDescending(l => l.EquivalentBaseUnits).ToListAsync())
            LevelOptions.Add(l);
        LineLevel = LevelOptions.FirstOrDefault();
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
            foreach (var b in await db.ItemBatches.AsNoTracking().Where(b => b.ItemId == LineItem.Id).OrderByDescending(b => b.Id).ToListAsync())
                BatchOptions.Add(new BatchChoice { BatchId = b.Id, Label = b.BatchNumber + (b.ExpiryDate is { } e ? $" — ينتهي {e:yyyy/MM/dd}" : "") });
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
                    Label = $"{b.BatchNumber}" + (b.ExpiryDate is { } e ? $" — ينتهي {e:yyyy/MM/dd}" : "") + $" — {r.Qty:N0} قطعة"
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
            if (pieces + already > available) { Dialogs.Error($"الكمية أكبر من المتاح ({available - already:N0} قطعة)"); return; }
        }
        var newBatch = IsReceipt && LineBatch?.BatchId is null && !string.IsNullOrWhiteSpace(LineNewBatch) ? LineNewBatch.Trim() : null;
        Lines.Add(new DocumentLineDraft
        {
            ItemId = LineItem.Id, ItemCode = LineItem.ItemCode, ItemName = LineItem.ItemName,
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
        var confirm = $"حفظ مستند {Operation.Label} ({Lines.Count} سطر، {Lines.Sum(l => l.Pieces):N0} قطعة)" +
                      (IsTransfer && CounterWarehouse is not null ? $" إلى \"{CounterWarehouse.Name}\"" : "") + "؟";
        if (!Dialogs.Confirm(confirm)) return;

        IsBusy = true;
        try
        {
            await using var db = Session.NewDb();
            var request = new StockDocumentRequest(Operation.Value, WarehouseId, DocumentDate,
                Lines.Select(l => new StockDocumentLineInput(l.ItemId, l.PackagingLevelId, l.QuantityInLevel, l.BatchId, l.NewBatchNumber, l.NewBatchExpiry)).ToList(),
                Session.UserId, IsTransfer ? CounterWarehouse?.Id : null, NeedsParty ? PartyName : null,
                IsDamaged ? DamageReason?.Value : null, Notes, MoveToDamaged);
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

    private void ClearForm()
    {
        Lines.Clear();
        PartyName = null;
        Notes = null;
        LineQuantity = 1;
        LineNewBatch = null;
        LineExpiry = null;
        OnPropertyChanged(nameof(LinesTotalText));
    }

    // ============================ الطباعة ============================
    private ReportDocument NewReport(string title, string? stamp = null) =>
        new() { CompanyName = Session.ProjectName, Title = title, Stamp = stamp, PrintedBy = Session.FullName };

    public async Task PrintDocumentAsync(int documentId)
    {
        await using var db = Session.NewDb();
        var d = await new WarehouseDocumentService(db).GetDocumentAsync(documentId);
        if (d is null) { Dialogs.Error("المستند غير موجود"); return; }
        Dialogs.ShowReport(BuildDocumentReport(d));
    }

    public ReportDocument BuildDocumentReport(StockDocument d) => DocumentReports.StockDocumentReport(Session, d);

    public ReportDocument BuildBalancesReport()
    {
        var r = NewReport($"أرصدة {Title}");
        r.Field("المخزن", Title).Field("النوع", WarehouseTypeLabel).Field("حتى تاريخ", DateTime.Now.ToString("yyyy/MM/dd HH:mm"));
        r.Columns.AddRange(new[] { "#", "الكود", "الصنف", "التشغيلة", "الصلاحية", "الرصيد (قطعة)", "بوحدات التعبئة" });
        var i = 0;
        foreach (var b in Balances)
            r.Rows.Add(new[] { (++i).ToString(), b.ItemCode, b.ItemName + (b.BelowAlert ? " ⚠" : ""), b.BatchNumber ?? "—",
                               b.ExpiryDate?.ToString("yyyy/MM/dd") ?? "", $"{b.Quantity:N0}", b.Breakdown });
        r.Total("عدد الأصناف", ItemsCount.ToString()).Total("إجمالي القطع", $"{TotalPieces:N0}", true);
        if (LowCount > 0) r.Total("أصناف عند حد التنبيه", LowCount.ToString());
        r.Signatures.AddRange(new[] { "أمين المخزن", "المدقق" });
        return r;
    }

    public ReportDocument BuildSummaryReport()
    {
        var r = NewReport($"تقرير حركة {Title}");
        r.Field("المخزن", Title).Field("الفترة", $"{ReportFrom:yyyy/MM/dd} — {ReportTo:yyyy/MM/dd}");
        r.Columns.AddRange(new[] { "الكود", "الصنف", "أول المدة", "وارد", "صادر", "تالف", "مجاني", "المتبقي" });
        foreach (var s in Summary)
            r.Rows.Add(new[] { s.ItemCode, s.ItemName, $"{s.Opening:N0}", $"{s.In:N0}", $"{s.Out:N0}", $"{s.Damaged:N0}", $"{s.Free:N0}", $"{s.Closing:N0}" });
        r.Total("إجمالي الوارد", $"{Summary.Sum(s => s.In):N0} قطعة")
         .Total("إجمالي الصادر", $"{Summary.Sum(s => s.Out):N0} قطعة")
         .Total("إجمالي التالف", $"{Summary.Sum(s => s.Damaged):N0} قطعة")
         .Total("إجمالي المجاني", $"{Summary.Sum(s => s.Free):N0} قطعة")
         .Total("المتبقي آخر المدة", $"{Summary.Sum(s => s.Closing):N0} قطعة", true);
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
                ? new[] { x.Date.ToString("yyyy/MM/dd HH:mm"), x.ItemName, x.TypeLabel, x.Reference, x.BatchNumber ?? "", Num(x.In), Num(x.Out) }
                : new[] { x.Date.ToString("yyyy/MM/dd HH:mm"), x.TypeLabel, x.Reference, x.BatchNumber ?? "", x.Party ?? "", Num(x.In), Num(x.Out), $"{x.Balance:N0}" });
        r.Total("إجمالي الوارد", $"{Ledger.Sum(x => x.In):N0}").Total("إجمالي الصادر", $"{Ledger.Sum(x => x.Out):N0}");
        if (LedgerItem is not null) r.Total("الرصيد آخر المدة", $"{Ledger.LastOrDefault()?.Balance ?? 0:N0} قطعة", true);
        return r;

        static string Num(decimal v) => v == 0 ? "" : $"{v:N0}";
    }
}
