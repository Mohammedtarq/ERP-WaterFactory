using System.Collections.ObjectModel;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Suppliers;

public class SuppliersModuleViewModel : ModuleViewModel
{
    public SuppliersModuleViewModel(AppSession s, IDialogService d)
        : base("الموردون والمشتريات", Icons.Suppliers, ModuleColors.Suppliers)
    {
        Add(new SuppliersSectionViewModel(s, d));
        Add(new PurchaseOrdersSectionViewModel(s, d));
        Add(new GoodsReceiptSectionViewModel(s, d));
        Add(new SupplierStatementSectionViewModel(s, d));
    }
}

// ============================ الموردون ============================
public class SuppliersSectionViewModel : CrudSectionViewModel<Supplier>
{
    public SuppliersSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Suppliers, "الموردون", Icons.People, "#F59E0B", "بيانات الموردين وشروط الدفع الافتراضية") { }

    public IReadOnlyList<Option<SupplierPaymentTerms>> TermsOptions { get; } = ArabicLabels.OptionsOf<SupplierPaymentTerms>();

    protected override int GetId(Supplier e) => e.Id;
    protected override string Describe(Supplier e) => e.Name;
    protected override bool Matches(Supplier e, string t) => base.Matches(e, t) || (e.Phone?.Contains(t) ?? false);
    protected override Task<List<Supplier>> QueryAsync(ProjectDbContext db) => db.Suppliers.AsNoTracking().OrderBy(s => s.Name).ToListAsync();
    protected override Supplier CreateNew() => new() { DefaultPaymentTerms = SupplierPaymentTerms.Credit };
    protected override string? Validate(Supplier e) => string.IsNullOrWhiteSpace(e.Name) ? "أدخل اسم المورد" : null;

    protected override Task BeforeSaveAsync(ProjectDbContext db, Supplier e)
    {
        e.Name = e.Name.Trim();
        return Task.CompletedTask;
    }
}

// ============================ أوامر الشراء ============================
public class PurchaseLineInput : ObservableObject
{
    private Item? _item;
    private decimal _quantity;
    private decimal _unitCost;
    private readonly Action _changed;

    public PurchaseLineInput(Action changed) => _changed = changed;

    public Item? Item { get => _item; set => SetProperty(ref _item, value); }
    public decimal Quantity { get => _quantity; set { if (SetProperty(ref _quantity, value)) { OnPropertyChanged(nameof(LineTotal)); _changed(); } } }
    public decimal UnitCost { get => _unitCost; set { if (SetProperty(ref _unitCost, value)) { OnPropertyChanged(nameof(LineTotal)); _changed(); } } }
    public decimal LineTotal => Quantity * UnitCost;
}

public class PurchaseOrderRow
{
    public int Id { get; init; }
    public string PONumber { get; init; } = "";
    public string SupplierName { get; init; } = "";
    public string WarehouseName { get; init; } = "";
    public DateTime OrderDate { get; init; }
    public DateTime? ExpectedDeliveryDate { get; init; }
    public string StatusLabel { get; init; } = "";
    public PurchaseOrderStatus Status { get; init; }
    public string TermsLabel { get; init; } = "";
    public decimal AdvanceAmount { get; init; }
    public decimal Total { get; init; }
}

public class PurchaseOrdersSectionViewModel : SectionViewModel
{
    private bool _isComposing;
    private Supplier? _supplier;
    private Data.ProjectDb.Entities.Warehouse? _warehouse;
    private DateTime _orderDate = DateTime.Today;
    private DateTime? _expectedDate;
    private Option<SupplierPaymentTerms> _terms;
    private decimal _advanceAmount;

