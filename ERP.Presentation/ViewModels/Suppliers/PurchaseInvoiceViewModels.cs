using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Suppliers;

// ============================ فاتورة شراء مباشرة ============================
/// <summary>سطر في فاتورة الشراء: الصنف بوحدة الشراء (كرتون، باليت، طن...) وسعر الوحدة كما في فاتورة المورد.</summary>
public class PurchaseInvoiceLine : ObservableObject
{
    private readonly Func<int, Task<List<PurchaseUnitOption>>> _units;
    private readonly Action _changed;
    private Item? _material;
    private PurchaseUnitOption? _unit;
    private decimal _quantity;
    private decimal _unitPrice;

    public PurchaseInvoiceLine(Func<int, Task<List<PurchaseUnitOption>>> units, Action changed)
    {
        _units = units;
        _changed = changed;
    }

    public ObservableCollection<PurchaseUnitOption> Units { get; } = new();
    /// <summary>المادة المشتراة (الاسم Material لا Item: ربط WPF على خاصية اسمها Item يتعارض مع مفهرس المجموعات).</summary>
    public Item? Material
    {
        get => _material;
        set { if (SetProperty(ref _material, value)) LoadUnits = LoadUnitsAsync(); }
    }
    /// <summary>تحميل وحدات الصنف المختار (تنتظره الاختبارات).</summary>
    public Task LoadUnits { get; private set; } = Task.CompletedTask;
    public PurchaseUnitOption? Unit { get => _unit; set { if (SetProperty(ref _unit, value)) Raise(); } }
    public decimal Quantity { get => _quantity; set { if (SetProperty(ref _quantity, value)) Raise(); } }
    public decimal UnitPrice { get => _unitPrice; set { if (SetProperty(ref _unitPrice, value)) Raise(); } }
    public decimal Pieces => Unit is null ? 0 : Math.Round(Quantity * Unit.PiecesPerUnit, 3);
    public decimal LineTotal => Math.Round(Quantity * UnitPrice, 2);
    public decimal CostPerPiece => Pieces > 0 ? Math.Round(LineTotal / Pieces, 4) : 0;

    private async Task LoadUnitsAsync()
    {
        Units.Clear();
        Unit = null;
        if (Material is null) return;
        foreach (var u in await _units(Material.Id)) Units.Add(u);
        // الأكبر عادةً هو وحدة الشراء (كرتون/باليت)
        Unit = Units.Where(u => u.Label is not ("كغم" or "طن")).OrderByDescending(u => u.PiecesPerUnit).FirstOrDefault() ?? Units.FirstOrDefault();
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(Pieces));
        OnPropertyChanged(nameof(LineTotal));
        OnPropertyChanged(nameof(CostPerPiece));
        _changed();
    }
}

/// <summary>
/// فاتورة شراء مباشرة (الوضع البسيط): المورد ← الأصناف بوحدات الشراء ← دخول المخزن بالقطع،
/// وذمة المورد وقيدها، وتحديث متوسط الكلفة، في خطوة واحدة. أمر الشراء المسبق اختياري من شاشته.
/// </summary>
public class PurchaseInvoiceSectionViewModel : SectionViewModel
{
    protected override bool HasPendingInput => Lines.Any(l => l.Material is not null);

    private Supplier? _supplier;
    private Data.ProjectDb.Entities.Warehouse? _warehouse;
    private DateTime _invoiceDate = DateTime.Today;
    private string? _supplierInvoiceNumber;
    private decimal _paidNow;

