using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Sales;

/// <summary>سطر مرتجع: المنتج، العبوة، الكمية، ومنها التالف.</summary>
public class ReturnLineDraft : ObservableObject
{
    private readonly CustomerReturnSectionViewModel _owner;
    private Item? _product;
    private ItemPackagingLevel? _level;
    private decimal _quantity;
    private decimal _damaged;

    public ReturnLineDraft(CustomerReturnSectionViewModel owner) => _owner = owner;

    public ObservableCollection<ItemPackagingLevel> Levels { get; } = new();
    public Item? Product
    {
        get => _product;
        set
        {
            if (!SetProperty(ref _product, value)) return;
            Levels.Clear();
            if (value is not null) foreach (var l in _owner.LevelsOf(value.Id)) Levels.Add(l);
            Level = Levels.FirstOrDefault();
        }
    }
    public ItemPackagingLevel? Level { get => _level; set => SetProperty(ref _level, value); }
    public decimal Quantity { get => _quantity; set => SetProperty(ref _quantity, value); }
    /// <summary>من الكمية: ما عاد تالفًا (يذهب لمخزن التالف).</summary>
    public decimal Damaged { get => _damaged; set => SetProperty(ref _damaged, value); }
}

/// <summary>
/// «مرتجع زبون»: بضاعة اشتراها وأعادها. السليم يعود للمخزن أو سيارة المندوب، والتالف لمخزن التالف؛
/// قيمته بسعر آخر بيع له تُخصم من دينه، أو تُرد نقدًا من محفظة المندوب.
/// </summary>
public class CustomerReturnSectionViewModel : SectionViewModel
{
    private Customer? _customer;
    private Data.ProjectDb.Entities.Warehouse? _warehouse;
    private Option<CustomerReturnSettlement> _settlement;
    private DateTime _date = DateTime.Today;
    private string _reason = "";
    private List<ItemPackagingLevel> _levels = new();

