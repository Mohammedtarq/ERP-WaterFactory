using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Reps;

/// <summary>مجموع حافز مندوب في الفترة.</summary>
public record RepIncentiveTotalRow(string RepName, decimal Loaded, decimal Returned, decimal Free, decimal Net, decimal Amount);

/// <summary>
/// جدول حوافز المندوبين للاطلاع: المحمّل والراجع والمجاني والمباع بالعدد لكل صنف وعبوة، والمبلغ المتجمع.
/// لا قيد يومي: المجموع الشهري يُصرف مع الراتب.
/// </summary>
public class RepIncentivesSectionViewModel : SectionViewModel
{
    private DateTime _from = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _to = DateTime.Today;
    private Option<int?>? _rep;

    public RepIncentivesSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Reps, "حوافز المندوبين", Icons.Star, "#F97316", "المباع (المحمّل − الراجع) بالشرنك والكارتون × مبلغ الحافز — يتجمع على الشهر ويُصرف مع الراتب")
    {
        PrintCommand = new RelayCommand(Print);
    }

    protected override bool ReloadOnActivate => true;
    public DateTime From { get => _from; set => SetProperty(ref _from, value); }
    public DateTime To { get => _to; set => SetProperty(ref _to, value); }
    public ObservableCollection<Option<int?>> RepOptions { get; } = new();
    public Option<int?>? Rep { get => _rep; set { if (SetProperty(ref _rep, value) && value is not null) Background(LoadAsync()); } }
    public ObservableCollection<RepIncentiveRow> Rows { get; } = new();
    public ObservableCollection<RepIncentiveTotalRow> Totals { get; } = new();
    public decimal TotalAmount => Totals.Sum(t => t.Amount);
    public RelayCommand PrintCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        if (RepOptions.Count == 0)
        {
            RepOptions.Add(new Option<int?>(null, "كل المندوبين"));
            foreach (var e in await db.Employees.AsNoTracking().Where(e => e.IsSalesRep).OrderBy(e => e.FullName).ToListAsync())
                RepOptions.Add(new Option<int?>(e.Id, e.FullName));
            _rep = RepOptions[0];
            OnPropertyChanged(nameof(Rep));
        }
        var rows = await new RepIncentiveService(db).RowsAsync(From, To, Rep?.Value);
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        Totals.Clear();
        foreach (var g in rows.GroupBy(r => r.RepName))
            Totals.Add(new RepIncentiveTotalRow(g.Key, g.Sum(r => r.Loaded), g.Sum(r => r.Returned), g.Sum(r => r.Free), g.Sum(r => r.Net), g.Sum(r => r.Amount)));
        OnPropertyChanged(nameof(TotalAmount));
        StatusMessage = rows.Count == 0 ? "لا تحميل للمندوبين في هذه الفترة"
            : rows.Any(r => r.Rate == 0) ? $"مجموع الحوافز {TotalAmount:N0} د.ع — بعض العبوات بلا مبلغ حافز (حدده من الموارد البشرية ← حافز المندوب)"
            : $"مجموع الحوافز {TotalAmount:N0} د.ع لـ{Totals.Count} مندوب";
    }

    private void Print()
    {
        var r = new ReportDocument { CompanyName = Session.ProjectName, Title = $"حوافز المندوبين — {From:yyyy/MM/dd} إلى {To:yyyy/MM/dd}", PrintedBy = Session.FullName };
        r.Columns.AddRange(new[] { "المندوب", "الصنف", "العبوة", "المحمّل", "الراجع", "المجاني", "المباع", "الحافز للعبوة", "المبلغ" });
        foreach (var x in Rows)
            r.Rows.Add(new[] { x.RepName, x.ItemName, x.LevelName, $"{x.Loaded:N0}", $"{x.Returned:N0}", $"{x.Free:N0}", $"{x.Net:N0}", $"{x.Rate:N0}", $"{x.Amount:N0}" });
        foreach (var t in Totals) r.Total(t.RepName, $"{t.Amount:N0} د.ع");
        r.Total("المجموع", $"{TotalAmount:N0} د.ع", true);
        Dialogs.ShowReport(r);
    }
}
