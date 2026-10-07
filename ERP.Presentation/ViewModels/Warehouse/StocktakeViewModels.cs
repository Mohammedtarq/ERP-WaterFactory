using System.Collections.ObjectModel;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Warehouse;

// ============================ الجرد السريع ============================
/// <summary>خانة عدّ بوحدة واحدة (باليت، كرتون، قطعة...).</summary>
public class CountCell : ObservableObject
{
    private readonly Action _changed;
    private decimal _count;
    public CountCell(string unit, decimal pieces, Action changed) { Unit = unit; PiecesPerUnit = pieces; _changed = changed; }
    public string Unit { get; }
    public decimal PiecesPerUnit { get; }
    public string Label => PiecesPerUnit == 1 ? Unit : $"{Unit} ({PiecesPerUnit:#,0.###})";
    public decimal Count { get => _count; set { if (SetProperty(ref _count, value)) _changed(); } }
}

/// <summary>سطر جرد: رصيد النظام، والعدّ بالوحدات الكبيرة، والفرق بالقطع وقيمته.</summary>
public class CountLine : ObservableObject
{
    private bool _counted;
    public CountLine(StocktakeSheetRow row)
    {
        Row = row;
        foreach (var (name, pieces) in row.Units) Cells.Add(new CountCell(name, pieces, Changed));
    }
    public StocktakeSheetRow Row { get; }
    public ObservableCollection<CountCell> Cells { get; } = new();
    /// <summary>عُدّ هذا الصنف (يُرسل مع الجرد حتى لو كان العدد صفرًا).</summary>
    public bool Counted { get => _counted; set { if (SetProperty(ref _counted, value)) Raise(); } }
    public decimal CountedPieces => Cells.Sum(c => c.Count * c.PiecesPerUnit);
    public decimal Variance => Counted ? CountedPieces - Row.SystemQuantity : 0;
    public decimal VarianceValue => Math.Round(Variance * (Row.UnitCost ?? 0), 2);
    /// <summary>رصيد النظام والمعدود والفرق بوحدات الصنف ("50 شرنك") بدل القطع.</summary>
    public string SystemText => InUnits(Row.SystemQuantity);
    public string CountedText => InUnits(CountedPieces);
    public string VarianceText => Variance == 0 ? "0" : (Variance > 0 ? "+" : "−") + InUnits(Math.Abs(Variance));

    private string InUnits(decimal pieces)
    {
        if (pieces == 0) return "0";
        var parts = new List<string>();
        var rest = pieces;
        foreach (var c in Cells.Where(c => c.PiecesPerUnit > 0).OrderByDescending(c => c.PiecesPerUnit))
        {
            var n = Math.Floor(rest / c.PiecesPerUnit);
            if (n <= 0) continue;
            parts.Add($"{n:#,0} {c.Unit}");
            rest -= n * c.PiecesPerUnit;
        }
        if (rest > 0) parts.Add($"{rest:#,0.###}");
        return string.Join(" + ", parts);
    }

    private void Changed()
    {
        _counted = true;
        OnPropertyChanged(nameof(Counted));
        Raise();
    }
    private void Raise()
    {
        OnPropertyChanged(nameof(CountedPieces));
        OnPropertyChanged(nameof(Variance));
        OnPropertyChanged(nameof(VarianceValue));
        OnPropertyChanged(nameof(CountedText));
        OnPropertyChanged(nameof(VarianceText));
    }
}

/// <summary>
/// الجرد الأسبوعي السريع: يُعدّ ما يُرى (باليت + كرتون + قطع، الشرنك بالرولات) والنظام يحوّل إلى قطع.
/// الفرق يُسجَّل "فرق جرد" بالكلفة منفصلًا عن التلف. ورقة الجرد تُطبع للعدّ على الورق ثم تُدخل.
/// </summary>
public class StocktakeSectionViewModel : SectionViewModel
{
    protected override bool HasPendingInput => Lines.Any(l => l.Counted);

    private Data.ProjectDb.Entities.Warehouse? _warehouse;
    private DateTime _countDate = DateTime.Today;
    private string? _notes;
    private string _filter = "";

