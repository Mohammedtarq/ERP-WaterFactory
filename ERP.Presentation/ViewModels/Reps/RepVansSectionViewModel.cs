using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using ERP.Presentation.ViewModels.Warehouse;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Reps;

public enum RepVanFilter { All, NeedsSettlement, HasStock }

/// <summary>
/// «سيارات المندوبين»: شاشة واحدة ببطاقة لكل مندوب بدل تبويب لكل سيارة. الضغط على بطاقة يعرض شاشة السيارة نفسها
/// (الأرصدة بالتشغيلة، الحركات، المستندات، الطباعة) تحت البطاقات. تظهر في المندوبين وفي المخازن لأمين المخزن.
/// </summary>
public class RepVansSectionViewModel : SectionViewModel
{
    private List<RepVanCard> _all = new();
    private string _search = "";
    private Option<RepVanFilter> _filter;
    private RepVanCard? _selected;
    private WarehouseWorkspaceSectionViewModel? _detail;

    public RepVansSectionViewModel(AppSession s, IDialogService d, string moduleCode = ModuleCode.Reps)
        : base(s, d, moduleCode, "سيارات المندوبين", Icons.Truck, "#0EA5E9", "كل السيارات في شاشة واحدة: الرصيد، حمولة اليوم، حالة التسوية، والنقد المتوقع")
    {
        _filter = Filters[0];
        PrintCommand = new RelayCommand(Print);
    }

    protected override bool ReloadOnActivate => true;

    public IReadOnlyList<Option<RepVanFilter>> Filters { get; } = new[]
    {
        new Option<RepVanFilter>(RepVanFilter.All, "كل السيارات"),
        new Option<RepVanFilter>(RepVanFilter.NeedsSettlement, "تحتاج تسوية"),
        new Option<RepVanFilter>(RepVanFilter.HasStock, "فيها رصيد"),
    };
    public ObservableCollection<RepVanCard> Cards { get; } = new();
    public string Search { get => _search; set { if (SetProperty(ref _search, value)) ApplyFilter(); } }
    public Option<RepVanFilter> Filter { get => _filter; set { if (SetProperty(ref _filter, value)) ApplyFilter(); } }
    public RepVanCard? SelectedCard { get => _selected; set { if (SetProperty(ref _selected, value)) Background(OpenDetailAsync()); } }
    /// <summary>شاشة السيارة المختارة (نفس شاشة المخزن) — تُعرض تحت البطاقات.</summary>
    public WarehouseWorkspaceSectionViewModel? Detail { get => _detail; private set => SetProperty(ref _detail, value); }

    public int VanCount => _all.Count;
    public int PendingCount => _all.Count(c => c.NeedsSettlement);
    public int OverdueCount => _all.Count(c => c.Status == RepVanStatus.Overdue);
    public decimal TotalPieces => _all.Sum(c => c.BalancePieces);
    public decimal TotalCash => _all.Sum(c => c.ExpectedCash);
    public RelayCommand PrintCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        _all = await new RepVanBoardService(db).CardsAsync(DateTime.Today);
        ApplyFilter();
        foreach (var p in new[] { nameof(VanCount), nameof(PendingCount), nameof(OverdueCount), nameof(TotalPieces), nameof(TotalCash) }) OnPropertyChanged(p);
        StatusMessage = _all.Count == 0 ? "لا توجد سيارات مندوبين — عرّفها من المخازن (نوع «كاش فان» مع المندوب صاحبه)"
            : OverdueCount > 0 ? $"{OverdueCount} مندوب متأخر عن التسوية — اضغط بطاقته للتفاصيل"
            : $"{VanCount} سيارة، {PendingCount} بانتظار التسوية";
        if (Detail is not null) await Detail.LoadCoalescedAsync();
    }

    private void ApplyFilter()
    {
        var keep = SelectedCard?.VanWarehouseId;
        var q = _all.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            q = q.Where(c => c.RepName.Contains(s) || c.VanName.Contains(s) || (c.Territories?.Contains(s) ?? false));
        }
        q = Filter.Value switch
        {
            RepVanFilter.NeedsSettlement => q.Where(c => c.NeedsSettlement),
            RepVanFilter.HasStock => q.Where(c => c.BalancePieces > 0),
            _ => q
        };
        Cards.Clear();
        foreach (var c in q) Cards.Add(c);
        // تبقى السيارة المختارة مختارة بعد التحديث (بالبيانات الجديدة) دون إعادة فتح شاشتها
        _selected = Cards.FirstOrDefault(c => c.VanWarehouseId == keep);
        OnPropertyChanged(nameof(SelectedCard));
    }

    private async Task OpenDetailAsync()
    {
        if (SelectedCard is null) { Detail = null; return; }
        if (Detail?.WarehouseId == SelectedCard.VanWarehouseId) return;
        await using var db = Session.NewDb();
        var van = await db.Warehouses.AsNoTracking().FirstOrDefaultAsync(w => w.Id == SelectedCard.VanWarehouseId);
        if (van is null) { Detail = null; return; }
        var detail = new WarehouseWorkspaceSectionViewModel(Session, Dialogs, van);
        Detail = detail;
        await detail.ActivateAsync();
    }

    private void Print()
    {
        var r = new ReportDocument { CompanyName = Session.ProjectName, Title = $"سيارات المندوبين — {DateTime.Today:yyyy/MM/dd}", PrintedBy = Session.FullName };
        r.Columns.AddRange(new[] { "المندوب", "السيارة", "الحالة", "حمولة اليوم", "الرصيد الآن", "النقد المتوقع" });
        foreach (var c in Cards)
            r.Rows.Add(new[] { c.RepName, c.VanName, c.StatusText, c.TodayLoadText, c.BalanceText, $"{c.ExpectedCash:N0}" });
        r.Total("في السيارات", $"{TotalPieces:N0} قطعة");
        r.Total("بانتظار التسوية", $"{PendingCount} مندوب");
        r.Total("النقد المتوقع", $"{TotalCash:N0} د.ع", true);
        Dialogs.ShowReport(r);
    }
}

/// <summary>نسخة لوحة السيارات في وحدة المخازن (لأمين المخزن بصلاحية المخازن) — مفتاح مستقل في توزيع الأقسام.</summary>
public class WarehouseRepVansSectionViewModel : RepVansSectionViewModel
{
    public WarehouseRepVansSectionViewModel(AppSession s, IDialogService d) : base(s, d, ModuleCode.Warehouse) { }
}