    public CustomerReturnSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Sales, "مرتجع زبون", Icons.Receive, "#B45309", "بضاعة أعادها زبون: السليم للمخزن والتالف لمخزن التالف، وقيمتها تُخصم من دينه أو تُرد نقدًا")
    {
        _settlement = Settlements[0];
        AddLineCommand = new RelayCommand(() => Lines.Add(new ReturnLineDraft(this)));
        RemoveLineCommand = new RelayCommand(p => { if (p is ReturnLineDraft l) Lines.Remove(l); });
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        PrintCommand = new RelayCommand(p => { if (p is CustomerReturnRow r) Print(r); });
        Lines.Add(new ReturnLineDraft(this));
    }

    protected override bool HasPendingInput => Lines.Any(l => l.Product is not null && l.Quantity > 0);

    protected override void ResetInput()
    {
        Customer = null;
        Settlement = Settlements[0];
        Date = DateTime.Today;
        Reason = "";
        Lines.Clear();
        Lines.Add(new ReturnLineDraft(this));
    }

    public IReadOnlyList<Option<CustomerReturnSettlement>> Settlements { get; } = new[]
    {
        new Option<CustomerReturnSettlement>(CustomerReturnSettlement.Debt, "خصم من دين الزبون"),
        new Option<CustomerReturnSettlement>(CustomerReturnSettlement.Cash, "رد نقدي من محفظة المندوب"),
    };
    public ObservableCollection<Customer> Customers { get; } = new();
    public ObservableCollection<Data.ProjectDb.Entities.Warehouse> Warehouses { get; } = new();
    public ObservableCollection<Item> Products { get; } = new();
    public ObservableCollection<ReturnLineDraft> Lines { get; } = new();
    public ObservableCollection<CustomerReturnRow> Recent { get; } = new();
    public Customer? Customer { get => _customer; set => SetProperty(ref _customer, value); }
    public Data.ProjectDb.Entities.Warehouse? Warehouse { get => _warehouse; set => SetProperty(ref _warehouse, value); }
    public Option<CustomerReturnSettlement> Settlement { get => _settlement; set => SetProperty(ref _settlement, value); }
    public DateTime Date { get => _date; set => SetProperty(ref _date, value); }
    public string Reason { get => _reason; set => SetProperty(ref _reason, value); }
    public RelayCommand AddLineCommand { get; }
    public RelayCommand RemoveLineCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand PrintCommand { get; }

    internal IEnumerable<ItemPackagingLevel> LevelsOf(int itemId) => _levels.Where(l => l.ItemId == itemId).OrderByDescending(l => l.EquivalentBaseUnits);

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var keepCustomer = Customer?.Id;
        var keepWarehouse = Warehouse?.Id;
        Customers.Clear();
        foreach (var c in await db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).ToListAsync()) Customers.Add(c);
        Warehouses.Clear();
        // مخزن المنتج التام أولًا ثم سيارات المندوبين
        foreach (var w in (await db.Warehouses.AsNoTracking().Where(w => w.IsActive && (w.WarehouseType == WarehouseType.FinishedGoods || w.WarehouseType == WarehouseType.RepVan))
                                   .ToListAsync()).OrderBy(w => w.WarehouseType == WarehouseType.RepVan).ThenBy(w => w.Name)) Warehouses.Add(w);
        Products.Clear();
        foreach (var i in await db.Items.AsNoTracking().Where(i => i.IsActive && i.SourcingMethod != SourcingMethod.Purchased).OrderBy(i => i.ItemName).ToListAsync()) Products.Add(i);
        var ids = Products.Select(p => p.Id).ToList();
        _levels = await db.ItemPackagingLevels.AsNoTracking().Where(l => ids.Contains(l.ItemId)).ToListAsync();
        Customer = Customers.FirstOrDefault(c => c.Id == keepCustomer);
        Warehouse = Warehouses.FirstOrDefault(w => w.Id == keepWarehouse) ?? Warehouses.FirstOrDefault();
        Recent.Clear();
        foreach (var r in await new CustomerReturnService(db).ListAsync(DateTime.Today.AddDays(-30), DateTime.Today)) Recent.Add(r);
    }

    private async Task SaveAsync()
    {
        if (!Require(CanAdd, "تسجيل مرتجع")) return;
        if (Customer is null || Warehouse is null) { Dialogs.Error("اختر الزبون والمخزن الذي يعود إليه السليم"); return; }
        var lines = Lines.Where(l => l.Product is not null && l.Level is not null && l.Quantity > 0)
                         .Select(l => new CustomerReturnLineInput(l.Product!.Id, l.Level!.Id, l.Quantity, l.Damaged)).ToList();
        await using var db = Session.NewDb();
        CustomerReturn? created = null;
        if (await RunOperationAsync(async () =>
            {
                var (r, c) = await new CustomerReturnService(db).CreateAsync(new CustomerReturnRequest(
                    Customer.Id, Warehouse.Id, Date, Settlement.Value, Reason, lines, Session.UserId));
                created = c;
                return r;
            }, "تم تسجيل المرتجع"))
        {
            StatusMessage = $"{created!.ReturnNumber} — {created.TotalAmount:N0} د.ع — {(created.Settlement == CustomerReturnSettlement.Debt ? "خُصم من دين الزبون" : "رُد نقدًا من محفظة المندوب")}";
            Lines.Clear();
            Lines.Add(new ReturnLineDraft(this));
            Reason = "";
            await LoadAsync();
        }
    }

    private void Print(CustomerReturnRow x)
    {
        var r = new ReportDocument { CompanyName = Session.ProjectName, Title = $"مرتجع زبون — {x.ReturnNumber}", PrintedBy = Session.FullName };
        r.HeaderFields.Add(new ReportField("الزبون", x.CustomerName));
        r.HeaderFields.Add(new ReportField("التاريخ", x.ReturnDate.ToString("yyyy/MM/dd")));
        r.HeaderFields.Add(new ReportField("المخزن", x.WarehouseName + (x.RepName is null ? "" : $" — {x.RepName}")));
        r.HeaderFields.Add(new ReportField("التسوية", x.SettlementText));
        r.HeaderFields.Add(new ReportField("السبب", x.Reason));
        r.Columns.Add("الأصناف");
        r.Rows.Add(new[] { x.Items });
        r.Total("قيمة المرتجع", $"{x.TotalAmount:N0} د.ع", true);
        r.Signatures.AddRange(new[] { "الزبون", "المستلم", "المحاسب" });
        Dialogs.ShowReport(r);
    }
}
