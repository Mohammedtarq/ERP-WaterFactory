using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Reps;

/// <summary>سطر طلب تحميل قيد الإدخال: الصنف، الوحدة (مفلترة حسب الصنف)، الكمية.</summary>
public class LoadLineDraft : ObservableObject
{
    private readonly LoadOrdersSectionViewModel _owner;
    private Item? _product;
    private ItemPackagingLevel? _level;
    private CustomRecipe? _recipe;
    private decimal _quantity;

    public LoadLineDraft(LoadOrdersSectionViewModel owner) => _owner = owner;

    public ObservableCollection<ItemPackagingLevel> Levels { get; } = new();
    public ObservableCollection<CustomRecipe> Recipes { get; } = new();
    public Item? Product
    {
        get => _product;
        set
        {
            if (!SetProperty(ref _product, value)) return;
            Levels.Clear();
            Recipes.Clear();
            if (value is not null)
            {
                foreach (var l in _owner.LevelsOf(value.Id)) Levels.Add(l);
                Recipes.Add(Production.DailyProductionSectionViewModel.Basic);
                foreach (var r in _owner.RecipesOf(value.Id)) Recipes.Add(r);
            }
            Level = Levels.FirstOrDefault();
            Recipe = Recipes.FirstOrDefault();
            _owner.RaiseTotals();
        }
    }
    /// <summary>الأساسي = حمولة عامة؛ اسم مطعم = تحميل تشغيلاته المحجوزة لتوصيلها له.</summary>
    public CustomRecipe? Recipe { get => _recipe; set => SetProperty(ref _recipe, value); }
    public int? RecipeId => Recipe is { Id: > 0 } r ? r.Id : null;
    public ItemPackagingLevel? Level { get => _level; set { if (SetProperty(ref _level, value)) Raise(); } }
    public decimal Quantity { get => _quantity; set { if (SetProperty(ref _quantity, value)) Raise(); } }
    public decimal Pieces => Level is null ? 0 : Quantity * Level.EquivalentBaseUnits;
    /// <summary>المتاح من الصنف في المخزن المختار (بالعبوات).</summary>
    public string AvailableText => Product is null ? "" : _owner.PacksText(Product.Id, _owner.AvailableOf(Product.Id));
    /// <summary>مجموع المطلوب من الصنف في كل السطور أكبر من المتاح.</summary>
    public bool IsShort => Product is not null && _owner.RequestedOf(Product.Id) > _owner.AvailableOf(Product.Id);

    internal void RefreshAvailability()
    {
        OnPropertyChanged(nameof(AvailableText));
        OnPropertyChanged(nameof(IsShort));
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(Pieces));
        _owner.RaiseTotals();
    }
}

/// <summary>سطر تجهيز: المطلوب، وما جهّزه أمين المخزن فعلًا (قابل للتعديل).</summary>
public class PrepareLineDraft : ObservableObject
{
    private decimal _prepared;
    public int LineId { get; init; }
    public string ItemName { get; init; } = "";
    public string LevelName { get; init; } = "";
    public string VariantName { get; init; } = "أساسي";
    public decimal Requested { get; init; }
    public decimal Units { get; init; } = 1;
    /// <summary>المتاح من الصنف في مخزن الطلب الآن (بالقطع، ونصه بالعبوات).</summary>
    public decimal AvailablePieces { get; init; }
    public string AvailableText { get; init; } = "";
    public decimal Prepared { get => _prepared; set { if (SetProperty(ref _prepared, value)) OnPropertyChanged(nameof(IsShort)); } }
    public bool IsShort => Prepared * Units > AvailablePieces;
}

/// <summary>ما يشترك فيه «طلبات التحميل» (المندوبون) و«طلبات التجهيز» (المخازن): المتاح، وسطور التجهيز، والطباعة.</summary>
public static class LoadOrderTools
{
    /// <summary>رصيد كل صنف في المخزن بالقطعة.</summary>
    public static async Task<Dictionary<int, decimal>> StockAsync(Data.ProjectDb.ProjectDbContext db, int warehouseId) =>
        await db.StockTransactions.AsNoTracking().Where(t => t.WarehouseId == warehouseId)
            .GroupBy(t => t.ItemId).Select(g => new { g.Key, Qty = g.Sum(t => t.QuantityBaseUnits) })
            .ToDictionaryAsync(x => x.Key, x => x.Qty);

