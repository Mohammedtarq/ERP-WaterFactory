using System.Collections.ObjectModel;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Reps;

public class RepsModuleViewModel : ModuleViewModel
{
    public RepsModuleViewModel(AppSession s, IDialogService d)
        : base("المندوبون", Icons.Reps, ModuleColors.Reps)
    {
        Van = Add(new VanOperationsSectionViewModel(s, d));
        Wallet = Add(new WalletSectionViewModel(s, d));
        Add(new TerritoriesSectionViewModel(s, d));
        Add(new CustomerAssignmentsSectionViewModel(s, d));
        Add(new SyncConflictsSectionViewModel(s, d));
        Add(new FleetSectionViewModel(s, d));
    }

    public VanOperationsSectionViewModel Van { get; }
    public WalletSectionViewModel Wallet { get; }
}

public enum VanOperation { Load, Return, Damage }

// ============================ عمليات الكاش فان ============================
public class VanLineDraft
{
    public int ItemId { get; init; }
    public string ItemName { get; init; } = "";
    public string LevelName { get; init; } = "";
    public decimal QuantityInLevel { get; init; }
    public decimal BaseUnits { get; init; }
}

public class VanOperationsSectionViewModel : SectionViewModel
{
    private Data.ProjectDb.Entities.Warehouse? _van;
    private Option<VanOperation> _operation;
    private Data.ProjectDb.Entities.Warehouse? _otherWarehouse;
    private Item? _lineItem;
    private ItemPackagingLevel? _lineLevel;
    private decimal _lineQuantity = 1;
    private string? _notes;

