using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;

namespace ERP.Presentation.ViewModels.Warehouse;

public class DamagedSaleLineRow
{
    public int ItemId { get; init; }
    public string ItemName { get; init; } = "";
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal Amount => Math.Round(Quantity * UnitPrice, 2);
}

/// <summary>بيع المواد التالفة نقدًا لجهة (مثل بائع الخردة): المواد من مخزن التالف، والنقد للصندوق، وفاتورة مطبوعة.</summary>
public class DamagedSalesSectionViewModel : SectionViewModel
{
    private string _buyer = "";
    private DateTime _date = DateTime.Today;
    private string? _notes;
    private DamagedStockRow? _lineItem;
    private decimal _lineQty;
    private decimal _linePrice;
    private DateTime _from = DateTime.Today.AddDays(-30);
    private DateTime _to = DateTime.Today;

    public DamagedSalesSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Warehouse, "بيع المواد التالفة", Icons.Alert, "#DC2626", "بيع ما في مخزن التالف نقدًا لجهة، بفاتورة وإخراج مخزني")
    {
        AddLineCommand = new RelayCommand(AddLine);
        RemoveLineCommand = new RelayCommand(p => { if (p is DamagedSaleLineRow r) { Lines.Remove(r); OnPropertyChanged(nameof(Total)); } });
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        PrintCommand = new AsyncRelayCommand(p => p is DamagedSaleRow r ? PrintAsync(db => DocumentReports.DamagedSaleAsync(Session, db, r.Id)) : Task.CompletedTask);
    }

    protected override bool HasPendingInput => Lines.Count > 0;
    protected override bool ReloadOnActivate => true;

    public ObservableCollection<DamagedStockRow> Available { get; } = new();
    public ObservableCollection<DamagedSaleLineRow> Lines { get; } = new();
    public ObservableCollection<DamagedSaleRow> Sales { get; } = new();
    public RelayCommand AddLineCommand { get; }
    public RelayCommand RemoveLineCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand PrintCommand { get; }

    public string Buyer { get => _buyer; set => SetProperty(ref _buyer, value); }
    public DateTime Date { get => _date; set => SetProperty(ref _date, value); }
    public string? Notes { get => _notes; set => SetProperty(ref _notes, value); }
    public DamagedStockRow? LineItem { get => _lineItem; set => SetProperty(ref _lineItem, value); }
    public decimal LineQuantity { get => _lineQty; set => SetProperty(ref _lineQty, value); }
    public decimal LinePrice { get => _linePrice; set => SetProperty(ref _linePrice, value); }
    public decimal Total => Lines.Sum(l => l.Amount);
    public DateTime From { get => _from; set { if (SetProperty(ref _from, value)) Background(LoadSalesAsync()); } }
    public DateTime To { get => _to; set { if (SetProperty(ref _to, value)) Background(LoadSalesAsync()); } }
    public decimal SalesTotal => Sales.Sum(x => x.TotalAmount);

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        Available.Clear();
        foreach (var a in await new DamagedSaleService(db).GetAvailableAsync()) Available.Add(a);
        await LoadSalesAsync();
    }

    private async Task LoadSalesAsync()
    {
        await using var db = Session.NewDb();
        Sales.Clear();
        foreach (var x in await new DamagedSaleService(db).GetListAsync(From, To)) Sales.Add(x);
        OnPropertyChanged(nameof(SalesTotal));
    }

    private void AddLine()
    {
        if (LineItem is null) { Dialogs.Error("اختر المادة من مخزن التالف"); return; }
        if (LineQuantity <= 0) { Dialogs.Error("أدخل الكمية"); return; }
        if (LineQuantity > LineItem.Available) { Dialogs.Error($"المتاح في مخزن التالف {LineItem.Available:#,0.###} فقط"); return; }
        if (Lines.Any(l => l.ItemId == LineItem.ItemId)) { Dialogs.Error("المادة مضافة — احذفها وأضفها بالكمية الكلية"); return; }
        Lines.Add(new DamagedSaleLineRow { ItemId = LineItem.ItemId, ItemName = LineItem.ItemName, Quantity = LineQuantity, UnitPrice = LinePrice });
        OnPropertyChanged(nameof(Total));
        LineItem = null;
        LineQuantity = 0;
        LinePrice = 0;
    }

    private async Task SaveAsync()
    {
        if (!Require(CanAdd, "بيع المواد التالفة")) return;
        if (string.IsNullOrWhiteSpace(Buyer)) { Dialogs.Error("اكتب اسم المشتري"); return; }
        if (Lines.Count == 0) { Dialogs.Error("أضف مادة واحدة على الأقل"); return; }
        await using var db = Session.NewDb();
        var (result, sale) = await new DamagedSaleService(db).CreateAsync(Buyer, Date,
            Lines.Select(l => new DamagedSaleLineInput(l.ItemId, l.Quantity, l.UnitPrice)).ToList(), Notes, Session.UserId);
        if (!result.Success) { Dialogs.Error(result.ErrorMessage ?? "تعذّر الحفظ"); return; }
        StatusMessage = $"حُفظت فاتورة {sale!.SaleNumber} بمبلغ {sale.TotalAmount:N0} د.ع ودخل النقد الصندوق";
        Lines.Clear();
        OnPropertyChanged(nameof(Total));
        Buyer = "";
        Notes = null;
        await LoadAsync();
        await PrintAsync(d => DocumentReports.DamagedSaleAsync(Session, d, sale.Id));
    }
}
