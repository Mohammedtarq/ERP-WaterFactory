using System.Collections.ObjectModel;
using System.Data;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Shell;

/// <summary>مرشحات التقارير المشتركة: الفترة، ومنتج/مندوب/قسم/موظف اختياري، والتجميع، وإظهار الكلفة.</summary>
public record ReportFilter(DateTime From, DateTime To, int? ItemId, int? RepEmployeeId, ReportPeriod Period, int? DepartmentId, int? EmployeeId, bool WithCost)
{
    public ReportRequest Request => new(From, To, ItemId, RepEmployeeId, Period, WithCost);
}

/// <summary>
/// تقرير في قائمة تقارير القسم: عنوانه ووصفه، والمرشحات التي يحتاجها، وهل هو للمدير فقط (الكلفة والربح)، وكيف يُبنى.
/// </summary>
public sealed record ReportOption(string Key, string Title, string Description, Func<ProjectDbContext, ReportFilter, Task<ReportTable>> Run)
{
    public bool UsesItem { get; init; }
    public bool UsesRep { get; init; }
    public bool UsesPeriod { get; init; }
    public bool UsesDepartment { get; init; }
    public bool UsesEmployee { get; init; }
    /// <summary>يحتاج صلاحية «رؤية الكلفة والأرباح» (المدير): لا يظهر لغيره.</summary>
    public bool ManagerOnly { get; init; }
    /// <summary>يُظهر الكلفة لمن له الصلاحية (ويُخفيها لغيره) دون أن يمنع التقرير.</summary>
    public bool ShowsCost { get; init; }
}

/// <summary>
/// صفحة التقارير الموحدة في كل قسم: يختار المستخدم التقرير، ثم المرشحات التي يحتاجها (الفترة، منتج واحد أو الكل، مندوب، قسم، موظف،
/// تجميع يومي/أسبوعي/شهري)، فيظهر الجدول والمجاميع، ويُطبع بهوية المعمل وشعاره كأي مستند.
/// </summary>
public abstract class ReportsSectionViewModel : SectionViewModel
{
    private ReportOption? _selected;
    private DateTime _from = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _to = DateTime.Today;
    private Option<int?>? _product, _rep, _department, _employee;
    private Option<ReportPeriod>? _period;
    private DataView? _table;
    private ReportTable? _last;
    private string _totalsText = "";