    public static async Task<List<PrepareLineDraft>> PrepareLinesAsync(Data.ProjectDb.ProjectDbContext db, int orderId)
    {
        var order = await new RepOperationsService(db).GetLoadOrderAsync(orderId);
        if (order is null) return new();
        var stock = await StockAsync(db, order.FromWarehouseId);
        var ids = order.Lines.Select(l => l.ItemId).ToList();
        var levels = (await db.ItemPackagingLevels.AsNoTracking().Where(l => ids.Contains(l.ItemId)).ToListAsync())
                     .GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => (IReadOnlyList<ItemPackagingLevel>)g.OrderByDescending(l => l.EquivalentBaseUnits).ToList());
        return order.Lines.OrderBy(l => l.Item.ItemName).Select(l =>
        {
            var have = stock.GetValueOrDefault(l.ItemId);
            return new PrepareLineDraft
            {
                LineId = l.Id, ItemName = l.Item.ItemName, LevelName = l.PackagingLevel.LevelName, VariantName = l.CustomRecipe?.Name ?? "أساسي",
                Requested = l.QuantityInLevel, Prepared = l.PreparedQuantity ?? l.QuantityInLevel, Units = l.PackagingLevel.EquivalentBaseUnits,
                AvailablePieces = have, AvailableText = WarehouseDocumentService.Breakdown(have, levels.GetValueOrDefault(l.ItemId))
            };
        }).ToList();
    }

    public static async Task<ReportDocument?> ReportAsync(AppSession session, Data.ProjectDb.ProjectDbContext db, int orderId)
    {
        var o = await new RepOperationsService(db).GetLoadOrderAsync(orderId);
        if (o is null) return null;
        var r = new ReportDocument { Key = "load-order", CompanyName = session.ProjectName, Title = $"طلب تحميل {o.OrderNumber}", PrintedBy = session.FullName };
        r.Field("المندوب", o.RepEmployee.FullName).Field("السيارة", o.VanWarehouse.Name).Field("من مخزن", o.FromWarehouse.Name)
         .Field("التاريخ", o.LoadDate.ToString("yyyy/MM/dd")).Field("مستند الإسناد", o.StockDocument?.DocumentNumber ?? "لم يُجهَّز بعد");
        r.Columns.AddRange(new[] { "الصنف", "الوحدة", "المطلوب", "المجهَّز", "القطع" });
        foreach (var l in o.Lines.OrderBy(l => l.Item.ItemName))
            r.Rows.Add(new[] { l.Item.ItemName, l.PackagingLevel.LevelName, $"{l.QuantityInLevel:N0}", l.PreparedQuantity is { } p ? $"{p:N0}" : "",
                               $"{(l.PreparedQuantity ?? l.QuantityInLevel) * l.PackagingLevel.EquivalentBaseUnits:N0}" });
        r.Signatures.AddRange(new[] { "مدير المبيعات", "أمين المخزن", "المندوب" });
        return r;
    }
}

// ============================ طلبات التحميل ============================
/// <summary>
/// مدير المبيعات يطلب الحمولة (مقترحة من الحمولة الافتراضية للمندوب)، وأمين المخزن يجهّزها:
/// عند التجهيز فقط يخرج المخزون بمستند إسناد مرقّم بالكميات المجهَّزة فعلًا.
/// </summary>
public class LoadOrdersSectionViewModel : SectionViewModel
{
    private Data.ProjectDb.Entities.Warehouse? _van;
    private Data.ProjectDb.Entities.Warehouse? _store;
    private DateTime _date = DateTime.Today;
    private string? _notes;
    private RepLoadOrderRow? _selectedOrder;
    private string? _cancelReason;
    private List<ItemPackagingLevel> _levels = new();
    private List<CustomRecipe> _recipes = new();
    private Dictionary<int, decimal> _available = new();