    public VanOperationsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Reps, "عمليات الكاش فان", Icons.Truck, "#F97316", "تحميل السيارة من المخزن، الإرجاع، والتالف")
    {
        _operation = Operations[0];
        AddLineCommand = new AsyncRelayCommand(AddLineAsync);
        RemoveLineCommand = new RelayCommand(p => { if (p is VanLineDraft l) Lines.Remove(l); });
        ExecuteCommand = new AsyncRelayCommand(ExecuteAsync);
    }

    protected override bool ReloadOnActivate => true;

    public IReadOnlyList<Option<VanOperation>> Operations { get; } = new[]
    {
        new Option<VanOperation>(VanOperation.Load, "تحميل السيارة من المخزن"),
        new Option<VanOperation>(VanOperation.Return, "إرجاع من السيارة للمخزن"),
        new Option<VanOperation>(VanOperation.Damage, "تالف في السيارة"),
    };
    public ObservableCollection<Data.ProjectDb.Entities.Warehouse> Vans { get; } = new();
    public ObservableCollection<Data.ProjectDb.Entities.Warehouse> StoreWarehouses { get; } = new();
    public ObservableCollection<Item> ItemsLookup { get; } = new();
    public ObservableCollection<ItemPackagingLevel> LevelOptions { get; } = new();
    public ObservableCollection<VanLineDraft> Lines { get; } = new();
    public ObservableCollection<CurrentStockRow> VanStock { get; } = new();

    public Data.ProjectDb.Entities.Warehouse? Van { get => _van; set { if (SetProperty(ref _van, value)) { OnPropertyChanged(nameof(RepName)); Background(LoadVanStockAsync()); } } }
    public string RepName => Van?.OwnerEmployee?.FullName is { } n ? $"المندوب: {n}" : "";
    public Option<VanOperation> Operation
    {
        get => _operation;
        set { if (SetProperty(ref _operation, value)) { OnPropertyChanged(nameof(NeedsOtherWarehouse)); OnPropertyChanged(nameof(OtherWarehouseLabel)); } }
    }
    public bool NeedsOtherWarehouse => Operation.Value != VanOperation.Damage;
    public string OtherWarehouseLabel => Operation.Value == VanOperation.Load ? "من المخزن" : "إلى المخزن";
    public Data.ProjectDb.Entities.Warehouse? OtherWarehouse { get => _otherWarehouse; set => SetProperty(ref _otherWarehouse, value); }
    public Item? LineItem { get => _lineItem; set { if (SetProperty(ref _lineItem, value)) Background(LoadLevelsAsync()); } }
    public ItemPackagingLevel? LineLevel { get => _lineLevel; set => SetProperty(ref _lineLevel, value); }
    public decimal LineQuantity { get => _lineQuantity; set => SetProperty(ref _lineQuantity, value); }
    public string? Notes { get => _notes; set => SetProperty(ref _notes, value); }
    public decimal VanTotalPieces => VanStock.Sum(r => r.QuantityBaseUnits);

    public AsyncRelayCommand AddLineCommand { get; }
    public RelayCommand RemoveLineCommand { get; }
    public AsyncRelayCommand ExecuteCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var vanId = Van?.Id;
        Vans.Clear();
        foreach (var v in await db.Warehouses.AsNoTracking().Include(w => w.OwnerEmployee)
                     .Where(w => w.IsActive && w.WarehouseType == WarehouseType.RepVan).OrderBy(w => w.Name).ToListAsync()) Vans.Add(v);
        StoreWarehouses.Clear();
        foreach (var w in await db.Warehouses.AsNoTracking().Where(w => w.IsActive && w.WarehouseType != WarehouseType.RepVan)
                     .OrderBy(w => w.Name).ToListAsync()) StoreWarehouses.Add(w);
        if (ItemsLookup.Count == 0)
            foreach (var i in await db.Items.AsNoTracking().Where(i => i.IsActive).OrderBy(i => i.ItemName).ToListAsync()) ItemsLookup.Add(i);
        Van = Vans.FirstOrDefault(v => v.Id == vanId) ?? Vans.FirstOrDefault();
        OtherWarehouse ??= StoreWarehouses.FirstOrDefault(w => w.IsSellableStock);
        await LoadVanStockAsync();
    }

    private async Task LoadVanStockAsync()
    {
        VanStock.Clear();
        if (Van is not null)
        {
            await using var db = Session.NewDb();
            foreach (var r in await new StockQueryService(db).GetCurrentStockAsync(Van.Id)) VanStock.Add(r);
        }
        OnPropertyChanged(nameof(VanTotalPieces));
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

    private Task AddLineAsync()
    {
        if (LineItem is null || LineLevel is null) { Dialogs.Error("اختر الصنف ووحدة التعبئة"); return Task.CompletedTask; }
        if (LineQuantity <= 0) { Dialogs.Error("الكمية يجب أن تكون أكبر من صفر"); return Task.CompletedTask; }
        Lines.Add(new VanLineDraft { ItemId = LineItem.Id, ItemName = LineItem.ItemName, LevelName = LineLevel.LevelName,
                                     QuantityInLevel = LineQuantity, BaseUnits = LineQuantity * LineLevel.EquivalentBaseUnits });
        LineQuantity = 1;
        return Task.CompletedTask;
    }

    private async Task ExecuteAsync()
    {
        if (!Require(CanAdd, "عمليات الكاش فان")) return;
        if (Van is null) { Dialogs.Error("اختر الكاش فان (أضفه من المخازن بنوع \"كاش فان مندوب\")"); return; }
        if (Lines.Count == 0) { Dialogs.Error("أضف صنفًا واحدًا على الأقل"); return; }
        if (NeedsOtherWarehouse && OtherWarehouse is null) { Dialogs.Error("اختر المخزن"); return; }

        var lines = Lines.Select(l => new StockLineInput(l.ItemId, null, l.BaseUnits)).ToList();
        await using var db = Session.NewDb();
        var reps = new RepsService(db);
        var ok = Operation.Value switch
        {
            VanOperation.Load => await RunOperationAsync(() => reps.LoadVanAsync(Van.Id, OtherWarehouse!.Id, lines, Session.UserId), $"تم تحميل {Van.Name}"),
            VanOperation.Return => await RunOperationAsync(() => reps.ReturnFromVanAsync(Van.Id, OtherWarehouse!.Id, lines, Session.UserId), $"تم الإرجاع من {Van.Name}"),
            _ => await RunDamageAsync(reps, lines)
        };
        if (!ok) return;
        Lines.Clear();
        Notes = null;
        await LoadVanStockAsync();
    }

    private async Task<bool> RunDamageAsync(RepsService reps, List<StockLineInput> lines)
    {
        foreach (var l in lines)
            if (!await RunOperationAsync(() => reps.RecordVanDamageAsync(Van!.Id, l, Notes, Session.UserId), "تم تسجيل التالف"))
                return false;
        return true;
    }
}

// ============================ محفظة المندوب ============================
public enum WalletAction { Expense, Handover, Collection }

public class WalletSectionViewModel : SectionViewModel
{
    private Employee? _rep;
    private decimal _balance;
    private Option<WalletAction> _action;
    private decimal _amount;
    private string _description = "";
    private Customer? _customer;
    private DateTime _date = DateTime.Today;