    public StocktakeSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Warehouse, "الجرد", Icons.Adjust, "#0F766E", "عدّ فعلي بالباليت والكرتون والقطع، وفرق الجرد بالكلفة منفصلًا عن التلف")
    {
        LoadSheetCommand = new AsyncRelayCommand(LoadSheetAsync);
        PostCommand = new AsyncRelayCommand(PostAsync);
        PrintSheetCommand = new RelayCommand(PrintSheet);
    }

    public ObservableCollection<Data.ProjectDb.Entities.Warehouse> Warehouses { get; } = new();
    public ObservableCollection<CountLine> Lines { get; } = new();
    public ObservableCollection<CountHistoryRow> History { get; } = new();
    public Data.ProjectDb.Entities.Warehouse? Warehouse { get => _warehouse; set { if (SetProperty(ref _warehouse, value)) Background(LoadSheetAsync()); } }
    public DateTime CountDate { get => _countDate; set => SetProperty(ref _countDate, value); }
    public string? Notes { get => _notes; set => SetProperty(ref _notes, value); }
    public string Filter { get => _filter; set { if (SetProperty(ref _filter, value ?? "")) OnPropertyChanged(nameof(VisibleLines)); } }
    public IEnumerable<CountLine> VisibleLines => Filter.Length == 0 ? Lines
        : Lines.Where(l => l.Row.ItemName.Contains(Filter, StringComparison.OrdinalIgnoreCase) || l.Row.ItemCode.Contains(Filter, StringComparison.OrdinalIgnoreCase));
    public bool CanSeeCost => Has(SpecialPermission.CostAndProfit);

    public AsyncRelayCommand LoadSheetCommand { get; }
    public AsyncRelayCommand PostCommand { get; }
    public RelayCommand PrintSheetCommand { get; }

    public record CountHistoryRow(string CountNumber, DateTime CountDate, string Warehouse, int Items, decimal Shortage, decimal Surplus, string User);

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var keep = Warehouse?.Id;
        Warehouses.Clear();
        foreach (var w in await db.Warehouses.AsNoTracking().Where(w => w.IsActive && w.WarehouseType != WarehouseType.WorkInProcess)
                     .OrderBy(w => w.WarehouseType == WarehouseType.RawMaterial ? 0 : w.WarehouseType == WarehouseType.FinishedGoods ? 1 : 2).ThenBy(w => w.Name).ToListAsync())
            Warehouses.Add(w);
        _warehouse = Warehouses.FirstOrDefault(w => w.Id == keep) ?? Warehouses.FirstOrDefault();
        OnPropertyChanged(nameof(Warehouse));
        await LoadSheetAsync();
        await LoadHistoryAsync(db);
    }

    private async Task LoadHistoryAsync(ProjectDbContext db)
    {
        History.Clear();
        foreach (var c in await db.StockCounts.AsNoTracking().OrderByDescending(c => c.Id).Take(50)
                     .Select(c => new CountHistoryRow(c.CountNumber, c.CountDate, c.Warehouse.Name, c.Lines.Count, c.ShortageValue, c.SurplusValue, c.CreatedByUser.Username))
                     .ToListAsync())
            History.Add(c);
    }

    private async Task LoadSheetAsync()
    {
        Lines.Clear();
        if (Warehouse is null) { OnPropertyChanged(nameof(VisibleLines)); return; }
        await using var db = Session.NewDb();
        foreach (var r in await new StocktakeService(db).SheetAsync(Warehouse.Id)) Lines.Add(new CountLine(r));
        OnPropertyChanged(nameof(VisibleLines));
    }

    private async Task PostAsync()
    {
        if (!Require(CanEdit, "تسجيل الجرد")) return;
        if (Warehouse is null) { Dialogs.Error("اختر المخزن"); return; }
        var counted = Lines.Where(l => l.Counted).ToList();
        if (counted.Count == 0) { Dialogs.Error("أدخل عدد صنف واحد على الأقل"); return; }
        var withVariance = counted.Count(l => l.Variance != 0);
        if (!Dialogs.Confirm($"تسجيل جرد {Warehouse.Name}: {counted.Count} صنف، منها {withVariance} بفرق. يُعدَّل الرصيد إلى المعدود فعلًا، ويُسجَّل الفرق «فرق جرد». متابعة؟"))
            return;
        await using var db = Session.NewDb();
        StocktakeResult? summary = null;
        if (await RunOperationAsync(async () =>
            {
                var (r, s) = await new StocktakeService(db).PostAsync(Warehouse.Id, CountDate, Notes,
                    counted.Select(l => new StocktakeLineInput(l.Row.ItemId, l.Cells.Select(c => (c.Unit, c.PiecesPerUnit, c.Count)).ToList())).ToList(), Session.UserId);
                summary = s;
                return r;
            }, "سُجّل الجرد"))
        {
            if (summary is not null)
                StatusMessage = $"سُجّل الجرد {summary.CountNumber}: {summary.ItemsWithVariance} صنف بفرق" +
                                (CanSeeCost ? $" — نقص {summary.ShortageValue:N0} د.ع، زيادة {summary.SurplusValue:N0} د.ع" : "");
            Notes = null;
            await LoadSheetAsync();
            await LoadHistoryAsync(db);
        }
    }

    /// <summary>ورقة الجرد للعدّ على الورق: الصنف ووحداته بلا رصيد النظام (العدّ أعمى).</summary>
    private void PrintSheet()
    {
        if (Warehouse is null) return;
        var r = new ReportDocument { CompanyName = Session.ProjectName, Title = $"ورقة جرد — {Warehouse.Name}", PrintedBy = Session.FullName,
                                     Notes = "اكتب العدد بكل وحدة كما تراه (باليت كامل، كراتين، قطع مفردة). لا تُطبع أرصدة النظام حتى يكون العدّ مستقلًا." };
        r.Columns.AddRange(new[] { "الرمز", "الصنف", "الوحدات", "العدد", "ملاحظة" });
        foreach (var l in Lines)
            r.Rows.Add(new[] { l.Row.ItemCode, l.Row.ItemName, string.Join(" / ", l.Cells.Select(c => c.Label)), "", "" });
        r.Signatures.AddRange(new[] { "القائم بالجرد", "أمين المخزن", "المدير" });
        Dialogs.ShowReport(r);
    }
}