    public PurchaseInvoiceSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Suppliers, "فاتورة شراء", Icons.Receive, "#F59E0B", "الشراء المباشر بوحدات المورد: يدخل المخزن ويُسجَّل الدين ويُحدَّث متوسط الكلفة معًا")
    {
        AddLineCommand = new RelayCommand(() => Lines.Add(NewLine()));
        RemoveLineCommand = new RelayCommand(p => { if (p is PurchaseInvoiceLine l) { Lines.Remove(l); RaiseTotals(); } });
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        Lines.Add(NewLine());
    }

    public ObservableCollection<Supplier> SuppliersLookup { get; } = new();
    public ObservableCollection<Data.ProjectDb.Entities.Warehouse> Warehouses { get; } = new();
    public ObservableCollection<Item> ItemsLookup { get; } = new();
    public ObservableCollection<PurchaseInvoiceLine> Lines { get; } = new();

    public Supplier? Supplier { get => _supplier; set => SetProperty(ref _supplier, value); }
    public Data.ProjectDb.Entities.Warehouse? Warehouse { get => _warehouse; set => SetProperty(ref _warehouse, value); }
    public DateTime InvoiceDate { get => _invoiceDate; set => SetProperty(ref _invoiceDate, value); }
    public string? SupplierInvoiceNumber { get => _supplierInvoiceNumber; set => SetProperty(ref _supplierInvoiceNumber, value); }
    public decimal PaidNow { get => _paidNow; set { if (SetProperty(ref _paidNow, value)) RaiseTotals(); } }
    public decimal Total => Lines.Sum(l => l.LineTotal);
    public decimal Remaining => Total - PaidNow;

    public RelayCommand AddLineCommand { get; }
    public RelayCommand RemoveLineCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }

    private PurchaseInvoiceLine NewLine() => new(async itemId =>
    {
        await using var db = Session.NewDb();
        return await new SupplierPurchasingService(db).GetPurchaseUnitsAsync(itemId);
    }, RaiseTotals);

    private void RaiseTotals()
    {
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(Remaining));
    }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var keepSupplier = Supplier?.Id;
        var keepWarehouse = Warehouse?.Id;
        SuppliersLookup.Clear();
        foreach (var x in await db.Suppliers.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync()) SuppliersLookup.Add(x);
        Warehouses.Clear();
        foreach (var x in await db.Warehouses.AsNoTracking()
                     .Where(x => x.IsActive && (x.WarehouseType == WarehouseType.RawMaterial || x.WarehouseType == WarehouseType.FinishedGoods))
                     .OrderBy(x => x.WarehouseType == WarehouseType.RawMaterial ? 0 : 1).ThenBy(x => x.Name).ToListAsync())
            Warehouses.Add(x);
        ItemsLookup.Clear();
        foreach (var x in await db.Items.AsNoTracking().Where(x => x.IsActive && x.SourcingMethod != SourcingMethod.Manufactured)
                     .OrderBy(x => x.ItemName).ToListAsync())
            ItemsLookup.Add(x);
        Supplier = SuppliersLookup.FirstOrDefault(x => x.Id == keepSupplier);
        Warehouse = Warehouses.FirstOrDefault(x => x.Id == keepWarehouse) ?? Warehouses.FirstOrDefault();
    }

    private async Task SaveAsync()
    {
        if (!Require(CanAdd, "تسجيل فواتير الشراء")) return;
        if (Supplier is null || Warehouse is null) { Dialogs.Error("اختر المورد ومخزن الاستلام"); return; }
        var lines = Lines.Where(l => l.Material is not null && l.Unit is not null && l.Quantity > 0).ToList();
        if (lines.Count == 0) { Dialogs.Error("أضف صنفًا واحدًا على الأقل بوحدة وكمية"); return; }
        if (PaidNow < 0 || PaidNow > Total) { Dialogs.Error("المدفوع الآن بين صفر وإجمالي الفاتورة"); return; }

        await using var db = Session.NewDb();
        var input = lines.Select(l => new PurchaseInvoiceLineInput(l.Material!.Id, l.Unit!.Label, l.Unit.PiecesPerUnit, l.Quantity, l.UnitPrice)).ToList();
        int? receiptId = null;
        if (await RunOperationAsync(async () =>
            {
                var (r, id) = await new SupplierPurchasingService(db).PurchaseInvoiceAsync(Supplier.Id, Warehouse.Id, InvoiceDate, SupplierInvoiceNumber,
                                                                                         input, PaidNow, Session.UserId);
                receiptId = id;
                return r;
            }, $"سُجّلت فاتورة الشراء بقيمة {Total:N0} د.ع"))
        {
            LastReceiptId = receiptId;
            Lines.Clear();
            Lines.Add(NewLine());
            PaidNow = 0;
            SupplierInvoiceNumber = null;
            RaiseTotals();
        }
    }

    /// <summary>آخر استلام سُجّل من الشاشة (للطباعة وللاختبارات).</summary>
    public int? LastReceiptId { get; private set; }
}

// ============================ مقترح الشراء ============================
/// <summary>ما يجب طلبه الآن من المواد الأولية حتى لا يتوقف الإنتاج قبل وصول البضاعة (حسب مدة تجهيز كل مادة).</summary>
public class ReorderSectionViewModel : SectionViewModel
{
    private int _historyDays = 30;
    private int _coverDays = 30;

    public ReorderSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Suppliers, "مقترح الشراء", Icons.Alert, "#DC2626", "الاستهلاك اليومي ومدة التجهيز: ما يجب طلبه الآن وبأي كمية")
    {
        PrintCommand = new RelayCommand(Print);
    }

    protected override bool ReloadOnActivate => true;

    public int HistoryDays { get => _historyDays; set { if (SetProperty(ref _historyDays, Math.Max(7, value))) Background(LoadAsync()); } }
    public int CoverDays { get => _coverDays; set { if (SetProperty(ref _coverDays, Math.Max(7, value))) Background(LoadAsync()); } }
    public ObservableCollection<ReorderRow> Rows { get; } = new();
    public int NeedsOrderCount => Rows.Count(r => r.NeedsOrder);
    public RelayCommand PrintCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var rows = await new ReorderService(db).SuggestAsync(HistoryDays, CoverDays);
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        OnPropertyChanged(nameof(NeedsOrderCount));
        StatusMessage = NeedsOrderCount == 0 ? "كل المواد تكفي حتى وصول طلب جديد" : $"{NeedsOrderCount} مادة يجب طلبها الآن";
    }

    private void Print()
    {
        var r = new ReportDocument { CompanyName = Session.ProjectName, Title = "مقترح الشراء", PrintedBy = Session.FullName,
                                     Notes = $"الاستهلاك محسوب على آخر {HistoryDays} يومًا، والكمية المقترحة تكفي {CoverDays} يومًا بعد الوصول، مع {ReorderService.SafetyDays} أيام أمان." };
        r.Columns.AddRange(new[] { "الرمز", "المادة", "الرصيد", "الاستهلاك اليومي", "يكفي (يوم)", "مدة التجهيز", "نقطة الطلب", "الكمية المقترحة", "الحالة" });
        foreach (var x in Rows)
            r.Rows.Add(new[] { x.ItemCode, x.ItemName, $"{x.OnHand:N0}", $"{x.DailyUse:N0}", x.CoverDays?.ToString("N1") ?? "—", x.LeadTimeDays.ToString(),
                               $"{x.ReorderPoint:N0}", $"{x.SuggestedQuantity:N0}", x.Status });
        Dialogs.ShowReport(r);
    }
}
