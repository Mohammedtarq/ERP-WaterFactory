using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Production;

// ============================ إنتاج اليوم ============================
/// <summary>سطر إنتاج: المنتج، الاسم الخاص (مفلتر حسب المنتج)، وحدة التعبئة، العدد، اللون.</summary>
public class DailyLineDraft : ObservableObject
{
    private readonly DailyProductionSectionViewModel _owner;
    private Item? _product;
    private ItemPackagingLevel? _level;
    private CustomRecipe? _recipe;
    private decimal _packs;
    private string? _color;

    public DailyLineDraft(DailyProductionSectionViewModel owner) => _owner = owner;

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
            if (value is null) { Level = null; Recipe = null; return; }
            foreach (var l in _owner.LevelsOf(value.Id)) Levels.Add(l);
            foreach (var r in _owner.RecipesOf(value.Id)) Recipes.Add(r);
            Level = Levels.FirstOrDefault();
            Recipe = null;
        }
    }
    public ItemPackagingLevel? Level { get => _level; set { if (SetProperty(ref _level, value)) Raise(); } }
    /// <summary>فارغ = الليبل العام؛ اختيار ستيكر خاص يستبدله من الوصفة المخصصة.</summary>
    public CustomRecipe? Recipe { get => _recipe; set => SetProperty(ref _recipe, value); }
    public decimal Packs { get => _packs; set { if (SetProperty(ref _packs, value)) Raise(); } }
    public string? Color { get => _color; set => SetProperty(ref _color, value); }
    public decimal Pieces => Level is null ? 0 : Packs * Level.EquivalentBaseUnits;

    private void Raise()
    {
        OnPropertyChanged(nameof(Pieces));
        _owner.RaiseTotals();
    }
}

/// <summary>
/// "إنتاج اليوم": شاشة واحدة بدل أمر إنتاج ← تحت التصنيع ← تعبئة. صرف المواد من الوصفة وكلفة فعلية
/// وتشغيلة باسم اليوم ودخول المنتج التام، وتسوية أي بيع سبق الإنتاج — بضغطة واحدة.
/// </summary>
public class DailyProductionSectionViewModel : SectionViewModel
{
    protected override bool HasPendingInput => Lines.Any(l => l.Product is not null);

    private DateTime _date = DateTime.Today;
    private List<ItemPackagingLevel> _levels = new();
    private List<CustomRecipe> _recipes = new();

    public DailyProductionSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Production, "إنتاج اليوم", Icons.Factory, "#14B8A6", "تسجيل إنتاج اليوم بضغطة واحدة: صرف المواد، الكلفة الفعلية، والتشغيلة")
    {
        AddLineCommand = new RelayCommand(() => Lines.Add(new DailyLineDraft(this)));
        RemoveLineCommand = new RelayCommand(p => { if (p is DailyLineDraft l) { Lines.Remove(l); RaiseTotals(); } });
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        Lines.Add(new DailyLineDraft(this));
    }

    public DateTime Date { get => _date; set => SetProperty(ref _date, value); }
    public ObservableCollection<Item> Products { get; } = new();
    public ObservableCollection<DailyLineDraft> Lines { get; } = new();
    public ObservableCollection<DailyProductionLineResult> LastResult { get; } = new();
    public decimal TotalPieces => Lines.Sum(l => l.Pieces);
    public bool CanSeeCost => Has(SpecialPermission.CostAndProfit);

    public RelayCommand AddLineCommand { get; }
    public RelayCommand RemoveLineCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }

    internal IEnumerable<ItemPackagingLevel> LevelsOf(int itemId) =>
        _levels.Where(l => l.ItemId == itemId).OrderByDescending(l => l.EquivalentBaseUnits);
    internal IEnumerable<CustomRecipe> RecipesOf(int itemId) => _recipes.Where(r => r.FinishedItemId == itemId).OrderBy(r => r.Name);
    internal void RaiseTotals() => OnPropertyChanged(nameof(TotalPieces));

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var withBom = await db.BillOfMaterials.Where(b => b.IsActive).Select(b => b.FinishedItemId).Distinct().ToListAsync();
        Products.Clear();
        foreach (var i in await db.Items.AsNoTracking().Where(i => i.IsActive && withBom.Contains(i.Id)).OrderBy(i => i.ItemName).ToListAsync())
            Products.Add(i);
        _levels = await db.ItemPackagingLevels.AsNoTracking().Where(l => withBom.Contains(l.ItemId) && l.IsSellableUnit).ToListAsync();
        _recipes = await db.CustomRecipes.AsNoTracking().Where(r => r.IsActive && withBom.Contains(r.FinishedItemId)).ToListAsync();
    }

    private async Task SaveAsync()
    {
        if (!Require(CanAdd, "تسجيل الإنتاج")) return;
        var lines = Lines.Where(l => l.Product is not null && l.Level is not null && l.Packs > 0).ToList();
        if (lines.Count == 0) { Dialogs.Error("أضف منتجًا واحدًا على الأقل بعدد عبوات"); return; }
        if (!Dialogs.Confirm($"تسجيل إنتاج يوم {Date:yyyy/MM/dd}: {string.Join("، ", lines.Select(l => $"{l.Product!.ItemName} {l.Packs:N0} {l.Level!.LevelName}"))}؟ تُصرف المواد من الوصفة فورًا."))
            return;
        await using var db = Session.NewDb();
        List<DailyProductionLineResult> result = new();
        if (await RunOperationAsync(async () =>
            {
                var (r, _, rows) = await new DailyProductionService(db).RecordAsync(Date,
                    lines.Select(l => new DailyProductionLineInput(l.Product!.Id, l.Level!.Id, l.Packs, l.Recipe?.Id, l.Color)).ToList(), Session.UserId);
                result = rows;
                return r;
            }, $"سُجّل إنتاج يوم {Date:yyyy/MM/dd}"))
        {
            LastResult.Clear();
            foreach (var r in result) LastResult.Add(r);
            var settled = result.Sum(r => r.SettledShortage);
            if (settled > 0) StatusMessage += $" — وسُوّي {settled:N0} قطعة بيعت بانتظار الإنتاج";
            Lines.Clear();
            Lines.Add(new DailyLineDraft(this));
            RaiseTotals();
        }
    }
}