    protected ReportsSectionViewModel(AppSession s, IDialogService d, string moduleCode, string title, string description, IEnumerable<ReportOption> reports)
        : base(s, d, moduleCode, title, Icons.Statement, "#7C3AED", description)
    {
        foreach (var r in reports.Where(r => !r.ManagerOnly || Has(SpecialPermission.CostAndProfit))) Reports.Add(r);
        _selected = Reports.FirstOrDefault();
        _period = Periods[0];
        RunCommand = new AsyncRelayCommand(RunAsync);
        PrintCommand = new RelayCommand(Print);
        TodayCommand = new AsyncRelayCommand(() => RangeAsync(DateTime.Today, DateTime.Today));
        WeekCommand = new AsyncRelayCommand(() => RangeAsync(ReportsService.WeekStart(DateTime.Today), DateTime.Today));
        MonthCommand = new AsyncRelayCommand(() => RangeAsync(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), DateTime.Today));
    }

    public ObservableCollection<ReportOption> Reports { get; } = new();
    public ReportOption? SelectedReport
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(UsesItem)); OnPropertyChanged(nameof(UsesRep)); OnPropertyChanged(nameof(UsesPeriod));
            OnPropertyChanged(nameof(UsesDepartment)); OnPropertyChanged(nameof(UsesEmployee));
            Table = null;
            TotalsText = "";
        }
    }
    public bool UsesItem => SelectedReport?.UsesItem == true;
    public bool UsesRep => SelectedReport?.UsesRep == true;
    public bool UsesPeriod => SelectedReport?.UsesPeriod == true;
    public bool UsesDepartment => SelectedReport?.UsesDepartment == true;
    public bool UsesEmployee => SelectedReport?.UsesEmployee == true;

    public DateTime From { get => _from; set => SetProperty(ref _from, value); }
    public DateTime To { get => _to; set => SetProperty(ref _to, value); }
    public ObservableCollection<Option<int?>> ItemOptions { get; } = new();
    public Option<int?>? Product { get => _product; set => SetProperty(ref _product, value); }
    public ObservableCollection<Option<int?>> RepOptions { get; } = new();
    public Option<int?>? Rep { get => _rep; set => SetProperty(ref _rep, value); }
    public ObservableCollection<Option<int?>> DepartmentOptions { get; } = new();
    public Option<int?>? Department { get => _department; set => SetProperty(ref _department, value); }
    public ObservableCollection<Option<int?>> EmployeeOptions { get; } = new();
    public Option<int?>? Employee { get => _employee; set => SetProperty(ref _employee, value); }
    public IReadOnlyList<Option<ReportPeriod>> Periods { get; } = new[]
    {
        new Option<ReportPeriod>(ReportPeriod.None, "الفترة كلها"), new Option<ReportPeriod>(ReportPeriod.Day, "يومي"),
        new Option<ReportPeriod>(ReportPeriod.Week, "أسبوعي (يبدأ السبت)"), new Option<ReportPeriod>(ReportPeriod.Month, "شهري")
    };
    public Option<ReportPeriod>? Period { get => _period; set => SetProperty(ref _period, value); }

    /// <summary>الجدول المعروض (أعمدته c0، c1… وعناوينها في Caption).</summary>
    public DataView? Table { get => _table; private set { if (SetProperty(ref _table, value)) OnPropertyChanged(nameof(HasTable)); } }
    public bool HasTable => Table is not null;
    public ReportTable? Last => _last;
    public string TotalsText { get => _totalsText; private set => SetProperty(ref _totalsText, value); }
    public bool CanSeeCost => Has(SpecialPermission.CostAndProfit);

    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand PrintCommand { get; }
    public AsyncRelayCommand TodayCommand { get; }
    public AsyncRelayCommand WeekCommand { get; }
    public AsyncRelayCommand MonthCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        Fill(ItemOptions, "كل المنتجات", await db.Items.AsNoTracking().Where(i => i.IsActive && i.SourcingMethod != SourcingMethod.Purchased)
            .OrderBy(i => i.ItemName).Select(i => new Option<int?>(i.Id, i.ItemName)).ToListAsync(), v => Product = v, Product);
        Fill(RepOptions, "كل المندوبين", await db.Employees.AsNoTracking().Where(e => e.IsSalesRep)
            .OrderBy(e => e.FullName).Select(e => new Option<int?>(e.Id, e.FullName)).ToListAsync(), v => Rep = v, Rep);
        Fill(DepartmentOptions, "كل الأقسام", await db.Departments.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new Option<int?>(x.Id, x.Name)).ToListAsync(), v => Department = v, Department);
        Fill(EmployeeOptions, "كل الموظفين", await db.Employees.AsNoTracking().Where(e => e.IsActive)
            .OrderBy(e => e.FullName).Select(e => new Option<int?>(e.Id, e.FullName)).ToListAsync(), v => Employee = v, Employee);
    }

    private static void Fill(ObservableCollection<Option<int?>> target, string all, List<Option<int?>> items, Action<Option<int?>?> set, Option<int?>? keep)
    {
        target.Clear();
        target.Add(new Option<int?>(null, all));
        foreach (var i in items) target.Add(i);
        set(target.FirstOrDefault(o => keep is not null && o.Value == keep.Value) ?? target[0]);
    }

    private async Task RangeAsync(DateTime from, DateTime to)
    {
        (From, To) = (from, to);
        await RunAsync();
    }

    public ReportFilter Filter => new(From, To, UsesItem ? Product?.Value : null, UsesRep ? Rep?.Value : null,
                                      UsesPeriod ? Period?.Value ?? ReportPeriod.None : ReportPeriod.None,
                                      UsesDepartment ? Department?.Value : null, UsesEmployee ? Employee?.Value : null,
                                      SelectedReport?.ShowsCost == true && CanSeeCost);

    private async Task RunAsync()
    {
        if (SelectedReport is not { } report) { Dialogs.Error("اختر التقرير"); return; }
        if (From.Date > To.Date) { Dialogs.Error("بداية الفترة بعد نهايتها"); return; }
        if (report.ManagerOnly && !CanSeeCost) { Dialogs.Error("هذا التقرير للمدير فقط"); return; }
        await using var db = Session.NewDb();
        var t = await report.Run(db, Filter);
        _last = t;
        var table = new DataTable();
        for (var i = 0; i < t.Columns.Count; i++) table.Columns.Add(new DataColumn($"c{i}", typeof(string)) { Caption = t.Columns[i] });
        foreach (var row in t.Rows) table.Rows.Add(row.Cast<object>().ToArray());
        Table = table.DefaultView;
        TotalsText = string.Join("   ", t.Totals.Select(x => $"{x.Label}: {x.Value}"));
        StatusMessage = t.Rows.Count == 0 ? "لا بيانات في هذه الفترة" : $"{t.Rows.Count} سطر";
    }

    private void Print()
    {
        if (_last is not { } t) { Dialogs.Error("اعرض التقرير أولًا"); return; }
        var r = new ReportDocument { Key = $"report-{SelectedReport?.Key}", CompanyName = Session.ProjectName, Title = t.Title, PrintedBy = Session.Username, Notes = t.Notes };
        r.HeaderFields.Add(new ReportField("الفترة", $"{From:yyyy/MM/dd} — {To:yyyy/MM/dd}"));
        if (UsesItem) r.HeaderFields.Add(new ReportField("المنتج", Product?.Label ?? "كل المنتجات"));
        if (UsesRep) r.HeaderFields.Add(new ReportField("المندوب", Rep?.Label ?? "كل المندوبين"));
        if (UsesDepartment) r.HeaderFields.Add(new ReportField("القسم", Department?.Label ?? "كل الأقسام"));
        if (UsesEmployee) r.HeaderFields.Add(new ReportField("الموظف", Employee?.Label ?? "كل الموظفين"));
        if (UsesPeriod) r.HeaderFields.Add(new ReportField("التجميع", Period?.Label ?? ""));
        r.Columns.AddRange(t.Columns);
        foreach (var row in t.Rows) r.Rows.Add(row);
        foreach (var (label, value) in t.Totals) r.Total(label, value);
        Dialogs.ShowReport(r);
    }
}