    public WalletSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Reps, "محفظة المندوب", Icons.Voucher, "#10B981", "العهدة النقدية: المبيعات، التحصيل، المصروفات، والتسليم للخزينة")
    {
        _action = Actions[0];
        SubmitCommand = new AsyncRelayCommand(SubmitAsync);
    }

    protected override bool ReloadOnActivate => true;

    public IReadOnlyList<Option<WalletAction>> Actions { get; } = new[]
    {
        new Option<WalletAction>(WalletAction.Handover, "تسليم نقد للخزينة"),
        new Option<WalletAction>(WalletAction.Expense, "مصروف ميداني"),
        new Option<WalletAction>(WalletAction.Collection, "تحصيل دين من عميل"),
    };
    public ObservableCollection<Employee> Reps { get; } = new();
    public ObservableCollection<Customer> Customers { get; } = new();
    public ObservableCollection<WalletRow> Rows { get; } = new();

    public Employee? Rep { get => _rep; set { if (SetProperty(ref _rep, value)) Background(LoadStatementAsync()); } }
    public decimal Balance { get => _balance; private set => SetProperty(ref _balance, value); }
    public Option<WalletAction> Action
    {
        get => _action;
        set { if (SetProperty(ref _action, value)) { OnPropertyChanged(nameof(NeedsCustomer)); OnPropertyChanged(nameof(NeedsDescription)); } }
    }
    public bool NeedsCustomer => Action.Value == WalletAction.Collection;
    public bool NeedsDescription => Action.Value == WalletAction.Expense;
    public decimal Amount { get => _amount; set => SetProperty(ref _amount, value); }
    public string ExpenseDescription { get => _description; set => SetProperty(ref _description, value); }
    public Customer? Customer { get => _customer; set => SetProperty(ref _customer, value); }
    public DateTime Date { get => _date; set => SetProperty(ref _date, value); }
    public AsyncRelayCommand SubmitCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var repId = Rep?.Id;
        Reps.Clear();
        foreach (var r in await db.Employees.AsNoTracking().Where(e => e.IsSalesRep && e.IsActive).OrderBy(e => e.FullName).ToListAsync()) Reps.Add(r);
        Customers.Clear();
        foreach (var c in await db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).ToListAsync()) Customers.Add(c);
        _rep = Reps.FirstOrDefault(r => r.Id == repId) ?? Reps.FirstOrDefault();
        OnPropertyChanged(nameof(Rep));
        await LoadStatementAsync();
    }

    private async Task LoadStatementAsync()
    {
        Rows.Clear();
        Balance = 0;
        if (Rep is null) return;
        await using var db = Session.NewDb();
        foreach (var r in await new RepsService(db).GetWalletStatementAsync(Rep.Id)) Rows.Add(r);
        Balance = Rows.LastOrDefault()?.RunningBalance ?? 0;
    }

    private async Task SubmitAsync()
    {
        if (!Require(CanAdd, "حركات المحفظة")) return;
        if (Rep is null) { Dialogs.Error("اختر المندوب"); return; }
        if (NeedsCustomer && Customer is null) { Dialogs.Error("اختر العميل"); return; }
        if (NeedsDescription && string.IsNullOrWhiteSpace(ExpenseDescription)) { Dialogs.Error("اكتب وصف المصروف"); return; }

        await using var db = Session.NewDb();
        var reps = new RepsService(db);
        var ok = await RunOperationAsync(() => Action.Value switch
        {
            WalletAction.Expense => reps.RecordFieldExpenseAsync(Rep.Id, Amount, ExpenseDescription.Trim(), Date, Session.UserId),
            WalletAction.Handover => reps.RecordCashHandoverAsync(Rep.Id, Amount, Date, Session.UserId),
            _ => reps.RecordDebtCollectionAsync(Rep.Id, Customer!.Id, Amount, Date, Session.UserId)
        }, $"تم تسجيل {Action.Label} بمبلغ {Amount:N0} د.ع مع قيده");
        if (!ok) return;
        Amount = 0;
        ExpenseDescription = "";
        await LoadStatementAsync();
    }
}