    public LoadOrdersSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Reps, "طلبات التحميل", Icons.Order, "#2563EB",
               "طلب الحمولة اليومية للمندوب (من حمولته الافتراضية) وتجهيزها من أمين المخزن — المخزون يتحرك عند التجهيز فقط")
    {
        AddLineCommand = new RelayCommand(() => Lines.Add(new LoadLineDraft(this)));
        RemoveLineCommand = new RelayCommand(p => { if (p is LoadLineDraft l) { Lines.Remove(l); RaiseTotals(); } });
        UseDefaultCommand = new AsyncRelayCommand(UseDefaultAsync);
        SaveDefaultCommand = new AsyncRelayCommand(SaveDefaultAsync);
        CreateCommand = new AsyncRelayCommand(CreateAsync);
        PrepareCommand = new AsyncRelayCommand(PrepareAsync);
        CancelOrderCommand = new AsyncRelayCommand(CancelOrderAsync);
        PrintCommand = new AsyncRelayCommand(p => p is RepLoadOrderRow r ? PrintOrderAsync(r.Id) : Task.CompletedTask);
    }

    protected override bool ReloadOnActivate => true;
    protected override bool HasPendingInput => Lines.Any(l => l.Product is not null && l.Quantity > 0);

    protected override void ResetInput()
    {
        _van = null;
        OnPropertyChanged(nameof(Van));
        OnPropertyChanged(nameof(RepName));
        Lines.Clear();
        Date = DateTime.Today;
        Notes = null;
        CancelReason = null;
        RaiseTotals();
    }

    public ObservableCollection<Data.ProjectDb.Entities.Warehouse> Vans { get; } = new();
    public ObservableCollection<Data.ProjectDb.Entities.Warehouse> Stores { get; } = new();
    public ObservableCollection<Item> Products { get; } = new();
    public ObservableCollection<LoadLineDraft> Lines { get; } = new();
    public ObservableCollection<RepLoadOrderRow> Orders { get; } = new();
    public ObservableCollection<PrepareLineDraft> PrepareLines { get; } = new();

    public Data.ProjectDb.Entities.Warehouse? Van
    {
        get => _van;
        set { if (SetProperty(ref _van, value)) { OnPropertyChanged(nameof(RepName)); if (value is not null && Lines.All(l => l.Product is null)) Background(UseDefaultAsync()); } }
    }
    public string RepName => Van?.OwnerEmployee?.FullName is { } n ? $"المندوب: {n}" : "السيارة بلا مندوب — حدّده من تعريف المخازن";
    public Data.ProjectDb.Entities.Warehouse? Store { get => _store; set { if (SetProperty(ref _store, value)) Background(LoadAvailableAsync()); } }
    public DateTime Date { get => _date; set => SetProperty(ref _date, value); }
    public string? Notes { get => _notes; set => SetProperty(ref _notes, value); }
    public decimal TotalPieces => Lines.Sum(l => l.Pieces);
    public RepLoadOrderRow? SelectedOrder
    {
        get => _selectedOrder;
        set { if (SetProperty(ref _selectedOrder, value)) { OnPropertyChanged(nameof(CanPrepare)); Background(LoadPrepareLinesAsync()); } }
    }
    public bool CanPrepare => SelectedOrder?.Status == RepLoadOrderStatus.Pending;
    public string? CancelReason { get => _cancelReason; set => SetProperty(ref _cancelReason, value); }
    public int PendingCount => Orders.Count(o => o.Status == RepLoadOrderStatus.Pending);

    public RelayCommand AddLineCommand { get; }
    public RelayCommand RemoveLineCommand { get; }
    public AsyncRelayCommand UseDefaultCommand { get; }
    public AsyncRelayCommand SaveDefaultCommand { get; }
    public AsyncRelayCommand CreateCommand { get; }
    public AsyncRelayCommand PrepareCommand { get; }
    public AsyncRelayCommand CancelOrderCommand { get; }
    public AsyncRelayCommand PrintCommand { get; }

    internal IEnumerable<ItemPackagingLevel> LevelsOf(int itemId) =>
        _levels.Where(l => l.ItemId == itemId).OrderByDescending(l => l.EquivalentBaseUnits);
    internal IEnumerable<CustomRecipe> RecipesOf(int itemId) => _recipes.Where(r => r.FinishedItemId == itemId).OrderBy(r => r.Name);
    internal void RaiseTotals()
    {
        OnPropertyChanged(nameof(TotalPieces));
        foreach (var l in Lines) l.RefreshAvailability();
        OnPropertyChanged(nameof(ShortageText));
    }

    internal decimal AvailableOf(int itemId) => _available.GetValueOrDefault(itemId);
    internal decimal RequestedOf(int itemId) => Lines.Where(l => l.Product?.Id == itemId).Sum(l => l.Pieces);
    internal string PacksText(int itemId, decimal pieces) => WarehouseDocumentService.Breakdown(pieces, LevelsOf(itemId).ToList());

    /// <summary>الأصناف التي يزيد مطلوبها على المتاح في المخزن المختار.</summary>
    public string ShortageText
    {
        get
        {
            var shorts = Lines.Where(l => l.Product is not null).GroupBy(l => l.Product!.Id)
                .Where(g => RequestedOf(g.Key) > AvailableOf(g.Key))
                .Select(g => $"{g.First().Product!.ItemName} (المتاح {PacksText(g.Key, AvailableOf(g.Key))})").ToList();
            return shorts.Count == 0 ? "" : $"⚠ لا يكفي في {Store?.Name}: {string.Join("، ", shorts)}";
        }
    }

    private async Task LoadAvailableAsync()
    {
        if (Store is null) { _available = new(); RaiseTotals(); return; }
        await using var db = Session.NewDb();
        _available = await LoadOrderTools.StockAsync(db, Store.Id);
        RaiseTotals();
    }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var vanId = Van?.Id;
        var storeId = Store?.Id;
        Vans.Clear();
        foreach (var v in await db.Warehouses.AsNoTracking().Include(w => w.OwnerEmployee)
                     .Where(w => w.IsActive && w.WarehouseType == WarehouseType.RepVan).OrderBy(w => w.Name).ToListAsync()) Vans.Add(v);
        Stores.Clear();
        foreach (var w in (await db.Warehouses.AsNoTracking()
                     .Where(w => w.IsActive && w.IsSellableStock && w.WarehouseType != WarehouseType.RepVan && w.WarehouseType != WarehouseType.WorkInProcess
                                 && w.WarehouseType != WarehouseType.RawMaterial).ToListAsync())
                     .OrderBy(w => w.WarehouseType != WarehouseType.FinishedGoods).ThenBy(w => w.Name)) Stores.Add(w);
        if (Products.Count == 0)
        {
            foreach (var i in await db.Items.AsNoTracking().Where(i => i.IsActive && i.SourcingMethod != SourcingMethod.Purchased).OrderBy(i => i.ItemName).ToListAsync())
                Products.Add(i);
            var ids = Products.Select(p => p.Id).ToList();
            _levels = await db.ItemPackagingLevels.AsNoTracking().Where(l => ids.Contains(l.ItemId)).ToListAsync();
            _recipes = await db.CustomRecipes.AsNoTracking().Where(r => r.IsActive && ids.Contains(r.FinishedItemId)).ToListAsync();
        }
        _store = Stores.FirstOrDefault(w => w.Id == storeId) ?? Stores.FirstOrDefault();
        OnPropertyChanged(nameof(Store));
        _van = Vans.FirstOrDefault(v => v.Id == vanId) ?? Vans.FirstOrDefault();
        OnPropertyChanged(nameof(Van));
        OnPropertyChanged(nameof(RepName));
        if (Lines.All(l => l.Product is null)) await UseDefaultAsync();
        await LoadAvailableAsync();
        await LoadOrdersAsync();
    }

    private async Task LoadOrdersAsync()
    {
        await using var db = Session.NewDb();
        var selected = SelectedOrder?.Id;
        Orders.Clear();
        foreach (var o in await new RepOperationsService(db).GetLoadOrdersAsync(DateTime.Today.AddDays(-14), DateTime.Today.AddDays(7))) Orders.Add(o);
        SelectedOrder = Orders.FirstOrDefault(o => o.Id == selected) ?? Orders.FirstOrDefault(o => o.Status == RepLoadOrderStatus.Pending);
        OnPropertyChanged(nameof(PendingCount));
    }

    private async Task LoadPrepareLinesAsync()
    {
        PrepareLines.Clear();
        if (SelectedOrder is null) return;
        await using var db = Session.NewDb();
        foreach (var l in await LoadOrderTools.PrepareLinesAsync(db, SelectedOrder.Id)) PrepareLines.Add(l);
    }

    /// <summary>يملأ السطور من الحمولة الافتراضية لمندوب السيارة.</summary>
    private async Task UseDefaultAsync()
    {
        if (Van?.OwnerEmployeeId is not int repId) return;
        await using var db = Session.NewDb();
        var defaults = await new RepOperationsService(db).GetDefaultLoadAsync(repId);
        Lines.Clear();
        foreach (var d in defaults)
        {
            var line = new LoadLineDraft(this) { Product = Products.FirstOrDefault(p => p.Id == d.ItemId) };
            line.Level = line.Levels.FirstOrDefault(l => l.Id == d.PackagingLevelId) ?? line.Level;
            line.Quantity = d.QuantityInLevel;
            Lines.Add(line);
        }
        if (Lines.Count == 0) Lines.Add(new LoadLineDraft(this));
        RaiseTotals();
        StatusMessage = defaults.Count == 0 ? "لا حمولة افتراضية لهذا المندوب — أدخل السطور واحفظها افتراضية" : $"الحمولة الافتراضية: {defaults.Count} صنف";
    }

    private List<RepLoadLineInput> ValidLines() =>
        Lines.Where(l => l.Product is not null && l.Level is not null && l.Quantity > 0)
             .Select(l => new RepLoadLineInput(l.Product!.Id, l.Level!.Id, l.Quantity, l.RecipeId)).ToList();

    private async Task SaveDefaultAsync()
    {
        if (!Require(CanEdit, "الحمولة الافتراضية")) return;
        if (Van?.OwnerEmployeeId is not int repId) { Dialogs.Error("اختر سيارة مرتبطة بمندوب"); return; }
        await using var db = Session.NewDb();
        await RunOperationAsync(() => new RepOperationsService(db).SaveDefaultLoadAsync(repId, ValidLines(), Session.UserId),
                                $"حُفظت الحمولة الافتراضية لـ {Van.OwnerEmployee?.FullName}");
    }

    private async Task CreateAsync()
    {
        if (!Require(CanAdd, "طلبات التحميل")) return;
        if (Van is null || Store is null) { Dialogs.Error("اختر السيارة والمخزن"); return; }
        var lines = ValidLines();
        if (lines.Count == 0) { Dialogs.Error("أضف صنفًا واحدًا على الأقل بكمية"); return; }
        if (ShortageText.Length > 0 && !Dialogs.Confirm($"{ShortageText}\nإرسال الطلب على أي حال؟ (يجهَّز بما يتوفر، أو بعد تسجيل الإنتاج)")) return;
        await using var db = Session.NewDb();
        RepLoadOrder? order = null;
        if (await RunOperationAsync(async () =>
            {
                var (r, o) = await new RepOperationsService(db).CreateLoadOrderAsync(Van.Id, Store.Id, Date, lines, Notes, Session.UserId);
                order = o;
                return r;
            }, "أُرسل طلب التحميل لأمين المخزن"))
        {
            StatusMessage = $"{StatusMessage} — {order!.OrderNumber}";
            Notes = null;
            await LoadOrdersAsync();
            SelectedOrder = Orders.FirstOrDefault(o => o.Id == order.Id);
        }
    }

    private async Task PrepareAsync()
    {
        if (!Require(CanAdd, "تجهيز الحمولة")) return;
        if (SelectedOrder is not { Status: RepLoadOrderStatus.Pending } order) { Dialogs.Error("اختر طلبًا بانتظار التجهيز"); return; }
        if (PrepareLines.Any(l => l.Prepared < 0)) { Dialogs.Error("الكمية المجهَّزة لا تكون سالبة"); return; }
        var shortLines = PrepareLines.Where(l => l.Prepared < l.Requested).ToList();
        if (!Dialogs.Confirm($"تجهيز {order.OrderNumber} لـ {order.RepName}؟ تخرج الكميات من المخزن للسيارة الآن."
                             + (shortLines.Count > 0 ? $"\n{shortLines.Count} سطر مجهَّز بأقل من المطلوب." : "")))
            return;
        await using var db = Session.NewDb();
        StockDocument? doc = null;
        if (await RunOperationAsync(async () =>
            {
                var (r, d) = await new RepOperationsService(db).PrepareLoadOrderAsync(order.Id, PrepareLines.ToDictionary(l => l.LineId, l => l.Prepared), Session.UserId);
                doc = d;
                return r;
            }, $"جُهّزت الحمولة {order.OrderNumber}"))
        {
            StatusMessage = $"{StatusMessage} — مستند الإسناد {doc!.DocumentNumber}";
            await LoadOrdersAsync();
        }
    }

    private async Task CancelOrderAsync()
    {
        if (!Require(CanEdit, "إلغاء طلب التحميل")) return;
        if (SelectedOrder is not { Status: RepLoadOrderStatus.Pending } order) { Dialogs.Error("اختر طلبًا بانتظار التجهيز"); return; }
        if (string.IsNullOrWhiteSpace(CancelReason)) { Dialogs.Error("اكتب سبب الإلغاء"); return; }
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new RepOperationsService(db).CancelLoadOrderAsync(order.Id, CancelReason!, Session.UserId), $"أُلغي {order.OrderNumber}"))
        {
            CancelReason = null;
            await LoadOrdersAsync();
        }
    }

    public async Task PrintOrderAsync(int orderId)
    {
        await using var db = Session.NewDb();
        if (await LoadOrderTools.ReportAsync(Session, db, orderId) is { } r) Dialogs.ShowReport(r);
        else Dialogs.Error("الطلب غير موجود");
    }
}

