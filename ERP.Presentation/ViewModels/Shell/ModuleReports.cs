using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Services;

namespace ERP.Presentation.ViewModels.Shell;

/// <summary>تقارير المبيعات: المباع والمرتجع والمجاني لكل منتج (أو منتج واحد ومندوب)، والكلفة والربح للمدير فقط.</summary>
public class SalesReportsSectionViewModel : ReportsSectionViewModel
{
    public SalesReportsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Sales, "تقارير المبيعات", "المبيعات والمرتجع والمجاني حسب المنتج، والكلفة والربح (للمدير)", Catalog) { }

    /// <summary>سجل المجاني (مندوب ومخزن) — يظهر في تقارير المبيعات وفي تقارير المخازن.</summary>
    public static readonly ReportOption FreeLog = new("free-log", "سجل المجاني",
        "كل ما خرج مجانًا: الجهة والسبب والاسم الموحد للمنتج والعدد والمندوب، مع إجمالي لكل جهة ولكل شهر",
        (db, f) => new ReportsService(db).FreeIssueLogAsync(f.Request)) { UsesItem = true, UsesRep = true };

    public static readonly ReportOption[] Catalog =
    {
        new("sales-by-product", "المبيعات والمرتجع والمجاني حسب المنتج", "لكل منتج بعبوته ومتغيره: المباع وقيمته، والمرتجع، والمجاني، والصافي",
            (db, f) => new ReportsService(db).SalesByProductAsync(f.Request)) { UsesItem = true, UsesRep = true },
        FreeLog,
        new("cost-profit", "الكلفة والربح حسب المنتج", "صافي المبيع وصافي الكلفة والربح وهامشه، وكلفة المجاني — للمدير فقط",
            (db, f) => new ReportsService(db).CostAndProfitAsync(f.Request)) { UsesItem = true, UsesRep = true, ManagerOnly = true },
    };
}

/// <summary>تقارير المخازن: سجل المجاني (مسحوب المخزن ومجاني المندوبين) بالجهة والاسم الموحد والعدد.</summary>
public class WarehouseReportsSectionViewModel : ReportsSectionViewModel
{
    public WarehouseReportsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Warehouse, "تقارير المخازن", "سجل المجاني: الجهة والمنتج والعدد والسبب والمندوب", new[] { SalesReportsSectionViewModel.FreeLog }) { }
}

/// <summary>تقارير الإنتاج: اليومي والأسبوعي والشهري لمنتج أو للكل، بالكلفة لمن له الصلاحية.</summary>
public class ProductionReportsSectionViewModel : ReportsSectionViewModel
{
    public ProductionReportsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Production, "تقارير الإنتاج", "الإنتاج اليومي والأسبوعي والشهري بالكلفة لمنتج أو للكل", Catalog) { }

    public static readonly ReportOption[] Catalog =
    {
        new("production", "الإنتاج بالكلفة", "الكمية المنتجة لكل منتج ومتغير، يوميًا أو أسبوعيًا (من السبت) أو شهريًا، وكلفتها لمن له الصلاحية",
            (db, f) => new ReportsService(db).ProductionAsync(f.Request)) { UsesItem = true, UsesPeriod = true, ShowsCost = true },
    };
}

/// <summary>
/// تقارير الموارد البشرية: تقرير الموظف (غياب، إجازات، تأخر، زمنيات، عقوبات، قطع راتب، سلف، ترقيات، تقييم، حافز)
/// وسجله التفصيلي، والحضور (اليوم أو فترة) والمتأخرون والغائبون — لقسم أو موظف أو للكل.
/// </summary>
public class HrReportsSectionViewModel : ReportsSectionViewModel
{
    public HrReportsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.HR, "تقارير الموارد البشرية", "تقرير الموظف والحضور والمتأخرين والغائبين لقسم أو موظف", Catalog) { }

    private static HrReportRequest Hr(ReportFilter f) => new(f.From, f.To, f.DepartmentId, f.EmployeeId);

    public static readonly ReportOption[] Catalog =
    {
        new("hr-attendance", "الحضور (اليوم أو فترة)", "لكل موظف: حاضر أو متأخر أو غائب أو إجازة، بوقت الدخول والخروج ودقائق التأخر",
            (db, f) => new HrReportsService(db).AttendanceAsync(Hr(f))) { UsesDepartment = true, UsesEmployee = true },
        new("hr-late", "المتأخرون", "من تأخر: التاريخ ووقت الدخول ودقائق التأخر، ومجموعها",
            (db, f) => new HrReportsService(db).AttendanceAsync(Hr(f), AttendanceStatus.Late)) { UsesDepartment = true, UsesEmployee = true },
        new("hr-absent", "الغائبون", "من غاب في الفترة (لا تشمل الإجازات المسجّلة)",
            (db, f) => new HrReportsService(db).AttendanceAsync(Hr(f), AttendanceStatus.Absent)) { UsesDepartment = true, UsesEmployee = true },
        new("hr-employee", "تقرير الموظف", "غياب، إجازات، تأخر، زمنيات، عقوبات، قطع راتب، سلف، ترقيات، تقييم، حافز شهري — لكل موظف",
            (db, f) => new HrReportsService(db).EmployeeSummaryAsync(Hr(f))) { UsesDepartment = true, UsesEmployee = true },
        new("hr-employee-detail", "سجل الموظف التفصيلي", "كل حدث للموظف المختار بتاريخه وتفصيله ومبلغه",
            (db, f) => new HrReportsService(db).EmployeeDetailAsync(Hr(f))) { UsesEmployee = true },
    };
}