// ============================ جرد الإنتاج الشهري ============================
public class ProductionMonthSectionViewModel : SectionViewModel
{
    private int _year = DateTime.Today.Year;
    private int _month = DateTime.Today.Month;

    public ProductionMonthSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Production, "جرد الإنتاج الشهري", Icons.Calendar, "#0EA5E9", "يومًا بيوم: العبوات والقطع لكل منتج، وكلفة الإنتاج")
    {
        PrintCommand = new RelayCommand(Print);
    }

    protected override bool ReloadOnActivate => true;
    public IReadOnlyList<int> Years { get; } = Enumerable.Range(DateTime.Today.Year - 3, 4).Reverse().ToList();
    public IReadOnlyList<int> Months { get; } = Enumerable.Range(1, 12).ToList();
    public int Year { get => _year; set { if (SetProperty(ref _year, value)) Background(LoadAsync()); } }
    public int Month { get => _month; set { if (SetProperty(ref _month, value)) Background(LoadAsync()); } }
    public ObservableCollection<ProductionStockReports.ProductionDayRow> Rows { get; } = new();
    public decimal TotalPieces => Rows.Sum(r => r.Pieces);
    public decimal TotalCost => Rows.Sum(r => r.Cost);
    public bool CanSeeCost => Has(SpecialPermission.CostAndProfit);
    public RelayCommand PrintCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var rows = await new ProductionStockReports(db).MonthlyProductionAsync(Year, Month);
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        OnPropertyChanged(nameof(TotalPieces));
        OnPropertyChanged(nameof(TotalCost));
    }

    private void Print()
    {
        var r = new ReportDocument { CompanyName = Session.ProjectName, Title = $"جرد الإنتاج — {Month}/{Year}", PrintedBy = Session.FullName };
        r.Columns.AddRange(CanSeeCost ? new[] { "التاريخ", "المنتج", "العبوات", "الوحدة", "القطع", "الكلفة" } : new[] { "التاريخ", "المنتج", "العبوات", "الوحدة", "القطع" });
        foreach (var x in Rows)
        {
            var cells = new List<string> { x.Date.ToString("yyyy/MM/dd"), x.ItemName, $"{x.Packs:N0}", x.PackLabel, $"{x.Pieces:N0}" };
            if (CanSeeCost) cells.Add($"{x.Cost:N0}");
            r.Rows.Add(cells);
        }
        r.Total("مجموع القطع", $"{TotalPieces:N0}", true);
        if (CanSeeCost) r.Total("كلفة الإنتاج", $"{TotalCost:N0} د.ع");
        Dialogs.ShowReport(r);
    }
}

// ============================ بانتظار الإنتاج ============================
public class PendingProductionSectionViewModel : SectionViewModel
{
    public PendingProductionSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Production, "بانتظار الإنتاج", Icons.Alert, "#DC2626", "ما بيع قبل تسجيل إنتاجه: يُسوّى تلقائيًا عند تسجيل الإنتاج") { }

    protected override bool ReloadOnActivate => true;
    public ObservableCollection<PendingProductionService.OpenShortageRow> Rows { get; } = new();
    public decimal TotalOpen => Rows.Sum(r => r.Open);

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        Rows.Clear();
        foreach (var r in await new PendingProductionService(db).OpenAsync()) Rows.Add(r);
        OnPropertyChanged(nameof(TotalOpen));
        StatusMessage = Rows.Count == 0 ? "لا يوجد بيع بانتظار الإنتاج" : $"{TotalOpen:N0} قطعة بيعت ولم يُسجَّل إنتاجها بعد";
    }
}