// ============================ التسوية اليومية ============================
public class FreeLineDraft
{
    public int ItemId { get; init; }
    public string ItemName { get; init; } = "";
    public int PackagingLevelId { get; init; }
    public string LevelName { get; init; } = "";
    public decimal QuantityInLevel { get; init; }
    public int? CustomerId { get; init; }
    public string? CustomerName { get; init; }
    public string Reason { get; init; } = "";
}

public class ExpenseDraft
{
    public decimal Amount { get; init; }
    public string Description { get; init; } = "";
    public int? VehicleId { get; init; }
    public string? VehicleName { get; init; }
}

/// <summary>
/// تسوية نهاية اليوم مع أمين الصندوق: المرتجع (سليم/تالف)، مجاني المندوب لمن ولماذا، المصاريف الميدانية مع السيارة،
/// وفوترة ما بقي في السيارة نقدًا (اختياري)، ثم النقد المستلم مقابل المتوقع — والفرق يبقى في ذمة المندوب.
/// </summary>
public class RepSettlementSectionViewModel : SectionViewModel
{
    private Data.ProjectDb.Entities.Warehouse? _van;
    private Data.ProjectDb.Entities.Warehouse? _store;
    private DateTime _date = DateTime.Today;
    private decimal _walletBalance;
    private Item? _lineItem;
    private ItemPackagingLevel? _lineLevel;
    private decimal _lineQuantity = 1;
    private bool _lineDamaged;
    private Item? _freeItem;
    private ItemPackagingLevel? _freeLevel;
    private decimal _freeQuantity = 1;
    private Customer? _freeCustomer;
    private string? _freeReason;
    private decimal _expenseAmount;
    private string? _expenseDescription;
    private Vehicle? _expenseVehicle;
    private bool _invoiceRemaining;
    private Customer? _invoiceCustomer;
    private decimal _receivedCash;
    private string? _notes;
    private DateTime _from = DateTime.Today.AddDays(-30);
    private DateTime _to = DateTime.Today;