// ============================ المناطق والزبائن المخصصون ============================
public class TerritoriesSectionViewModel : CrudSectionViewModel<RepTerritory>
{
    public TerritoriesSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Reps, "المناطق", Icons.Location, "#0EA5E9", "مناطق عمل كل مندوب") { }

    public ObservableCollection<Employee> Reps { get; } = new();

    protected override int GetId(RepTerritory e) => e.Id;
    protected override string Describe(RepTerritory e) => $"{e.Employee?.FullName} — {e.TerritoryName}";

    protected override async Task LoadLookupsAsync(ProjectDbContext db)
    {
        Reps.Clear();
        foreach (var r in await db.Employees.AsNoTracking().Where(e => e.IsSalesRep && e.IsActive).OrderBy(e => e.FullName).ToListAsync()) Reps.Add(r);
    }

    protected override Task<List<RepTerritory>> QueryAsync(ProjectDbContext db) =>
        db.RepTerritories.AsNoTracking().Include(t => t.Employee).OrderBy(t => t.Employee.FullName).ThenBy(t => t.TerritoryName).ToListAsync();

    protected override RepTerritory CreateNew() => new() { EmployeeId = Reps.FirstOrDefault()?.Id ?? 0 };
    protected override string? Validate(RepTerritory e) =>
        e.EmployeeId == 0 ? "اختر المندوب" : string.IsNullOrWhiteSpace(e.TerritoryName) ? "أدخل اسم المنطقة" : null;
}

public class CustomerAssignmentsSectionViewModel : CrudSectionViewModel<RepCustomerAssignment>
{
    public CustomerAssignmentsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Reps, "الزبائن المخصصون", Icons.People, "#8B5CF6", "العملاء الذين يخدمهم كل مندوب") { }

    public ObservableCollection<Employee> Reps { get; } = new();
    public ObservableCollection<Customer> Customers { get; } = new();

    protected override int GetId(RepCustomerAssignment e) => e.Id;
    protected override string Describe(RepCustomerAssignment e) => $"{e.Employee?.FullName} ← {e.Customer?.Name}";

    protected override async Task LoadLookupsAsync(ProjectDbContext db)
    {
        Reps.Clear();
        foreach (var r in await db.Employees.AsNoTracking().Where(e => e.IsSalesRep && e.IsActive).OrderBy(e => e.FullName).ToListAsync()) Reps.Add(r);
        Customers.Clear();
        foreach (var c in await db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).ToListAsync()) Customers.Add(c);
    }

    protected override Task<List<RepCustomerAssignment>> QueryAsync(ProjectDbContext db) =>
        db.RepCustomerAssignments.AsNoTracking().Include(a => a.Employee).Include(a => a.Customer)
          .OrderBy(a => a.Employee.FullName).ThenBy(a => a.Customer.Name).ToListAsync();

    protected override RepCustomerAssignment CreateNew() => new() { EmployeeId = Reps.FirstOrDefault()?.Id ?? 0, AssignedAt = DateTime.UtcNow };

    protected override string? Validate(RepCustomerAssignment e)
    {
        if (e.EmployeeId == 0 || e.CustomerId == 0) return "اختر المندوب والعميل";
        if (Items.Any(x => x.Id != e.Id && x.EmployeeId == e.EmployeeId && x.CustomerId == e.CustomerId)) return "هذا العميل مخصص لهذا المندوب مسبقًا";
        return null;
    }
}

// ============================ تعارضات المزامنة ============================
public class ConflictRow
{
    public int Id { get; init; }
    public DateTime CreatedAt { get; init; }
    public string RepName { get; init; } = "";
    public string ItemName { get; init; } = "";
    public string? BatchNumber { get; init; }
    public decimal RequestedQuantity { get; init; }
    public decimal ResultingBalance { get; init; }
    public string StatusLabel { get; init; } = "";
    public bool IsPending { get; init; }
    public string? ResolutionNotes { get; init; }
}

public class SyncConflictsSectionViewModel : SectionViewModel
{
    private ConflictRow? _selected;
    private string _notes = "";
    private bool _zeroBalance = true;
    private bool _showResolved;