// ============================ الخسائر بالكلفة ============================
public class LossesSectionViewModel : SectionViewModel
{
    private int _year = DateTime.Today.Year;
    private int _month = DateTime.Today.Month;

    public LossesSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Warehouse, "التلف والمسحوب وفرق الجرد", Icons.Alert, "#B45309", "خسائر الشهر بالكلفة: التلف، والمسحوب المجاني لكل جهة، وفرق الجرد — كل نوع منفصل")
    {
        PrintCommand = new RelayCommand(Print);
    }

    protected override bool ReloadOnActivate => true;
    public IReadOnlyList<int> Years { get; } = Enumerable.Range(DateTime.Today.Year - 3, 4).Reverse().ToList();
    public IReadOnlyList<int> Months { get; } = Enumerable.Range(1, 12).ToList();
    public int Year { get => _year; set { if (SetProperty(ref _year, value)) Background(LoadAsync()); } }
    public int Month { get => _month; set { if (SetProperty(ref _month, value)) Background(LoadAsync()); } }
    public ObservableCollection<LossView> Rows { get; } = new();
    public ObservableCollection<LossTotal> Totals { get; } = new();
    public bool CanSeeCost => Has(SpecialPermission.CostAndProfit);
    public RelayCommand PrintCommand { get; }

    public record LossView(string Kind, string ItemName, string Party, string Category, decimal Pieces, decimal Value);
    public record LossTotal(string Kind, decimal Pieces, decimal Value);

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var rows = await new ProductionStockReports(db).MonthlyLossesAsync(Year, Month);
        Rows.Clear();
        foreach (var r in rows)
            Rows.Add(new LossView(r.Kind, r.ItemName, r.Party ?? "", r.Category is null ? "" : ArabicLabels.Of(Enum.Parse<BeneficiaryCategory>(r.Category)), r.Pieces, r.Value));
        Totals.Clear();
        foreach (var g in rows.GroupBy(r => r.Kind)) Totals.Add(new LossTotal(g.Key, g.Sum(r => r.Pieces), g.Sum(r => r.Value)));
    }

    private void Print()
    {
        var r = new ReportDocument { CompanyName = Session.ProjectName, Title = $"التلف والمسحوب المجاني وفرق الجرد — {Month}/{Year}", PrintedBy = Session.FullName };
        r.Columns.AddRange(CanSeeCost ? new[] { "النوع", "الصنف", "الجهة", "التصنيف", "القطع", "القيمة بالكلفة" } : new[] { "النوع", "الصنف", "الجهة", "التصنيف", "القطع" });
        foreach (var x in Rows)
        {
            var cells = new List<string> { x.Kind, x.ItemName, x.Party, x.Category, $"{x.Pieces:N0}" };
            if (CanSeeCost) cells.Add($"{x.Value:N0}");
            r.Rows.Add(cells);
        }
        foreach (var t in Totals) r.Total(t.Kind, CanSeeCost ? $"{t.Pieces:N0} قطعة — {t.Value:N0} د.ع" : $"{t.Pieces:N0} قطعة");
        Dialogs.ShowReport(r);
    }
}

// ============================ جهات المسحوب المجاني ============================
public class BeneficiariesSectionViewModel : CrudSectionViewModel<FreeIssueBeneficiary>
{
    public BeneficiariesSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Warehouse, "جهات المسحوب المجاني", Icons.People, "#7C3AED", "قائمة ثابتة بالجهات وتصنيفها (حكومية، سائقون، شركاء...)") { }

    public IReadOnlyList<Option<BeneficiaryCategory>> CategoryOptions { get; } = ArabicLabels.OptionsOf<BeneficiaryCategory>();
    protected override int GetId(FreeIssueBeneficiary e) => e.Id;
    protected override string Describe(FreeIssueBeneficiary e) => e.Name;
    protected override Task<List<FreeIssueBeneficiary>> QueryAsync(ProjectDbContext db) =>
        db.FreeIssueBeneficiaries.AsNoTracking().OrderBy(b => b.Category).ThenBy(b => b.Name).ToListAsync();
    protected override string? Validate(FreeIssueBeneficiary e) => string.IsNullOrWhiteSpace(e.Name) ? "أدخل اسم الجهة" : null;
    protected override Task BeforeSaveAsync(ProjectDbContext db, FreeIssueBeneficiary e)
    {
        e.Name = e.Name.Trim();
        return Task.CompletedTask;
    }
}
