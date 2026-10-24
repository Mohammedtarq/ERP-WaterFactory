using System.Collections.ObjectModel;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Reps;

/// <summary>
/// صنف في سيارة المندوب بالاسم الموحد («شرنك 20×330 الرحمة») مع المتاح منه بعدد العبوات الكاملة؛
/// الاسم يحمل العبوة فلا حقل عبوة منفصل، واختياره يملأ الكمية بالمتاح.
/// </summary>
public class VanChoice
{
    public int ItemId { get; init; }
    public int? RecipeId { get; init; }
    public string Name { get; init; } = "";
    public int LevelId { get; init; }
    public decimal PerPack { get; init; } = 1;
    public decimal Available { get; init; }
    public string Display => $"{Name} — متوفر {Available:N0}";
    public string Key => $"{ItemId}/{RecipeId}";
}

/// <summary>سطر مرتجع أو مجاني قيد الإدخال، بالاسم الموحد وعدد عبوات صحيح.</summary>
public class SettleLineDraft
{
    public VanChoice Choice { get; init; } = null!;
    public decimal Quantity { get; init; }
    public bool IsDamaged { get; init; }
    public int? CustomerId { get; init; }
    public string? CustomerName { get; init; }
    public string? Reason { get; init; }
    public string Name => Choice.Name;
    public string Text => Reason is null ? $"{Quantity:N0} ({(IsDamaged ? "تالف ميداني" : "سليم")})"
                                         : $"{Quantity:N0} — {(CustomerName is null ? "" : CustomerName + " — ")}{Reason}";
}

/// <summary>طباعة مشتركة للتسوية المقسّمة (المنتجات والنقد) بالأسماء الموحدة.</summary>
public static class RepSettlementPrint
{
    public static async Task<ReportDocument?> BuildAsync(AppSession session, ProjectDbContext db, int settlementId)
    {
        var s = await new RepOperationsService(db).GetSettlementAsync(settlementId);
        if (s is null) return null;
        var ids = (s.ReturnDocument?.Lines.Select(l => l.ItemId) ?? Enumerable.Empty<int>()).Concat(s.FreeGoods.Select(f => f.ItemId)).Distinct().ToList();
        var names = await ProductNames.LoadAsync(db, ids);
        var packs = await PackFormatter.LoadAsync(db, ids);
        var r = new ReportDocument { Key = "rep-settlement", CompanyName = session.ProjectName, Title = $"تسوية مندوب {s.SettlementNumber}", PrintedBy = session.Username };
        r.Field("المندوب", s.RepEmployee.FullName).Field("السيارة", s.VanWarehouse.Name).Field("التاريخ", s.SettlementDate.ToString("yyyy/MM/dd"))
         .Field("الحالة", s.Status switch { RepSettlementStatus.PendingCash => "المنتجات سُوّيت — بانتظار النقد", RepSettlementStatus.PendingWarehouse => "بانتظار تأكيد المخازن", _ => "مكتملة" })
         .Field("مستند الإرجاع", s.ReturnDocument?.DocumentNumber ?? "—").Field("فاتورة ما بقي", s.AutoInvoice?.InvoiceNumber ?? "—");
        r.Columns.AddRange(new[] { "البند", "الصنف (الاسم الموحد)", "الكمية", "التفاصيل" });
        if (s.ReturnDocument is not null)
            foreach (var l in s.ReturnDocument.Lines)
                r.Rows.Add(new[] { l.IsDamaged ? "مرتجع تالف" : "مرتجع سليم", names.Name(l.ItemId, l.Batch?.CustomRecipeId), packs.Count(l.ItemId, l.QuantityBaseUnits), "" });
        foreach (var f in s.FreeGoods)
            r.Rows.Add(new[] { "مجاني المندوب", names.Name(f.ItemId), packs.Count(f.ItemId, f.QuantityBaseUnits), f.Customer is null ? f.Reason : $"{f.Customer.Name} — {f.Reason}" });
        if (s.Status == RepSettlementStatus.Confirmed)
        {
            r.Total("المصاريف الميدانية", $"{s.FieldExpenses:N0} د.ع");
            r.Total("النقد المتوقع", $"{s.ExpectedCash:N0} د.ع");
            r.Total("النقد المستلم", $"{s.ReceivedCash:N0} د.ع", true);
            r.Total("الفرق (في ذمة المندوب)", $"{s.Difference:N0} د.ع");
        }
        r.Signatures.AddRange(new[] { "المندوب", "أمين المخزن", "أمين الصندوق" });
        return r;
    }
}