    public SyncConflictsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Reps, "تعارضات المزامنة", Icons.Alert, "#EF4444", "أرصدة سالبة من عمل المندوب دون اتصال وتسويتها")
    {
        ResolveCommand = new AsyncRelayCommand(ResolveAsync);
    }

    protected override bool ReloadOnActivate => true;
    public ObservableCollection<ConflictRow> Rows { get; } = new();
    public ConflictRow? Selected { get => _selected; set => SetProperty(ref _selected, value); }
    public string Notes { get => _notes; set => SetProperty(ref _notes, value); }
    public bool ZeroBalance { get => _zeroBalance; set => SetProperty(ref _zeroBalance, value); }
    public bool ShowResolved { get => _showResolved; set { if (SetProperty(ref _showResolved, value)) Background(LoadAsync()); } }
    public int PendingCount => Rows.Count(r => r.IsPending);
    public AsyncRelayCommand ResolveCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var rows = await db.SyncConflicts.AsNoTracking()
            .Where(c => ShowResolved || c.Status == SyncConflictStatus.Pending)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new ConflictRow
            {
                Id = c.Id, CreatedAt = c.CreatedAt, RepName = c.Employee.FullName, ItemName = c.Item.ItemName,
                BatchNumber = c.Batch != null ? c.Batch.BatchNumber : null, RequestedQuantity = c.RequestedQuantity,
                ResultingBalance = c.ResultingBalance, IsPending = c.Status == SyncConflictStatus.Pending,
                StatusLabel = c.Status == SyncConflictStatus.Pending ? "بانتظار التسوية" : "مسوّى", ResolutionNotes = c.ResolutionNotes
            }).ToListAsync();
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        OnPropertyChanged(nameof(PendingCount));
    }

    private async Task ResolveAsync()
    {
        if (!Require(CanEdit, "تسوية التعارضات")) return;
        if (Selected is null || !Selected.IsPending) { Dialogs.Error("اختر تعارضًا بانتظار التسوية"); return; }
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new RepsService(db).ResolveConflictAsync(Selected.Id, Notes, ZeroBalance, Session.UserId), "تمت تسوية التعارض"))
        {
            Notes = "";
            await LoadAsync();
        }
    }
}

// ============================ الأسطول ============================
public class FleetSectionViewModel : CrudSectionViewModel<Vehicle>
{
    /// <summary>تنبيه قبل انتهاء الإجازة/السنوية بهذه المدة.</summary>
    public const int AlertDays = 30;

    public FleetSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Reps, "الأسطول", Icons.Truck, "#64748B", "السيارات، السائقون، وتنبيه انتهاء الإجازة والسنوية") { }

    public ObservableCollection<Employee> Employees { get; } = new();

    public string AlertsText
    {
        get
        {
            var limit = DateTime.Today.AddDays(AlertDays);
            var alerts = Items.Where(v => v.IsActive).SelectMany(v => new[]
            {
                v.DrivingLicenseExpiry is { } l && l <= limit ? $"إجازة سوق {v.AssignedEmployee?.FullName ?? v.VehicleName}: {(l < DateTime.Today ? "منتهية" : "تنتهي")} {l:yyyy/MM/dd}" : null,
                v.VehicleRegistrationExpiry is { } r && r <= limit ? $"سنوية {v.VehicleName} ({v.PlateNumber}): {(r < DateTime.Today ? "منتهية" : "تنتهي")} {r:yyyy/MM/dd}" : null,
            }).Where(x => x is not null).ToList();
            return alerts.Count == 0 ? $"لا توجد وثائق تنتهي خلال {AlertDays} يومًا ✓" : "⚠ " + string.Join(" · ", alerts);
        }
    }
    public bool HasAlerts => !AlertsText.EndsWith("✓");

    protected override int GetId(Vehicle e) => e.Id;
    protected override string Describe(Vehicle e) => $"{e.VehicleName} {e.PlateNumber}";
    protected override bool Matches(Vehicle e, string t) => base.Matches(e, t) || (e.AssignedEmployee?.FullName.Contains(t) ?? false);

    protected override async Task LoadLookupsAsync(ProjectDbContext db)
    {
        Employees.Clear();
        foreach (var e in await db.Employees.AsNoTracking().Where(e => e.IsActive).OrderBy(e => e.FullName).ToListAsync()) Employees.Add(e);
    }

    protected override Task<List<Vehicle>> QueryAsync(ProjectDbContext db) =>
        db.Vehicles.AsNoTracking().Include(v => v.AssignedEmployee).OrderBy(v => v.VehicleName).ToListAsync();

    protected override string? Validate(Vehicle e) => string.IsNullOrWhiteSpace(e.VehicleName) ? "أدخل اسم/نوع السيارة" : null;

    public override async Task LoadAsync()
    {
        await base.LoadAsync();
        OnPropertyChanged(nameof(AlertsText));
        OnPropertyChanged(nameof(HasAlerts));
    }
}