    public PurchaseOrdersSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Suppliers, "أوامر الشراء", Icons.Order, "#0EA5E9", "أوامر الشراء مع الدفعات المقدمة")
    {
        _terms = TermsOptions[1];
        NewOrderCommand = new RelayCommand(StartNew);
        AddLineCommand = new RelayCommand(() => Lines.Add(new PurchaseLineInput(RaiseTotal)));
        RemoveLineCommand = new RelayCommand(p => { if (p is PurchaseLineInput l) { Lines.Remove(l); RaiseTotal(); } });
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(() => IsComposing = false);
        CancelOrderCommand = new AsyncRelayCommand(p => p is PurchaseOrderRow r ? CancelOrderAsync(r) : Task.CompletedTask);
    }

    public IReadOnlyList<Option<SupplierPaymentTerms>> TermsOptions { get; } = ArabicLabels.OptionsOf<SupplierPaymentTerms>();
    public ObservableCollection<Supplier> SuppliersLookup { get; } = new();
    public ObservableCollection<Data.ProjectDb.Entities.Warehouse> Warehouses { get; } = new();
    public ObservableCollection<Item> ItemsLookup { get; } = new();
    public ObservableCollection<PurchaseOrderRow> Orders { get; } = new();
    public ObservableCollection<PurchaseLineInput> Lines { get; } = new();

    public bool IsComposing { get => _isComposing; private set => SetProperty(ref _isComposing, value); }
    public Supplier? Supplier
    {
        get => _supplier;
        set
        {
            if (SetProperty(ref _supplier, value) && value?.DefaultPaymentTerms is SupplierPaymentTerms t)
                Terms = TermsOptions.First(o => o.Value == t);
        }
    }
    public Data.ProjectDb.Entities.Warehouse? Warehouse { get => _warehouse; set => SetProperty(ref _warehouse, value); }
    public DateTime OrderDate { get => _orderDate; set => SetProperty(ref _orderDate, value); }
    public DateTime? ExpectedDate { get => _expectedDate; set => SetProperty(ref _expectedDate, value); }
    public Option<SupplierPaymentTerms> Terms
    {
        get => _terms;
        set { if (SetProperty(ref _terms, value)) OnPropertyChanged(nameof(NeedsAdvance)); }
    }
    public bool NeedsAdvance => Terms.Value == SupplierPaymentTerms.AdvancePlusCredit;
    public decimal AdvanceAmount { get => _advanceAmount; set => SetProperty(ref _advanceAmount, value); }
    public decimal Total => Lines.Sum(l => l.LineTotal);

    public RelayCommand NewOrderCommand { get; }
    public RelayCommand AddLineCommand { get; }
    public RelayCommand RemoveLineCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand CancelOrderCommand { get; }

    private void RaiseTotal() => OnPropertyChanged(nameof(Total));

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        if (SuppliersLookup.Count == 0)
        {
            foreach (var x in await db.Suppliers.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync()) SuppliersLookup.Add(x);
            foreach (var x in await db.Warehouses.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync()) Warehouses.Add(x);
            foreach (var x in await db.Items.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.ItemName).ToListAsync()) ItemsLookup.Add(x);
        }
        var rows = await db.PurchaseOrders.AsNoTracking().OrderByDescending(p => p.OrderDate).ThenByDescending(p => p.Id)
            .Select(p => new { p.Id, p.PONumber, Supplier = p.Supplier.Name, Warehouse = p.Warehouse.Name, p.OrderDate, p.ExpectedDeliveryDate,
                               p.Status, p.PaymentTerms, p.AdvanceAmount, Total = p.Lines.Sum(l => l.QuantityOrdered * l.ExpectedUnitCost) })
            .ToListAsync();
        Orders.Clear();
        foreach (var p in rows)
            Orders.Add(new PurchaseOrderRow
            {
                Id = p.Id, PONumber = p.PONumber, SupplierName = p.Supplier, WarehouseName = p.Warehouse, OrderDate = p.OrderDate,
                ExpectedDeliveryDate = p.ExpectedDeliveryDate, Status = p.Status, StatusLabel = ArabicLabels.Of(p.Status),
                TermsLabel = ArabicLabels.Of(p.PaymentTerms), AdvanceAmount = p.AdvanceAmount, Total = p.Total
            });
    }

    private void StartNew()
    {
        if (!Require(CanAdd, "إنشاء أوامر الشراء")) return;
        Lines.Clear();
        Lines.Add(new PurchaseLineInput(RaiseTotal));
        Supplier = null;
        Warehouse = Warehouses.FirstOrDefault();
        OrderDate = DateTime.Today;
        ExpectedDate = null;
        AdvanceAmount = 0;
        RaiseTotal();
        IsComposing = true;
    }

    private async Task SaveAsync()
    {
        if (Supplier is null || Warehouse is null) { Dialogs.Error("اختر المورد والمخزن المستهدف"); return; }
        var lines = Lines.Where(l => l.Item is not null && l.Quantity > 0).ToList();
        if (lines.Count == 0) { Dialogs.Error("أضف صنفًا واحدًا على الأقل بكمية أكبر من صفر"); return; }
        if (lines.Any(l => l.UnitCost < 0)) { Dialogs.Error("التكلفة لا يمكن أن تكون سالبة"); return; }
        if (NeedsAdvance && (AdvanceAmount <= 0 || AdvanceAmount > Total)) { Dialogs.Error("الدفعة المقدمة يجب أن تكون أكبر من صفر ولا تتجاوز إجمالي الأمر"); return; }

        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new SupplierPurchasingService(db).CreatePurchaseOrderAsync(
                Supplier.Id, Warehouse.Id, OrderDate, ExpectedDate, Terms.Value, NeedsAdvance ? AdvanceAmount : 0,
                lines.Select(l => new PurchaseOrderLineInput(l.Item!.Id, l.Quantity, l.UnitCost)).ToList(), Session.UserId),
            "تم إنشاء أمر الشراء"))
        {
            IsComposing = false;
            await LoadAsync();
        }
    }

    private async Task CancelOrderAsync(PurchaseOrderRow row)
    {
        if (!Require(CanEdit, "إلغاء أوامر الشراء")) return;
        if (row.Status is PurchaseOrderStatus.Completed or PurchaseOrderStatus.Cancelled or PurchaseOrderStatus.PartiallyReceived)
        { Dialogs.Error("لا يمكن إلغاء أمر مستلم (كليًا أو جزئيًا) أو ملغى مسبقًا"); return; }
        if (!Dialogs.Confirm($"إلغاء أمر الشراء {row.PONumber}؟")) return;
        await using var db = Session.NewDb();
        await db.PurchaseOrders.Where(p => p.Id == row.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PurchaseOrderStatus.Cancelled));
        StatusMessage = $"تم إلغاء {row.PONumber}";
        await LoadAsync();
    }
}