/// <summary>
/// «تسوية منتجات المندوب» (قسم المخازن): أمين المخزن يرى رصيد السيارة بالأسماء الموحدة ويسوّي المرتجع (سليم/تالف) ومجاني المندوب
/// وفاتورة ما بقي. المنتجات تُرحَّل فورًا، ثم تُكمل المالية النقد من «تسوية النقد».
/// </summary>
public class RepProductSettlementSectionViewModel : SectionViewModel
{
    private Data.ProjectDb.Entities.Warehouse? _van;
    private Data.ProjectDb.Entities.Warehouse? _store;
    private DateTime _date = DateTime.Today;
    private VanChoice? _lineChoice;
    private decimal _lineQuantity;
    private bool _lineDamaged;
    private VanChoice? _freeChoice;
    private decimal _freeQuantity;
    private Customer? _freeCustomer;
    private string? _freeReason;
    private bool _invoiceRemaining;
    private Customer? _invoiceCustomer;
    private string? _notes;

    public RepProductSettlementSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Warehouse, "تسوية منتجات المندوب", Icons.Truck, "#16A34A",
               "أمين المخزن يسوّي مرتجع المندوب ومجانيه بالأسماء الموحدة، ثم تسوّي المالية نقده")
    {
        AddReturnCommand = new RelayCommand(AddReturn);
        RemoveReturnCommand = new RelayCommand(p => { if (p is SettleLineDraft l) Returns.Remove(l); });
        AddFreeCommand = new RelayCommand(AddFree);
        RemoveFreeCommand = new RelayCommand(p => { if (p is SettleLineDraft l) FreeGoods.Remove(l); });
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        PrintCommand = new AsyncRelayCommand(async p =>
        {
            if (p is not RepSettlementRow row) return;
            await using var db = Session.NewDb();
            if (await RepSettlementPrint.BuildAsync(Session, db, row.Id) is { } r) Dialogs.ShowReport(r); else Dialogs.Error("التسوية غير موجودة");
        });
    }

    protected override bool ReloadOnActivate => true;
    protected override bool HasPendingInput => Returns.Count > 0 || FreeGoods.Count > 0;
    protected override void ResetInput()
    {
        Returns.Clear(); FreeGoods.Clear();
        Date = DateTime.Today;
        LineChoice = null; FreeChoice = null; FreeCustomer = null; FreeReason = null;
        InvoiceRemaining = false; InvoiceCustomer = null; Notes = null;
    }

    public ObservableCollection<Data.ProjectDb.Entities.Warehouse> Vans { get; } = new();
    public ObservableCollection<Data.ProjectDb.Entities.Warehouse> Stores { get; } = new();
    public ObservableCollection<Customer> Customers { get; } = new BulkObservableCollection<Customer>();
    public ObservableCollection<VanChoice> Choices { get; } = new();
    public ObservableCollection<SettleLineDraft> Returns { get; } = new();
    public ObservableCollection<SettleLineDraft> FreeGoods { get; } = new();
    public ObservableCollection<RepSettlementRow> History { get; } = new();

    public Data.ProjectDb.Entities.Warehouse? Van { get => _van; set { if (SetProperty(ref _van, value)) { OnPropertyChanged(nameof(RepName)); Returns.Clear(); FreeGoods.Clear(); Background(LoadChoicesAsync()); } } }
    public Data.ProjectDb.Entities.Warehouse? Store { get => _store; set => SetProperty(ref _store, value); }
    public DateTime Date { get => _date; set => SetProperty(ref _date, value); }
    public string RepName => Van?.OwnerEmployee?.FullName is { } n ? $"المندوب: {n}" : "السيارة بلا مندوب — حدّده من تعريف المخازن";
    /// <summary>رصيد السيارة بالأسماء الموحدة والعدد عند اختيار المندوب.</summary>
    public string VanText => Choices.Count == 0 ? "فارغة" : string.Join("، ", Choices.Select(c => $"{c.Name}: {c.Available:N0}"));

    /// <summary>اختيار الصنف يملأ الكمية بالمتاح مباشرة (يمكن إنقاصها).</summary>
    public VanChoice? LineChoice { get => _lineChoice; set { if (SetProperty(ref _lineChoice, value) && value is not null) LineQuantity = RemainingOf(value); } }
    public decimal LineQuantity { get => _lineQuantity; set => SetProperty(ref _lineQuantity, value); }
    public bool LineDamaged { get => _lineDamaged; set => SetProperty(ref _lineDamaged, value); }
    public VanChoice? FreeChoice { get => _freeChoice; set { if (SetProperty(ref _freeChoice, value) && value is not null) FreeQuantity = RemainingOf(value); } }
    public decimal FreeQuantity { get => _freeQuantity; set => SetProperty(ref _freeQuantity, value); }
    public Customer? FreeCustomer { get => _freeCustomer; set => SetProperty(ref _freeCustomer, value); }
    public string? FreeReason { get => _freeReason; set => SetProperty(ref _freeReason, value); }
    public bool InvoiceRemaining { get => _invoiceRemaining; set => SetProperty(ref _invoiceRemaining, value); }
    public Customer? InvoiceCustomer { get => _invoiceCustomer; set => SetProperty(ref _invoiceCustomer, value); }
    public string? Notes { get => _notes; set => SetProperty(ref _notes, value); }

    public RelayCommand AddReturnCommand { get; }
    public RelayCommand RemoveReturnCommand { get; }
    public RelayCommand AddFreeCommand { get; }
    public RelayCommand RemoveFreeCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand PrintCommand { get; }

    private decimal UsedOf(VanChoice c) => Returns.Concat(FreeGoods).Where(l => l.Choice.Key == c.Key).Sum(l => l.Quantity);
    private decimal RemainingOf(VanChoice c) => Math.Max(0, c.Available - UsedOf(c));

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var vanId = Van?.Id;
        var storeId = Store?.Id;
        Vans.Clear();
        foreach (var v in await db.Warehouses.AsNoTracking().Include(w => w.OwnerEmployee)
                     .Where(w => w.IsActive && w.WarehouseType == WarehouseType.RepVan && w.OwnerEmployeeId != null).OrderBy(w => w.Name).ToListAsync()) Vans.Add(v);
        Stores.Clear();
        foreach (var w in (await db.Warehouses.AsNoTracking()
                     .Where(w => w.IsActive && w.IsSellableStock && w.WarehouseType != WarehouseType.RepVan && w.WarehouseType != WarehouseType.WorkInProcess
                                 && w.WarehouseType != WarehouseType.RawMaterial).ToListAsync())
                     .OrderBy(w => w.WarehouseType != WarehouseType.FinishedGoods).ThenBy(w => w.Name)) Stores.Add(w);
        Customers.ReplaceAll(await db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).ToListAsync());
        _store = Stores.FirstOrDefault(w => w.Id == storeId) ?? Stores.FirstOrDefault();
        OnPropertyChanged(nameof(Store));
        _van = Vans.FirstOrDefault(v => v.Id == vanId) ?? Vans.FirstOrDefault();
        OnPropertyChanged(nameof(Van));
        OnPropertyChanged(nameof(RepName));
        await LoadChoicesAsync();
        History.Clear();
        foreach (var r in await new RepOperationsService(db).GetSettlementsAsync(DateTime.Today.AddDays(-14), DateTime.Today)) History.Add(r);
    }

    private async Task LoadChoicesAsync()
    {
        Choices.Clear();
        if (Van is { } van)
        {
            await using var db = Session.NewDb();
            var stock = await new StockQueryService(db).GetCurrentStockAsync(van.Id);
            var names = await ProductNames.LoadAsync(db, stock.Select(r => r.ItemId).Distinct());
            if (!ReferenceEquals(Van, van)) return;
            foreach (var g in stock.Where(r => r.QuantityBaseUnits > 0).GroupBy(r => (r.ItemId, r.RecipeId)).OrderBy(g => g.First().ItemName).ThenBy(g => g.Key.RecipeId ?? 0))
            {
                var top = names.TopLevel(g.Key.ItemId);
                var per = top?.EquivalentBaseUnits ?? 1;
                var available = Math.Floor(g.Sum(r => r.QuantityBaseUnits) / per);
                if (available <= 0 || top is null) continue;
                Choices.Add(new VanChoice
                {
                    ItemId = g.Key.ItemId, RecipeId = g.Key.RecipeId, Name = names.Name(g.Key.ItemId, g.Key.RecipeId), LevelId = top.Id, PerPack = per, Available = available
                });
            }
        }
        _lineChoice = null; _freeChoice = null;
        OnPropertyChanged(nameof(LineChoice)); OnPropertyChanged(nameof(FreeChoice)); OnPropertyChanged(nameof(VanText));
    }

    private string? CheckQuantity(VanChoice? choice, decimal quantity)
    {
        if (choice is null) return "اختر الصنف";
        if (quantity <= 0) return "الكمية يجب أن تكون أكبر من صفر";
        if (quantity != Math.Floor(quantity)) return "الكمية بعدد صحيح من العبوات";
        if (quantity > RemainingOf(choice)) return $"الكمية أكبر من المتوفر في السيارة ({RemainingOf(choice):N0})";
        return null;
    }

    private void AddReturn()
    {
        if (CheckQuantity(LineChoice, LineQuantity) is { } error) { Dialogs.Error(error); return; }
        Returns.Add(new SettleLineDraft { Choice = LineChoice!, Quantity = LineQuantity, IsDamaged = LineDamaged });
        LineDamaged = false;
        LineChoice = LineChoice;   // يحدّث الكمية المتبقية
        OnPropertyChanged(nameof(LineChoice));
        LineQuantity = RemainingOf(LineChoice!);
    }

    private void AddFree()
    {
        if (CheckQuantity(FreeChoice, FreeQuantity) is { } error) { Dialogs.Error(error); return; }
        if (string.IsNullOrWhiteSpace(FreeReason)) { Dialogs.Error("اكتب سبب المجاني (لمن ولماذا)"); return; }
        FreeGoods.Add(new SettleLineDraft { Choice = FreeChoice!, Quantity = FreeQuantity, CustomerId = FreeCustomer?.Id, CustomerName = FreeCustomer?.Name, Reason = FreeReason.Trim() });
        FreeReason = null;
        FreeCustomer = null;
        FreeQuantity = RemainingOf(FreeChoice!);
    }

    private async Task SaveAsync()
    {
        if (!Require(CanAdd, "تسوية المنتجات")) return;
        if (!Require(Has(SpecialPermission.SettlementConfirm), $"تسوية منتجات المندوب (صلاحية «{SpecialPermission.NameOf(SpecialPermission.SettlementConfirm)}»)")) return;
        if (Van is null || Store is null) { Dialogs.Error("اختر السيارة والمخزن المستلم للمرتجع"); return; }
        if (InvoiceRemaining && InvoiceCustomer is null) { Dialogs.Error("اختر العميل الذي تُسجَّل عليه فاتورة ما بقي في السيارة (مثل زبون نقدي)"); return; }
        if (!Dialogs.Confirm($"تسوية منتجات {Van.OwnerEmployee?.FullName} بتاريخ {Date:yyyy/MM/dd}: مرتجع {Returns.Count} سطر، مجاني {FreeGoods.Count} سطر"
                             + (InvoiceRemaining ? "، وفوترة ما بقي في السيارة نقدًا" : "") + "؟\n\nتُرحَّل المنتجات الآن، وتُكمل المالية النقد من «تسوية النقد»."))
            return;
        var request = new ProductSettlementRequest(Van.Id, Store.Id, Date,
            Returns.Select(l => new StockDocumentLineInput(l.Choice.ItemId, l.Choice.LevelId, l.Quantity, IsDamaged: l.IsDamaged, CustomRecipeId: l.Choice.RecipeId)).ToList(),
            FreeGoods.Select(f => new RepFreeLineInput(f.Choice.ItemId, f.Choice.LevelId, f.Quantity, f.CustomerId, f.Reason!, f.Choice.RecipeId)).ToList(),
            Session.UserId, InvoiceRemaining ? InvoiceCustomer!.Id : null, Notes);
        await using var db = Session.NewDb();
        RepSettlement? settlement = null;
        if (await RunOperationAsync(async () =>
            {
                var (r, st) = await new RepOperationsService(db).SettleProductsAsync(request);
                settlement = st;
                return r;
            }, "تمت تسوية المنتجات"))
        {
            StatusMessage = $"{StatusMessage} {settlement!.SettlementNumber} — بانتظار النقد في المالية (تسوية النقد)";
            Returns.Clear(); FreeGoods.Clear();
            Notes = null; InvoiceRemaining = false;
            await LoadAsync();
        }
    }
}

