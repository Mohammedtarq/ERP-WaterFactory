using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;

namespace ERP.Presentation.ViewModels.Sales;

/// <summary>
/// «تقرير الخصم الشهري» (قسم «ج»): الوكلاء وأصحاب الخصم التشجيعي (علامة بطاقة الزبون)، إجمالي خصم كل منهم،
/// عدد فواتيره، ما اشتراه قبل الخصم، ونسبة الخصم — من الفواتير المرحّلة.
/// </summary>
public class DiscountReportSectionViewModel : SectionViewModel
{
    private int _year = DateTime.Today.Year;
    private int _month = DateTime.Today.Month;

    public DiscountReportSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Sales, "تقرير الخصم الشهري", Icons.Statement, "#B45309", "خصم الوكلاء وأصحاب الخصم التشجيعي لكل شهر")
    {
        PrintCommand = new RelayCommand(Print);
    }

    protected override bool ReloadOnActivate => true;
    public IReadOnlyList<int> Years { get; } = Enumerable.Range(DateTime.Today.Year - 3, 5).Reverse().ToList();
    public IReadOnlyList<int> Months { get; } = Enumerable.Range(1, 12).ToList();
    public int Year { get => _year; set { if (SetProperty(ref _year, value)) Background(LoadAsync()); } }
    public int Month { get => _month; set { if (SetProperty(ref _month, value)) Background(LoadAsync()); } }
    public ObservableCollection<DiscountReportRow> Rows { get; } = new();
    public decimal TotalDiscount => Rows.Sum(r => r.Discount);
    public RelayCommand PrintCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var rows = await new DiscountReportService(db).MonthAsync(Year, Month);
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        OnPropertyChanged(nameof(TotalDiscount));
        StatusMessage = Rows.Count == 0
            ? "لا يوجد زبون بعلامة «وكيل» أو «صاحب خصم تشجيعي» — ضعها من بطاقة الزبون"
            : $"إجمالي خصم الشهر {TotalDiscount:N0} د.ع لـ {Rows.Count(r => r.InvoicesCount > 0)} زبون";
    }

    private void Print()
    {
        var r = new ReportDocument { Key = "discount-report", CompanyName = Session.ProjectName, Title = $"تقرير الخصم الشهري — {Month}/{Year}", PrintedBy = Session.FullName };
        r.Columns.AddRange(new[] { "الزبون", "العلامة", "الفواتير", "قبل الخصم", "الخصم", "%", "الصافي" });
        foreach (var x in Rows)
            r.Rows.Add(new[] { x.CustomerName, ArabicLabels.Of(x.Kind), $"{x.InvoicesCount:N0}", $"{x.GrossBeforeDiscount:N0}", $"{x.Discount:N0}", $"{x.DiscountPercent:N1}", $"{x.Net:N0}" });
        foreach (var g in Rows.GroupBy(x => x.Kind))
            r.Total($"خصم {ArabicLabels.Of(g.Key)}", $"{g.Sum(x => x.Discount):N0} د.ع");
        r.Total("إجمالي الخصم", $"{TotalDiscount:N0} د.ع", true);
        Dialogs.ShowReport(r);
    }
}