// ============================ استلام البضاعة ============================
public class ReceiptLineInput : ObservableObject
{
    private decimal _quantityNow;
    private decimal _unitCost;
    private string _batchNumber = "";
    private DateTime? _expiryDate;

    public int PurchaseOrderLineId { get; init; }
    public int ItemId { get; init; }
    public string ItemName { get; init; } = "";
    public decimal Ordered { get; init; }
    public decimal PreviouslyReceived { get; init; }
    public decimal Remaining => Ordered - PreviouslyReceived;
    public decimal QuantityNow { get => _quantityNow; set => SetProperty(ref _quantityNow, value); }
    public decimal UnitCost { get => _unitCost; set => SetProperty(ref _unitCost, value); }
    public string BatchNumber { get => _batchNumber; set => SetProperty(ref _batchNumber, value); }
    public DateTime? ExpiryDate { get => _expiryDate; set => SetProperty(ref _expiryDate, value); }
}

public class GoodsReceiptRow
{
    public string ReceiptNumber { get; init; } = "";
    public DateTime ReceiptDate { get; init; }
    public string SupplierName { get; init; } = "";
    public string? PONumber { get; init; }
    public string? SupplierInvoiceNumber { get; init; }
    public decimal Total { get; init; }
    public string? EntryNumber { get; init; }
}

public class GoodsReceiptSectionViewModel : SectionViewModel
{
    private PurchaseOrderRow? _selectedOrder;
    private DateTime _receiptDate = DateTime.Today;
    private string? _supplierInvoiceNumber;