/// <summary>
/// «تسوية النقد» (قسم المالية): بعد أن سوّى أمين المخزن منتجات المندوب، تسجّل المالية المصاريف الميدانية والنقد المستلم؛
/// والفرق يبقى في ذمة المندوب. تُكمل تسوية المنتجات المنتظرة أو تُنشئ تسوية نقد وحدها.
/// </summary>
public class RepCashSettlementSectionViewModel : SectionViewModel
{
    private Data.ProjectDb.Entities.Warehouse? _van;
    private DateTime _date = DateTime.Today;
    private decimal _walletBalance;
    private decimal _expenseAmount;
    private string? _expenseDescription;
    private Vehicle? _expenseVehicle;
    private decimal _receivedCash;
    private string? _notes;
    private RepSettlementRow? _pendingProducts;

    public RepCashSettlementSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Finance, "تسوية نقد المندوب", Icons.Currency, "#16A34A",
               "المالية تسجّل مصاريف المندوب الميدانية وتستلم نقده بعد تسوية أمين المخزن لمنتجاته")
    {
        AddExpenseCommand = new RelayCommand(AddExpense);
        RemoveExpenseCommand = new RelayCommand(p => { if (p is ExpenseDraft e) { Expenses.Remove(e); RaiseTotals(); } });
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        PrintCommand = new AsyncRelayCommand(async p =>
        {
            if (p is not RepSettlementRow row) return;
            await using var db = Session.NewDb();
            if (await RepSettlementPrint.BuildAsync(Session, db, row.Id) is { } r) Dialogs.ShowReport(r); else Dialogs.Error("التسوية غير موجودة");
        });
    }

    protected override bool ReloadOnActivate => true;
    protected override bool HasPendingInput => Expenses.Count > 0 || ReceivedCash > 0;
    protected override void ResetInput() { Expenses.Clear(); ReceivedCash = 0; ExpenseAmount = 0; ExpenseDescription = null; Notes = null; Date = DateTime.Today; RaiseTotals(); }

    public ObservableCollection<Data.ProjectDb.Entities.Warehouse> Vans { get; } = new();
    public ObservableCollection<Vehicle> Vehicles { get; } = new();
    public ObservableCollection<ExpenseDraft> Expenses { get; } = new();
    public ObservableCollection<RepSettlementRow> History { get; } = new();
    public ObservableCollection<RepSettlementRow> PendingCash { get; } = new();

    public Data.ProjectDb.Entities.Warehouse? Van { get => _van; set { if (SetProperty(ref _van, value)) { OnPropertyChanged(nameof(RepName)); Expenses.Clear(); Background(LoadVanAsync()); } } }
    public DateTime Date { get => _date; set => SetProperty(ref _date, value); }
    public string RepName => Van?.OwnerEmployee?.FullName is { } n ? $"المندوب: {n}" : "السيارة بلا مندوب";
    public decimal WalletBalance { get => _walletBalance; private set { if (SetProperty(ref _walletBalance, value)) RaiseTotals(); } }
    public decimal ExpenseAmount { get => _expenseAmount; set => SetProperty(ref _expenseAmount, value); }
    public string? ExpenseDescription { get => _expenseDescription; set => SetProperty(ref _expenseDescription, value); }
    public Vehicle? ExpenseVehicle { get => _expenseVehicle; set => SetProperty(ref _expenseVehicle, value); }
    public decimal ReceivedCash { get => _receivedCash; set { if (SetProperty(ref _receivedCash, value)) RaiseTotals(); } }
    public string? Notes { get => _notes; set => SetProperty(ref _notes, value); }
    public decimal ExpensesTotal => Expenses.Sum(e => e.Amount);
    public decimal Expected => WalletBalance - ExpensesTotal;
    public string CashHint => $"النقد المتوقع: في المحفظة {WalletBalance:N0} − مصاريف {ExpensesTotal:N0} = {Expected:N0} د.ع" + (ReceivedCash > 0 ? $" — الفرق {Expected - ReceivedCash:N0}" : "");
    /// <summary>حالة منتجات هذه السيارة: سوّاها أمين المخزن أم لا.</summary>
    public string ProductsText => _pendingProducts is { } p
        ? $"✔ سوّى أمين المخزن منتجات هذه السيارة ({p.SettlementNumber}): مرتجع {p.ReturnedPieces:N0} قطعة، مجاني {p.FreePieces:N0} قطعة — يمكنك تسوية النقد الآن"
        : "لم يسوِّ أمين المخزن منتجات هذه السيارة بعد؛ يمكنك تسوية النقد وحدها، والأفضل أن تُسوّى المنتجات أولًا لتتضح المبيعات";

    public RelayCommand AddExpenseCommand { get; }
    public RelayCommand RemoveExpenseCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand PrintCommand { get; }

    private void RaiseTotals()
    {
        OnPropertyChanged(nameof(ExpensesTotal)); OnPropertyChanged(nameof(Expected)); OnPropertyChanged(nameof(CashHint));
    }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var vanId = Van?.Id;
        Vans.Clear();
        foreach (var v in await db.Warehouses.AsNoTracking().Include(w => w.OwnerEmployee)
                     .Where(w => w.IsActive && w.WarehouseType == WarehouseType.RepVan && w.OwnerEmployeeId != null).OrderBy(w => w.Name).ToListAsync()) Vans.Add(v);
        Vehicles.Clear();
        foreach (var v in await db.Vehicles.AsNoTracking().Where(v => v.IsActive).OrderBy(v => v.VehicleName).ToListAsync()) Vehicles.Add(v);
        var ops = new RepOperationsService(db);
        PendingCash.Clear();
        foreach (var r in await ops.PendingCashSettlementsAsync()) PendingCash.Add(r);
        History.Clear();
        foreach (var r in await ops.GetSettlementsAsync(DateTime.Today.AddDays(-14), DateTime.Today)) History.Add(r);
        // السيارة المختارة، أو أول سيارة منتجاتها سُوّيت وتنتظر النقد
        _van = Vans.FirstOrDefault(v => v.Id == vanId)
               ?? Vans.FirstOrDefault(v => PendingCash.Any(p => p.RepName == v.OwnerEmployee?.FullName)) ?? Vans.FirstOrDefault();
        OnPropertyChanged(nameof(Van));
        OnPropertyChanged(nameof(RepName));
        await LoadVanAsync();
    }

    private async Task LoadVanAsync()
    {
        decimal wallet = 0;
        RepSettlementRow? pending = null;
        if (Van is { } van)
        {
            await using var db = Session.NewDb();
            if (van.OwnerEmployeeId is int repId)
            {
                wallet = await new RepsService(db).GetWalletBalanceAsync(repId);
                var name = van.OwnerEmployee?.FullName;
                pending = (await new RepOperationsService(db).PendingCashSettlementsAsync()).FirstOrDefault(p => p.RepName == name);
            }
            if (!ReferenceEquals(Van, van)) return;
            ExpenseVehicle = Vehicles.FirstOrDefault(v => v.AssignedEmployeeId == van.OwnerEmployeeId);
        }
        _pendingProducts = pending;
        WalletBalance = wallet;
        OnPropertyChanged(nameof(ProductsText));
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
        if (!Require(CanAdd, "تسوية النقد")) return;
        if (Van is null) { Dialogs.Error("اختر السيارة"); return; }
        if (ReceivedCash < 0) { Dialogs.Error("المبلغ المستلم لا يكون سالبًا"); return; }
        if (!Dialogs.Confirm($"تسوية نقد {Van.OwnerEmployee?.FullName} بتاريخ {Date:yyyy/MM/dd}: مصاريف {ExpensesTotal:N0}، مستلم {ReceivedCash:N0} د.ع، متوقع {Expected:N0}؟"))
            return;
        var request = new CashSettlementRequest(Van.Id, Date, Expenses.Select(e => new RepExpenseInput(e.Amount, e.Description, e.VehicleId)).ToList(), ReceivedCash, Session.UserId, Notes);
        await using var db = Session.NewDb();
        RepSettlement? settlement = null;
        if (await RunOperationAsync(async () =>
            {
                var (r, st) = await new RepOperationsService(db).SettleCashAsync(request);
                settlement = st;
                return r;
            }, "تمت تسوية النقد"))
        {
            StatusMessage = $"{StatusMessage} {settlement!.SettlementNumber}: متوقع {settlement.ExpectedCash:N0}، مستلم {settlement.ReceivedCash:N0}"
                            + (settlement.Difference != 0 ? $"، الفرق {settlement.Difference:N0} يبقى في ذمة المندوب" : "");
            Expenses.Clear();
            ReceivedCash = 0;
            Notes = null;
            await LoadAsync();
        }
    }
}