    public RepSettlementSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Reps, "تسوية المندوب", Icons.Currency, "#16A34A",
               "تسوية نهاية اليوم مع أمين الصندوق: المرتجع، المجاني، المصاريف، والنقد المتوقع مقابل المستلم")
    {
        AddReturnCommand = new RelayCommand(AddReturn);
        RemoveReturnCommand = new RelayCommand(p => { if (p is RepLineDraft l) { Returns.Remove(l); RaiseTotals(); } });
        AddFreeCommand = new RelayCommand(AddFree);
        RemoveFreeCommand = new RelayCommand(p => { if (p is FreeLineDraft l) { FreeGoods.Remove(l); RaiseTotals(); } });
        AddExpenseCommand = new RelayCommand(AddExpense);
        RemoveExpenseCommand = new RelayCommand(p => { if (p is ExpenseDraft e) { Expenses.Remove(e); RaiseTotals(); } });
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        LoadHistoryCommand = new AsyncRelayCommand(LoadHistoryAsync);
        PrintCommand = new AsyncRelayCommand(p => p is RepSettlementRow r ? PrintSettlementAsync(r.Id) : Task.CompletedTask);
    }

    protected override bool ReloadOnActivate => true;
    protected override bool HasPendingInput => Returns.Count > 0 || FreeGoods.Count > 0 || Expenses.Count > 0;

    protected override void ResetInput()
    {
        _van = null;
        OnPropertyChanged(nameof(Van));
        VanStock.Clear();
        Returns.Clear();
        FreeGoods.Clear();
        Expenses.Clear();
        Date = DateTime.Today;
        LineQuantity = 1;
        LineDamaged = false;
        FreeQuantity = 1;
        FreeCustomer = null;
        FreeReason = null;
        ExpenseAmount = 0;
        ExpenseDescription = null;
        InvoiceRemaining = false;
        InvoiceCustomer = null;
        ReceivedCash = 0;
        Notes = null;
        RaiseTotals();
    }

    public ObservableCollection<Data.ProjectDb.Entities.Warehouse> Vans { get; } = new();
    public ObservableCollection<Data.ProjectDb.Entities.Warehouse> Stores { get; } = new();
    public ObservableCollection<Item> ItemsLookup { get; } = new();
    public ObservableCollection<ItemPackagingLevel> LevelOptions { get; } = new();
    public ObservableCollection<ItemPackagingLevel> FreeLevelOptions { get; } = new();
    public ObservableCollection<Customer> Customers { get; } = new();
    public ObservableCollection<Vehicle> Vehicles { get; } = new();
    public ObservableCollection<CurrentStockRow> VanStock { get; } = new();
    public ObservableCollection<RepLineDraft> Returns { get; } = new();
    public ObservableCollection<FreeLineDraft> FreeGoods { get; } = new();
    public ObservableCollection<ExpenseDraft> Expenses { get; } = new();
    public ObservableCollection<RepSettlementRow> History { get; } = new();
    public ObservableCollection<OverdueRepRow> Overdue { get; } = new();

    public Data.ProjectDb.Entities.Warehouse? Van
    {
        get => _van;
        set { if (SetProperty(ref _van, value)) { OnPropertyChanged(nameof(RepName)); Background(LoadVanAsync()); } }
    }
    public string RepName => Van?.OwnerEmployee?.FullName is { } n ? $"المندوب: {n}" : "السيارة بلا مندوب — حدّده من تعريف المخازن";
    public Data.ProjectDb.Entities.Warehouse? Store { get => _store; set => SetProperty(ref _store, value); }
    public DateTime Date { get => _date; set => SetProperty(ref _date, value); }
    public decimal WalletBalance { get => _walletBalance; private set { if (SetProperty(ref _walletBalance, value)) RaiseTotals(); } }
    public decimal VanPieces => VanStock.Sum(r => r.QuantityBaseUnits);
    /// <summary>ما في السيارة بعبوات كل منتج: "ماء 330 شرنك: 12 شرنك، …".</summary>
    public string VanText => VanStock.Count == 0 ? "فارغة"
        : string.Join("، ", VanStock.GroupBy(r => r.ItemName).Select(g => $"{g.Key}: {string.Join(" + ", g.Select(r => r.Breakdown))}"));

    public Item? LineItem { get => _lineItem; set { if (SetProperty(ref _lineItem, value)) Background(LoadLevelsAsync(value, LevelOptions, l => LineLevel = l)); } }
    public ItemPackagingLevel? LineLevel { get => _lineLevel; set => SetProperty(ref _lineLevel, value); }
    public decimal LineQuantity { get => _lineQuantity; set => SetProperty(ref _lineQuantity, value); }
    public bool LineDamaged { get => _lineDamaged; set => SetProperty(ref _lineDamaged, value); }

    public Item? FreeItem { get => _freeItem; set { if (SetProperty(ref _freeItem, value)) Background(LoadLevelsAsync(value, FreeLevelOptions, l => FreeLevel = l)); } }
    public ItemPackagingLevel? FreeLevel { get => _freeLevel; set => SetProperty(ref _freeLevel, value); }
    public decimal FreeQuantity { get => _freeQuantity; set => SetProperty(ref _freeQuantity, value); }
    public Customer? FreeCustomer { get => _freeCustomer; set => SetProperty(ref _freeCustomer, value); }
    public string? FreeReason { get => _freeReason; set => SetProperty(ref _freeReason, value); }

    public decimal ExpenseAmount { get => _expenseAmount; set => SetProperty(ref _expenseAmount, value); }
    public string? ExpenseDescription { get => _expenseDescription; set => SetProperty(ref _expenseDescription, value); }
    public Vehicle? ExpenseVehicle { get => _expenseVehicle; set => SetProperty(ref _expenseVehicle, value); }

    /// <summary>ما بقي في السيارة بعد المرتجع والمجاني يُعتبر مبيعًا نقديًا بفاتورة تلقائية.</summary>
    public bool InvoiceRemaining { get => _invoiceRemaining; set { if (SetProperty(ref _invoiceRemaining, value)) RaiseTotals(); } }
    public Customer? InvoiceCustomer { get => _invoiceCustomer; set => SetProperty(ref _invoiceCustomer, value); }
    public decimal ReceivedCash { get => _receivedCash; set { if (SetProperty(ref _receivedCash, value)) RaiseTotals(); } }
    public string? Notes { get => _notes; set => SetProperty(ref _notes, value); }
    public DateTime From { get => _from; set => SetProperty(ref _from, value); }
    public DateTime To { get => _to; set => SetProperty(ref _to, value); }

    public decimal ExpensesTotal => Expenses.Sum(e => e.Amount);
    public decimal ReturnPieces => Returns.Sum(r => r.BaseUnits);
    /// <summary>النقد المتوقع قبل الفاتورة التلقائية (إن وُجدت تُضاف قيمتها عند الحفظ).</summary>
    public decimal ExpectedBeforeInvoice => WalletBalance - ExpensesTotal;
    public string CashHint => InvoiceRemaining
        ? $"في المحفظة {WalletBalance:N0} − مصاريف {ExpensesTotal:N0} = {ExpectedBeforeInvoice:N0} د.ع، تُضاف إليه قيمة فاتورة ما بقي في السيارة عند الحفظ"
        : $"النقد المتوقع: في المحفظة {WalletBalance:N0} − مصاريف {ExpensesTotal:N0} = {ExpectedBeforeInvoice:N0} د.ع"
          + (ReceivedCash > 0 ? $" — الفرق {ExpectedBeforeInvoice - ReceivedCash:N0}" : "");
    public int OverdueCount => Overdue.Count;

    public RelayCommand AddReturnCommand { get; }
    public RelayCommand RemoveReturnCommand { get; }
    public RelayCommand AddFreeCommand { get; }
    public RelayCommand RemoveFreeCommand { get; }
    public RelayCommand AddExpenseCommand { get; }
    public RelayCommand RemoveExpenseCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand LoadHistoryCommand { get; }
    public AsyncRelayCommand PrintCommand { get; }

    private void RaiseTotals()
    {
        OnPropertyChanged(nameof(ExpensesTotal));
        OnPropertyChanged(nameof(ReturnPieces));
        OnPropertyChanged(nameof(ExpectedBeforeInvoice));
        OnPropertyChanged(nameof(CashHint));
    }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var vanId = Van?.Id;
        var storeId = Store?.Id;
        Vans.Clear();
        foreach (var v in await db.Warehouses.AsNoTracking().Include(w => w.OwnerEmployee)
                     .Where(w => w.IsActive && w.WarehouseType == WarehouseType.RepVan).OrderBy(w => w.Name).ToListAsync()) Vans.Add(v);
        Stores.Clear();
        foreach (var w in (await db.Warehouses.AsNoTracking()
                     .Where(w => w.IsActive && w.IsSellableStock && w.WarehouseType != WarehouseType.RepVan && w.WarehouseType != WarehouseType.WorkInProcess
                                 && w.WarehouseType != WarehouseType.RawMaterial).ToListAsync())
                     .OrderBy(w => w.WarehouseType != WarehouseType.FinishedGoods).ThenBy(w => w.Name)) Stores.Add(w);
        if (ItemsLookup.Count == 0)
            foreach (var i in await db.Items.AsNoTracking().Where(i => i.IsActive && i.SourcingMethod != SourcingMethod.Purchased).OrderBy(i => i.ItemName).ToListAsync())
                ItemsLookup.Add(i);
        Customers.Clear();
        foreach (var c in await db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).ToListAsync()) Customers.Add(c);
        Vehicles.Clear();
        foreach (var v in await db.Vehicles.AsNoTracking().Where(v => v.IsActive).OrderBy(v => v.VehicleName).ToListAsync()) Vehicles.Add(v);
        _store = Stores.FirstOrDefault(w => w.Id == storeId) ?? Stores.FirstOrDefault();
        OnPropertyChanged(nameof(Store));
        _van = Vans.FirstOrDefault(v => v.Id == vanId) ?? Vans.FirstOrDefault();
        OnPropertyChanged(nameof(Van));
        OnPropertyChanged(nameof(RepName));
        await LoadVanAsync();
        await LoadHistoryAsync();
        Overdue.Clear();
        foreach (var o in await new RepOperationsService(db).GetOverdueAsync(DateTime.Today)) Overdue.Add(o);
        OnPropertyChanged(nameof(OverdueCount));
    }

    private async Task LoadVanAsync()
    {
        VanStock.Clear();
        decimal wallet = 0;
        // السيارة تُلتقط مرة واحدة: قد تُغيَّر أو تُفرَّغ الشاشة أثناء التحميل
        if (Van is { } van)
        {
            await using var db = Session.NewDb();
            var stock = await new StockQueryService(db).GetCurrentStockAsync(van.Id);
            if (van.OwnerEmployeeId is int repId) wallet = await new RepsService(db).GetWalletBalanceAsync(repId);
            if (!ReferenceEquals(Van, van)) return;
            VanStock.Clear();
            foreach (var r in stock) VanStock.Add(r);
            ExpenseVehicle = Vehicles.FirstOrDefault(v => v.AssignedEmployeeId == van.OwnerEmployeeId);
        }
        WalletBalance = wallet;
        OnPropertyChanged(nameof(VanPieces));
        OnPropertyChanged(nameof(VanText));
    }

    private async Task LoadHistoryAsync()
    {
        await using var db = Session.NewDb();
        History.Clear();
        foreach (var r in await new RepOperationsService(db).GetSettlementsAsync(From, To)) History.Add(r);
    }

    private async Task LoadLevelsAsync(Item? item, ObservableCollection<ItemPackagingLevel> target, Action<ItemPackagingLevel?> select)
    {
        target.Clear();
        if (item is null) { select(null); return; }
        await using var db = Session.NewDb();
        foreach (var l in await db.ItemPackagingLevels.AsNoTracking().Where(l => l.ItemId == item.Id).OrderByDescending(l => l.EquivalentBaseUnits).ToListAsync())
            target.Add(l);
        select(target.FirstOrDefault());
    }

    private void AddReturn()
    {
        if (LineItem is null || LineLevel is null) { Dialogs.Error("اختر الصنف ووحدة التعبئة"); return; }
        if (LineQuantity <= 0) { Dialogs.Error("الكمية يجب أن تكون أكبر من صفر"); return; }
        Returns.Add(new RepLineDraft
        {
            ItemId = LineItem.Id, ItemName = LineItem.ItemName, PackagingLevelId = LineLevel.Id, LevelName = LineLevel.LevelName,
            QuantityInLevel = LineQuantity, BaseUnits = LineQuantity * LineLevel.EquivalentBaseUnits, IsDamaged = LineDamaged
        });
        LineQuantity = 1;
        LineDamaged = false;
        RaiseTotals();
    }

    private void AddFree()
    {
        if (FreeItem is null || FreeLevel is null) { Dialogs.Error("اختر الصنف ووحدة التعبئة للمجاني"); return; }
        if (FreeQuantity <= 0) { Dialogs.Error("الكمية يجب أن تكون أكبر من صفر"); return; }
        if (string.IsNullOrWhiteSpace(FreeReason)) { Dialogs.Error("اكتب سبب المجاني (لمن ولماذا)"); return; }
        FreeGoods.Add(new FreeLineDraft
        {
            ItemId = FreeItem.Id, ItemName = FreeItem.ItemName, PackagingLevelId = FreeLevel.Id, LevelName = FreeLevel.LevelName,
            QuantityInLevel = FreeQuantity, CustomerId = FreeCustomer?.Id, CustomerName = FreeCustomer?.Name, Reason = FreeReason.Trim()
        });
        FreeQuantity = 1;
        FreeReason = null;
        FreeCustomer = null;
        RaiseTotals();
    }

    private void AddExpense()
    {
        if (ExpenseAmount <= 0) { Dialogs.Error("مبلغ المصروف يجب أن يكون أكبر من صفر"); return; }
        if (string.IsNullOrWhiteSpace(ExpenseDescription)) { Dialogs.Error("اكتب وصف المصروف (وقود، تصليح...)"); return; }
        Expenses.Add(new ExpenseDraft { Amount = ExpenseAmount, Description = ExpenseDescription.Trim(), VehicleId = ExpenseVehicle?.Id, VehicleName = ExpenseVehicle?.VehicleName });
        ExpenseAmount = 0;
        ExpenseDescription = null;
        RaiseTotals();
    }

    private async Task SaveAsync()
    {
        if (!Require(CanAdd, "تسوية المندوب")) return;
        if (Van is null || Store is null) { Dialogs.Error("اختر السيارة والمخزن المستلم للمرتجع"); return; }
        if (InvoiceRemaining && InvoiceCustomer is null) { Dialogs.Error("اختر العميل الذي تُسجَّل عليه فاتورة ما بقي في السيارة (مثل زبون نقدي)"); return; }
        if (ReceivedCash < 0) { Dialogs.Error("المبلغ المستلم لا يكون سالبًا"); return; }
        if (!Dialogs.Confirm($"تسوية {Van.OwnerEmployee?.FullName} بتاريخ {Date:yyyy/MM/dd}: مرتجع {ReturnPieces:N0} قطعة، مجاني {FreeGoods.Count} سطر، مصاريف {ExpensesTotal:N0}، مستلم {ReceivedCash:N0} د.ع"
                             + (InvoiceRemaining ? "، وفوترة ما بقي في السيارة نقدًا" : "") + "؟"))
            return;
        var request = new RepSettlementRequest(Van.Id, Store.Id, Date,
            Returns.Select(l => new StockDocumentLineInput(l.ItemId, l.PackagingLevelId, l.QuantityInLevel, IsDamaged: l.IsDamaged)).ToList(),
            FreeGoods.Select(f => new RepFreeLineInput(f.ItemId, f.PackagingLevelId, f.QuantityInLevel, f.CustomerId, f.Reason)).ToList(),
            Expenses.Select(e => new RepExpenseInput(e.Amount, e.Description, e.VehicleId)).ToList(),
            ReceivedCash, Session.UserId, InvoiceRemaining ? InvoiceCustomer!.Id : null, Notes);
        await using var db = Session.NewDb();
        RepSettlement? settlement = null;
        if (await RunOperationAsync(async () =>
            {
                var (r, st) = await new RepOperationsService(db).SettleAsync(request);
                settlement = st;
                return r;
            }, "تمت التسوية"))
        {
            StatusMessage = $"{StatusMessage} {settlement!.SettlementNumber}: متوقع {settlement.ExpectedCash:N0}، مستلم {settlement.ReceivedCash:N0}"
                            + (settlement.Difference != 0 ? $"، الفرق {settlement.Difference:N0} يبقى في ذمة المندوب" : "");
            Returns.Clear();
            FreeGoods.Clear();
            Expenses.Clear();
            ReceivedCash = 0;
            Notes = null;
            InvoiceRemaining = false;
            await LoadAsync();
        }
    }

    public async Task PrintSettlementAsync(int settlementId)
    {
        await using var db = Session.NewDb();
        var s = await new RepOperationsService(db).GetSettlementAsync(settlementId);
        if (s is null) { Dialogs.Error("التسوية غير موجودة"); return; }
        var r = new ReportDocument { Key = "rep-settlement", CompanyName = Session.ProjectName, Title = $"تسوية مندوب {s.SettlementNumber}", PrintedBy = Session.FullName };
        r.Field("المندوب", s.RepEmployee.FullName).Field("السيارة", s.VanWarehouse.Name).Field("التاريخ", s.SettlementDate.ToString("yyyy/MM/dd"))
         .Field("مستند الإرجاع", s.ReturnDocument?.DocumentNumber ?? "—").Field("فاتورة ما بقي", s.AutoInvoice?.InvoiceNumber ?? "—");
        r.Columns.AddRange(new[] { "البند", "الصنف", "الكمية (قطعة)", "التفاصيل" });
        if (s.ReturnDocument is not null)
            foreach (var l in s.ReturnDocument.Lines)
                r.Rows.Add(new[] { l.IsDamaged ? "مرتجع تالف" : "مرتجع سليم", l.Item.ItemName, $"{l.QuantityBaseUnits:N0}", "" });
        foreach (var f in s.FreeGoods)
            r.Rows.Add(new[] { "مجاني المندوب", f.Item.ItemName, $"{f.QuantityBaseUnits:N0}", f.Customer is null ? f.Reason : $"{f.Customer.Name} — {f.Reason}" });
        r.Total("المصاريف الميدانية", $"{s.FieldExpenses:N0} د.ع");
        r.Total("النقد المتوقع", $"{s.ExpectedCash:N0} د.ع");
        r.Total("النقد المستلم", $"{s.ReceivedCash:N0} د.ع", true);
        r.Total("الفرق (في ذمة المندوب)", $"{s.Difference:N0} د.ع");
        r.Signatures.AddRange(new[] { "المندوب", "أمين الصندوق", "أمين المخزن" });
        Dialogs.ShowReport(r);
    }
}