    public GoodsReceiptSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Suppliers, "استلام البضاعة", Icons.Receive, "#10B981", "الاستلام مقابل أمر الشراء وتحديث المخزون")
    {
        ReceiveCommand = new AsyncRelayCommand(ReceiveAsync);
        FillRemainingCommand = new RelayCommand(() => { foreach (var l in Lines) l.QuantityNow = l.Remaining; });
    }

    public ObservableCollection<PurchaseOrderRow> OpenOrders { get; } = new();
    public ObservableCollection<ReceiptLineInput> Lines { get; } = new();
    public ObservableCollection<GoodsReceiptRow> Receipts { get; } = new();

    public PurchaseOrderRow? SelectedOrder { get => _selectedOrder; set { if (SetProperty(ref _selectedOrder, value)) Background(LoadLinesAsync()); } }
    public DateTime ReceiptDate { get => _receiptDate; set => SetProperty(ref _receiptDate, value); }
    public string? SupplierInvoiceNumber { get => _supplierInvoiceNumber; set => SetProperty(ref _supplierInvoiceNumber, value); }

    public AsyncRelayCommand ReceiveCommand { get; }
    public RelayCommand FillRemainingCommand { get; }
    protected override bool ReloadOnActivate => true;

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var open = new[] { PurchaseOrderStatus.Sent, PurchaseOrderStatus.PartiallyReceived, PurchaseOrderStatus.Draft };
        var orders = await db.PurchaseOrders.AsNoTracking().Where(p => open.Contains(p.Status)).OrderBy(p => p.OrderDate)
            .Select(p => new PurchaseOrderRow { Id = p.Id, PONumber = p.PONumber, SupplierName = p.Supplier.Name, WarehouseName = p.Warehouse.Name,
                                                OrderDate = p.OrderDate, Status = p.Status })
            .ToListAsync();
        OpenOrders.Clear();
        foreach (var o in orders) OpenOrders.Add(o);

        var receipts = await db.GoodsReceipts.AsNoTracking().OrderByDescending(g => g.ReceiptDate).ThenByDescending(g => g.Id).Take(200)
            .Select(g => new GoodsReceiptRow
            {
                ReceiptNumber = g.ReceiptNumber, ReceiptDate = g.ReceiptDate, SupplierName = g.Supplier.Name,
                PONumber = g.PurchaseOrder != null ? g.PurchaseOrder.PONumber : null, SupplierInvoiceNumber = g.SupplierInvoiceNumber,
                Total = g.Lines.Sum(l => l.QuantityReceived * l.UnitCost), EntryNumber = g.JournalEntry != null ? g.JournalEntry.EntryNumber : null
            }).ToListAsync();
        Receipts.Clear();
        foreach (var r in receipts) Receipts.Add(r);
    }

    private async Task LoadLinesAsync()
    {
        Lines.Clear();
        if (SelectedOrder is null) return;
        await using var db = Session.NewDb();
        var poLines = await db.PurchaseOrderLines.AsNoTracking().Where(l => l.PurchaseOrderId == SelectedOrder.Id).Include(l => l.Item).ToListAsync();
        foreach (var l in poLines)
            Lines.Add(new ReceiptLineInput
            {
                PurchaseOrderLineId = l.Id, ItemId = l.ItemId, ItemName = l.Item.ItemName, Ordered = l.QuantityOrdered,
                PreviouslyReceived = l.QuantityReceived, UnitCost = l.ExpectedUnitCost,
                BatchNumber = $"{SelectedOrder.PONumber}-{DateTime.Today:yyMMdd}"
            });
    }

    private async Task ReceiveAsync()
    {
        if (!Require(CanAdd, "استلام البضاعة")) return;
        if (SelectedOrder is null) { Dialogs.Error("اختر أمر الشراء"); return; }
        var lines = Lines.Where(l => l.QuantityNow > 0).ToList();
        if (lines.Count == 0) { Dialogs.Error("أدخل كمية مستلمة لسطر واحد على الأقل"); return; }
        if (lines.Any(l => string.IsNullOrWhiteSpace(l.BatchNumber))) { Dialogs.Error("أدخل رقم التشغيلة لكل سطر مستلم"); return; }
        var over = lines.FirstOrDefault(l => l.QuantityNow > l.Remaining);
        if (over is not null && !Dialogs.Confirm($"الكمية المستلمة من {over.ItemName} تتجاوز المتبقي ({over.Remaining:0.###}). متابعة؟")) return;

        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new SupplierPurchasingService(db).ReceiveGoodsAsync(
                SelectedOrder.Id, ReceiptDate, SupplierInvoiceNumber,
                lines.Select(l => new GoodsReceiptLineInput(l.ItemId, l.PurchaseOrderLineId, l.QuantityNow, l.UnitCost, l.BatchNumber.Trim(), l.ExpiryDate)).ToList(),
                Session.UserId),
            $"تم استلام بضاعة أمر الشراء {SelectedOrder.PONumber} وتحديث المخزون"))
        {
            SupplierInvoiceNumber = null;
            SelectedOrder = null;
            await LoadAsync();
        }
    }
}

