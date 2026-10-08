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
            Recipes.Add(DailyProductionSectionViewModel.Basic);
            foreach (var r in _owner.RecipesOf(value.Id)) Recipes.Add(r);
            Level = Levels.FirstOrDefault();
            Recipe = DailyProductionSectionViewModel.Basic;
        }
    }
    public ItemPackagingLevel? Level { get => _level; set { if (SetProperty(ref _level, value)) Raise(); } }
    /// <summary>«الأساسي» = الليبل العام؛ اختيار ستيكر خاص يستبدله من الوصفة المخصصة، وتحمل التشغيلة اسمه.</summary>
    public CustomRecipe? Recipe { get => _recipe; set => SetProperty(ref _recipe, value); }
    public int? RecipeId => Recipe is { Id: > 0 } r ? r.Id : null;
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

    protected override void ResetInput()
    {
        Lines.Clear();
        Lines.Add(new DailyLineDraft(this));
        Needs.Clear();
        Date = DateTime.Today;
        RaiseTotals();
    }

    private DateTime _date = DateTime.Today;
    private List<ItemPackagingLevel> _levels = new();
    private List<CustomRecipe> _recipes = new();

    public DailyProductionSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Production, "إنتاج اليوم", Icons.Factory, "#14B8A6", "تسجيل إنتاج اليوم بضغطة واحدة: صرف المواد، الكلفة الفعلية، والتشغيلة")
    {
        AddLineCommand = new RelayCommand(() => Lines.Add(new DailyLineDraft(this)));
        RemoveLineCommand = new RelayCommand(p => { if (p is DailyLineDraft l) { Lines.Remove(l); RaiseTotals(); } });
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        RepeatLastCommand = new AsyncRelayCommand(RepeatLastAsync);
        ApplyTemplateCommand = new AsyncRelayCommand(ApplyTemplateAsync);
        SaveTemplateCommand = new AsyncRelayCommand(SaveTemplateAsync);
        DeleteTemplateCommand = new AsyncRelayCommand(DeleteTemplateAsync);
        PreviewCommand = new AsyncRelayCommand(PreviewAsync);
        Lines.Add(new DailyLineDraft(this));
    }

    /// <summary>خيار «الأساسي» في قائمة الاسم الخاص (يمكن الرجوع إليه بعد اختيار اسم).</summary>
    public static readonly CustomRecipe Basic = new() { Id = 0, Name = "الأساسي (الليبل العام)" };

    public DateTime Date { get => _date; set => SetProperty(ref _date, value); }
    public ObservableCollection<Item> Products { get; } = new();
    public ObservableCollection<DailyLineDraft> Lines { get; } = new();
    public ObservableCollection<DailyProductionLineResult> LastResult { get; } = new();
    public ObservableCollection<MaterialNeedRow> Needs { get; } = new();
    public ObservableCollection<DailyProductionTemplate> Templates { get; } = new();
    public decimal TotalPieces => Lines.Sum(l => l.Pieces);
    public bool CanSeeCost => Has(SpecialPermission.CostAndProfit);
    public bool HasShortage => Needs.Any(n => n.Short > 0);

    private DailyProductionTemplate? _selectedTemplate;
    private string _templateName = "";
    public DailyProductionTemplate? SelectedTemplate
    {
        get => _selectedTemplate;
        set { if (SetProperty(ref _selectedTemplate, value) && value is not null) TemplateName = value.Name; }
    }
    public string TemplateName { get => _templateName; set => SetProperty(ref _templateName, value); }

    public RelayCommand AddLineCommand { get; }
    public RelayCommand RemoveLineCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand RepeatLastCommand { get; }
    public AsyncRelayCommand ApplyTemplateCommand { get; }
    public AsyncRelayCommand SaveTemplateCommand { get; }
    public AsyncRelayCommand DeleteTemplateCommand { get; }
    public AsyncRelayCommand PreviewCommand { get; }

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
        await LoadTemplatesAsync(db);
    }

    private async Task LoadTemplatesAsync(ERP.Data.ProjectDb.ProjectDbContext db)
    {
        var keep = SelectedTemplate?.Id;
        Templates.Clear();
        foreach (var t in await new DailyProductionTemplateService(db).ListAsync()) Templates.Add(t);
        _selectedTemplate = Templates.FirstOrDefault(t => t.Id == keep);
        OnPropertyChanged(nameof(SelectedTemplate));
    }

    private List<DailyLineDraft> FilledLines() => Lines.Where(l => l.Product is not null && l.Level is not null && l.Packs > 0).ToList();

    /// <summary>يملأ السطور من قالب أو من آخر إنتاج؛ ما لم يعد فعّالًا (منتج أو وحدة أو اسم) يُتخطّى ويُذكر.</summary>
    private void Fill(IReadOnlyList<DailyTemplateLine> source, string from)
    {
        Lines.Clear();
        var skipped = 0;
        foreach (var t in source)
        {
            var draft = new DailyLineDraft(this) { Product = Products.FirstOrDefault(p => p.Id == t.FinishedItemId) };
            var level = draft.Levels.FirstOrDefault(l => l.Id == t.PackagingLevelId);
            var recipe = t.CustomRecipeId is int rid ? draft.Recipes.FirstOrDefault(r => r.Id == rid) : Basic;
            if (draft.Product is null || level is null || recipe is null) { skipped++; continue; }
            draft.Level = level;
            draft.Recipe = recipe;
            draft.Packs = t.Packs;
            Lines.Add(draft);
        }
        if (Lines.Count == 0) Lines.Add(new DailyLineDraft(this));
        Needs.Clear();
        OnPropertyChanged(nameof(HasShortage));
        RaiseTotals();
        StatusMessage = $"{from}: {Lines.Count(l => l.Product is not null)} سطر — عدّل الأعداد ثم سجّل" + (skipped > 0 ? $" ({skipped} سطر لم يعد متاحًا)" : "");
    }

    private async Task RepeatLastAsync()
    {
        if (HasPendingInput && !Dialogs.Confirm("استبدال السطور الحالية بسطور آخر إنتاج؟")) return;
        await using var db = Session.NewDb();
        var (date, lines) = await new DailyProductionTemplateService(db).LastProductionAsync();
        if (lines.Count == 0) { Dialogs.Info("لا يوجد إنتاج يوم مسجّل بعد"); return; }
        Fill(lines, $"آخر إنتاج ({date:yyyy/MM/dd})");
    }

    private async Task ApplyTemplateAsync()
    {
        if (SelectedTemplate is null) { Dialogs.Error("اختر القالب"); return; }
        if (HasPendingInput && !Dialogs.Confirm($"استبدال السطور الحالية بقالب «{SelectedTemplate.Name}»؟")) return;
        await using var db = Session.NewDb();
        Fill(await new DailyProductionTemplateService(db).LinesAsync(SelectedTemplate.Id), $"قالب «{SelectedTemplate.Name}»");
    }

    private async Task SaveTemplateAsync()
    {
        if (!Require(CanAdd, "حفظ القالب")) return;
        var lines = FilledLines();
        if (Templates.Any(t => t.Name == TemplateName.Trim()) && !Dialogs.Confirm($"القالب «{TemplateName.Trim()}» موجود. استبدال سطوره بالسطور الحالية؟")) return;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(async () => (await new DailyProductionTemplateService(db).SaveAsync(TemplateName,
                lines.Select(l => new DailyTemplateLine(l.Product!.Id, l.Level!.Id, l.RecipeId, l.Packs)).ToList(), Session.UserId)).result,
                $"حُفظ القالب «{TemplateName.Trim()}»"))
        {
            await LoadTemplatesAsync(db);
            SelectedTemplate = Templates.FirstOrDefault(t => t.Name == TemplateName.Trim());
        }
    }

    private async Task DeleteTemplateAsync()
    {
        if (SelectedTemplate is null) { Dialogs.Error("اختر القالب"); return; }
        if (!Require(CanAdd, "حذف القالب") || !Dialogs.Confirm($"حذف القالب «{SelectedTemplate.Name}»؟ لا يمس أي إنتاج مسجّل.")) return;
        var name = SelectedTemplate.Name;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new DailyProductionTemplateService(db).DeleteAsync(SelectedTemplate.Id), $"حُذف القالب «{name}»"))
        {
            SelectedTemplate = null;
            TemplateName = "";
            await LoadTemplatesAsync(db);
        }
    }

    private async Task PreviewAsync()
    {
        await using var db = Session.NewDb();
        var (error, needs) = await new DailyProductionService(db).PreviewAsync(
            FilledLines().Select(l => new DailyProductionLineInput(l.Product!.Id, l.Level!.Id, l.Packs, l.RecipeId)).ToList());
        Needs.Clear();
        foreach (var n in needs) Needs.Add(n);
        OnPropertyChanged(nameof(HasShortage));
        StatusMessage = error ?? (HasShortage ? $"المواد لا تكفي: {string.Join("، ", needs.Where(n => n.Short > 0).Select(n => $"{n.ItemName} (ينقص {n.Short:N0})"))}"
                                              : $"المواد تكفي إنتاج اليوم ({needs.Count} مادة)");
    }

    private async Task SaveAsync()
    {
        if (!Require(CanAdd, "تسجيل الإنتاج")) return;
        var lines = FilledLines();
        if (lines.Count == 0) { Dialogs.Error("أضف منتجًا واحدًا على الأقل بعدد عبوات"); return; }
        if (!Dialogs.Confirm($"تسجيل إنتاج يوم {Date:yyyy/MM/dd}:\n{string.Join("\n", lines.Select(l => $"• {l.Product!.ItemName}{(l.RecipeId is null ? "" : $" — {l.Recipe!.Name}")}: {l.Packs:N0} {l.Level!.LevelName}"))}\nتُصرف المواد من الوصفة فورًا."))
            return;
        await using var db = Session.NewDb();
        List<DailyProductionLineResult> result = new();
        if (await RunOperationAsync(async () =>
            {
                var (r, _, rows) = await new DailyProductionService(db).RecordAsync(Date,
                    lines.Select(l => new DailyProductionLineInput(l.Product!.Id, l.Level!.Id, l.Packs, l.RecipeId, l.Color)).ToList(), Session.UserId);
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
            Needs.Clear();
            OnPropertyChanged(nameof(HasShortage));
            RaiseTotals();
        }
    }
}

// ============================ متغيرات المنتج ============================
/// <summary>
/// المنتج التام حسب المتغير: ما أُنتج وبيع في الفترة ورصيده الآن وكلفة قطعته، وتشغيلاته بأرصدتها،
/// وتصحيح متغير تشغيلة سُجّلت خطأً (بالصلاحية الخاصة).
/// </summary>
public class VariantStockSectionViewModel : SectionViewModel
{
    private DateTime _from = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _to = DateTime.Today;
    private VariantBatchRow? _selectedBatch;
    private CustomRecipe? _newVariant;
    private string _reason = "";
    private List<CustomRecipe> _recipes = new();

    public VariantStockSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Production, "متغيرات المنتج", Icons.Layers, "#8B5CF6", "الأساسي والمطاعم والمناسبات: الإنتاج والمبيع والرصيد والكلفة لكل متغير")
    {
        CorrectCommand = new AsyncRelayCommand(CorrectAsync);
        PrintCommand = new RelayCommand(Print);
    }

    protected override bool ReloadOnActivate => true;
    public DateTime From { get => _from; set => SetProperty(ref _from, value); }
    public DateTime To { get => _to; set => SetProperty(ref _to, value); }
    public ObservableCollection<VariantSummaryRow> Rows { get; } = new();
    public ObservableCollection<VariantBatchRow> Batches { get; } = new();
    public ObservableCollection<CustomRecipe> VariantOptions { get; } = new();
    public bool CanSeeCost => Has(SpecialPermission.CostAndProfit);
    public bool CanCorrect => Has(SpecialPermission.ReservedStock);
    public VariantBatchRow? SelectedBatch
    {
        get => _selectedBatch;
        set
        {
            if (!SetProperty(ref _selectedBatch, value)) return;
            VariantOptions.Clear();
            if (value is null) return;
            VariantOptions.Add(DailyProductionSectionViewModel.Basic);
            foreach (var r in _recipes.Where(r => r.FinishedItemId == value.ItemId).OrderBy(r => r.Name)) VariantOptions.Add(r);
            NewVariant = VariantOptions.FirstOrDefault(r => r.Id == (value.RecipeId ?? 0));
        }
    }
    public CustomRecipe? NewVariant { get => _newVariant; set => SetProperty(ref _newVariant, value); }
    public string Reason { get => _reason; set => SetProperty(ref _reason, value); }
    public AsyncRelayCommand CorrectCommand { get; }
    public RelayCommand PrintCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var svc = new VariantStockService(db);
        var rows = await svc.SummaryAsync(From, To);
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        var keep = SelectedBatch?.BatchId;
        _recipes = await db.CustomRecipes.AsNoTracking().ToListAsync();
        Batches.Clear();
        foreach (var b in await svc.BatchesAsync()) Batches.Add(b);
        SelectedBatch = Batches.FirstOrDefault(b => b.BatchId == keep);
        StatusMessage = $"{Rows.Count(r => r.RecipeId != null)} متغير خاص برصيد أو حركة، و{Batches.Count} تشغيلة بها رصيد";
    }

    private async Task CorrectAsync()
    {
        if (SelectedBatch is null || NewVariant is null) { Dialogs.Error("اختر التشغيلة من الجدول ثم المتغير الصحيح"); return; }
        var target = NewVariant.Id > 0 ? NewVariant.Name : "أساسي";
        if (!Dialogs.Confirm($"تصحيح متغير التشغيلة {SelectedBatch.BatchNumber} ({SelectedBatch.Balance:N0} قطعة): {SelectedBatch.Variant} ← {target}؟")) return;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new VariantStockService(db).SetBatchVariantAsync(SelectedBatch.BatchId, NewVariant.Id > 0 ? NewVariant.Id : null, Reason, Session.UserId),
                                    $"التشغيلة {SelectedBatch.BatchNumber} أصبحت: {target}"))
        {
            Reason = "";
            await LoadAsync();
        }
    }

    private void Print()
    {
        var r = new ReportDocument { CompanyName = Session.ProjectName, Title = $"متغيرات المنتج {From:yyyy/MM/dd} — {To:yyyy/MM/dd}", PrintedBy = Session.FullName };
        r.Columns.AddRange(CanSeeCost ? new[] { "المنتج", "المتغير", "النوع", "أُنتج", "المبيع", "الرصيد الآن", "كلفة القطعة" }
                                      : new[] { "المنتج", "المتغير", "النوع", "أُنتج", "المبيع", "الرصيد الآن" });
        foreach (var x in Rows)
        {
            var cells = new List<string> { x.ItemName, x.Variant, x.Kind, x.ProducedText, x.SoldText, x.BalanceText };
            if (CanSeeCost) cells.Add($"{x.UnitCost:N2}");
            r.Rows.Add(cells);
        }
        r.Total("مجموع الرصيد", $"{Rows.Sum(x => x.Balance):N0} قطعة", true);
        Dialogs.ShowReport(r);
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
