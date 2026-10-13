using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Services;

namespace ERP.Presentation.ViewModels.Shell;

/// <summary>تقارير المبيعات: المباع والمرتجع والمجاني لكل منتج (أو منتج واحد ومندوب)، والكلفة والربح للمدير فقط.</summary>
public class SalesReportsSectionViewModel : ReportsSectionViewModel
{
    public SalesReportsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Sales, "تقارير المبيعات", "المبيعات والمرتجع والمجاني حسب المنتج، والكلفة والربح (للمدير)", Catalog) { }

    public static readonly ReportOption[] Catalog =
    {
        new("sales-by-product", "المبيعات والمرتجع والمجاني حسب المنتج", "لكل منتج بعبوته ومتغيره: المباع وقيمته، والمرتجع، والمجاني، والصافي",
            (db, f) => new ReportsService(db).SalesByProductAsync(f.Request)) { UsesItem = true, UsesRep = true },
        new("cost-profit", "الكلفة والربح حسب المنتج", "صافي المبيع وصافي الكلفة والربح وهامشه، وكلفة المجاني — للمدير فقط",
            (db, f) => new ReportsService(db).CostAndProfitAsync(f.Request)) { UsesItem = true, UsesRep = true, ManagerOnly = true },
    };
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