// ============================ كشف حساب المورد ============================
public class StatementRow
{
    public DateTime Date { get; init; }
    public string DocType { get; init; } = "";
    public string DocNumber { get; init; } = "";
    public string Description { get; init; } = "";
    public decimal Debit { get; init; }
    public decimal Credit { get; init; }
    public decimal RunningBalance { get; set; }
}

public class SupplierStatementSectionViewModel : SectionViewModel
{
    private Supplier? _supplier;

    public SupplierStatementSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Suppliers, "كشف حساب المورد", Icons.Statement, "#8B5CF6", "الاستلامات والدفعات والرصيد المستحق") { }

    public ObservableCollection<Supplier> SuppliersLookup { get; } = new();
    public ObservableCollection<StatementRow> Rows { get; } = new();
    public Supplier? Supplier { get => _supplier; set { if (SetProperty(ref _supplier, value)) Background(LoadStatementAsync()); } }

    protected override bool ReloadOnActivate => true;

    /// <summary>موجب = مستحق للمورد علينا.</summary>
    public decimal Balance => Rows.LastOrDefault()?.RunningBalance ?? 0;
    public decimal TotalDebit => Rows.Sum(r => r.Debit);
    public decimal TotalCredit => Rows.Sum(r => r.Credit);

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        SuppliersLookup.Clear();
        foreach (var x in await db.Suppliers.AsNoTracking().OrderBy(x => x.Name).ToListAsync()) SuppliersLookup.Add(x);
        await LoadStatementAsync();
    }

    private async Task LoadStatementAsync()
    {
        Rows.Clear();
        if (Supplier is not null)
        {
            await using var db = Session.NewDb();
            var receipts = await db.GoodsReceipts.AsNoTracking().Where(g => g.SupplierId == Supplier.Id && g.Status == DocumentStatus.Posted)
                .Select(g => new StatementRow { Date = g.ReceiptDate, DocType = "استلام بضاعة", DocNumber = g.ReceiptNumber,
                                                Description = g.SupplierInvoiceNumber != null ? "فاتورة المورد " + g.SupplierInvoiceNumber : "استلام",
                                                Credit = g.Lines.Sum(l => l.QuantityReceived * l.UnitCost) })
                .ToListAsync();
            var vouchers = await db.Vouchers.AsNoTracking().Where(v => v.PartyType == VoucherPartyType.Supplier && v.PartyId == Supplier.Id)
                .Select(v => new { v.VoucherDate, v.VoucherType, v.VoucherNumber, v.Notes, v.Amount }).ToListAsync();

            var all = receipts.Concat(vouchers.Select(v => new StatementRow
            {
                Date = v.VoucherDate, DocType = ArabicLabels.Of(v.VoucherType), DocNumber = v.VoucherNumber, Description = v.Notes ?? "",
                Debit = v.VoucherType == VoucherType.Payment ? v.Amount : 0,
                Credit = v.VoucherType == VoucherType.Receipt ? v.Amount : 0
            })).OrderBy(r => r.Date).ThenBy(r => r.DocNumber).ToList();

            decimal running = 0;
            foreach (var r in all)
            {
                r.RunningBalance = running += r.Credit - r.Debit;
                Rows.Add(r);
            }
        }
        OnPropertyChanged(nameof(Balance));
        OnPropertyChanged(nameof(TotalDebit));
        OnPropertyChanged(nameof(TotalCredit));
    }
}
