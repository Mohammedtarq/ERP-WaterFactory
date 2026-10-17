using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;

namespace ERP.Presentation.ViewModels.Sales;

/// <summary>سطر قابل للتحرير: حد الاسم بالعبوة (فارغ = غير متابَع).</summary>
public class VariantAlertEditRow : ObservableObject
{
    private decimal? _minPacks;
    public VariantAlertEditRow(VariantAlertRow r)
    {
        Row = r;
        _minPacks = r.MinPacks;
        Original = r.MinPacks;
    }

    public VariantAlertRow Row { get; }
    public string Name => Row.Name;
    public string VariantName => Row.VariantName;
    public string PackName => Row.PackName;
    public string BalanceText => Row.BalanceText;
    public string DaysText => Row.DaysText;
    public decimal? MinPacks
    {
        get => _minPacks;
        set { if (SetProperty(ref _minPacks, value)) { OnPropertyChanged(nameof(IsLow)); OnPropertyChanged(nameof(IsTracked)); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(IsChanged)); } }
    }
    public bool IsTracked => MinPacks is not null;
    public bool IsLow => MinPacks is { } m && Row.BalancePacks <= m;
    public string StatusText => IsChanged ? "معدَّل — لم يُحفظ" : !IsTracked ? "غير متابَع" : IsLow ? "تحت الحد" : "كافٍ";
    internal decimal? Original { get; }
    public bool IsChanged => MinPacks != Original;
}

/// <summary>
/// «حدود تنبيه الأسماء الخاصة» (قسم «ح» من الاتفاقات، المرحلة الأولى): لكل اسم خاص وللأساسي حدّ يدوي بالعبوة،
/// والرصيد من مخازن المنتج التام، و«يكفي كم يومًا» للاسترشاد؛ الاسم تحت حدّه يظهر في الجرس لمدير المبيعات.
/// </summary>
public class VariantAlertsSectionViewModel : SectionViewModel
{
    private string _filter = "";
    private string _show = All;
    private const string All = "كل الأسماء", Low = "تحت الحد", Tracked = "المتابَعة فقط";

    public VariantAlertsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Sales, "حدود الأسماء الخاصة", Icons.Alert, "#F97316", "حد تنبيه يدوي لكل اسم خاص بالعبوة، والرصيد ويكفي كم يومًا")
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync);
    }

    public ObservableCollection<VariantAlertEditRow> Rows { get; } = new();
    public IReadOnlyList<string> ShowOptions { get; } = new[] { All, Low, Tracked };
    public string Show { get => _show; set { if (SetProperty(ref _show, value ?? All)) OnPropertyChanged(nameof(VisibleRows)); } }
    public string Filter { get => _filter; set { if (SetProperty(ref _filter, value ?? "")) OnPropertyChanged(nameof(VisibleRows)); } }

    /// <summary>قائمة فعلية: جدول قابل للتحرير يحتاج IList.</summary>
    public IList<VariantAlertEditRow> VisibleRows
    {
        get
        {
            IEnumerable<VariantAlertEditRow> q = Rows;
            if (Show == Low) q = q.Where(r => r.IsLow);
            else if (Show == Tracked) q = q.Where(r => r.IsTracked);
            var terms = ArabicText.Normalize(Filter).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (terms.Length > 0) q = q.Where(r => terms.All(t => ArabicText.Normalize(r.Name).Contains(t, StringComparison.OrdinalIgnoreCase)));
            return q.ToList();
        }
    }

    public int LowCount => Rows.Count(r => r.IsLow);
    /// <summary>تنبيه بسيط حتى لا يُنسى اسم جديد: «N أسماء خاصة بلا حدّ».</summary>
    public string UntrackedText
    {
        get
        {
            var n = Rows.Count(r => !r.IsTracked && r.Row.RecipeId is not null);
            return n == 0 ? "" : $"{n} اسم خاص بلا حدّ — لا يُتابَع ولا ينبّه";
        }
    }
    public AsyncRelayCommand SaveCommand { get; }
    protected override bool ReloadOnActivate => true;

    public override async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            await using var db = Session.NewDb();
            var rows = await new VariantAlertService(db).ListAsync();
            Rows.Clear();
            foreach (var r in rows) Rows.Add(new VariantAlertEditRow(r));
            RaiseLists();
        }
        finally { IsBusy = false; }
    }

    private void RaiseLists()
    {
        OnPropertyChanged(nameof(VisibleRows));
        OnPropertyChanged(nameof(LowCount));
        OnPropertyChanged(nameof(UntrackedText));
    }

    private async Task SaveAsync()
    {
        if (!CanEdit) { Dialogs.Error("لا تملك صلاحية تعديل حدود التنبيه"); return; }
        var changed = Rows.Where(r => r.IsChanged).ToList();
        if (changed.Count == 0) { StatusMessage = "لا توجد تغييرات"; return; }
        if (changed.Any(r => r.MinPacks < 0)) { Dialogs.Error("حد التنبيه لا يمكن أن يكون سالبًا"); return; }
        await using var db = Session.NewDb();
        var result = await new VariantAlertService(db).SaveAsync(changed.Select(r => (r.Row.ItemId, r.Row.RecipeId, r.MinPacks)).ToList(), Session.UserId);
        if (!result.Success) { Dialogs.Error(result.ErrorMessage ?? "تعذّر الحفظ"); return; }
        Session.MarkDataChanged();
        await LoadAsync();
        StatusMessage = $"حُفظت حدود {changed.Count} اسم";
    }
}
