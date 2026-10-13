using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.ViewModels.Production;
using ERP.Presentation.ViewModels.Sales;
using ERP.Presentation.ViewModels.Shell;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// صفحة التقارير الموحدة: تقارير المبيعات في «المبيعات» (الكلفة والربح للمدير فقط)، وتقرير الإنتاج بالكلفة في «الإنتاج»؛
/// المرشحات حسب التقرير، والعرض بنطاق سريع (اليوم/الأسبوع/الشهر)، والطباعة بعنوان التقرير ومرشحاته ومجاميعه.
/// </summary>
[Collection("app")]
public class ReportsScreenTests
{
    private readonly AppFixture _f;
    public ReportsScreenTests(AppFixture f) => _f = f;

    [Fact]
    public async Task Reports_page_runs_prints_and_hides_profit_from_non_managers()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var sales = shell.Open<SalesModuleViewModel>(ModuleCode.Sales);
        await sales.IdleAsync();
        var page = sales.Section<SalesReportsSectionViewModel>();
        sales.SelectedTab = page;
        await sales.IdleAsync();
        await page.IdleAsync();
        Assert.Equal(new[] { "sales-by-product", "cost-profit" }, page.Reports.Select(r => r.Key));
        Assert.True(page.UsesItem && page.UsesRep && !page.UsesPeriod);
        Assert.Equal("كل المنتجات", page.Product!.Label);
        Assert.Equal("كل المندوبين", page.Rep!.Label);

        await page.MonthCommand.ExecuteAsync();
        Assert.True(page.HasTable);
        Assert.Equal("المنتج", page.Table!.Table!.Columns[0].Caption);
        Assert.Contains("صافي المبيعات", page.TotalsText);
        page.PrintCommand.Execute(null);
        var printed = dialogs.Reports.Last();
        Assert.Equal("المبيعات والمرتجع والمجاني حسب المنتج", printed.Title);
        Assert.Contains(printed.HeaderFields, h => h.Label == "المنتج" && h.Value == "كل المنتجات");
        Assert.Contains(printed.Totals, t => t.Label == "صافي المبيعات");

        page.SelectedReport = page.Reports.Single(r => r.Key == "cost-profit");
        Assert.False(page.HasTable);                                              // تقرير جديد: الجدول يُفرَّغ
        await page.TodayCommand.ExecuteAsync();
        Assert.Contains("الربح الإجمالي", page.TotalsText);

        // الإنتاج: تجميع أسبوعي بالكلفة للمدير
        var prod = shell.Open<ProductionModuleViewModel>(ModuleCode.Production);
        await prod.IdleAsync();
        var production = prod.Section<ProductionReportsSectionViewModel>();
        prod.SelectedTab = production;
        await prod.IdleAsync();
        await production.IdleAsync();
        Assert.True(production.UsesPeriod);
        production.Period = production.Periods.Single(p => p.Value == ReportPeriod.Week);
        await production.MonthCommand.ExecuteAsync();
        Assert.Contains("كلفة العبوة", Enumerable.Range(0, production.Table!.Table!.Columns.Count).Select(i => production.Table.Table.Columns[i].Caption));
        Assert.True(production.Filter.WithCost);

        // الموارد البشرية: حضور اليوم لقسم أو موظف، والسجل التفصيلي يطلب موظفًا
        var hrModule = shell.Open<ERP.Presentation.ViewModels.HR.HrModuleViewModel>(ModuleCode.HR);
        await hrModule.IdleAsync();
        var hrPage = hrModule.Section<HrReportsSectionViewModel>();
        hrModule.SelectedTab = hrPage;
        await hrModule.IdleAsync();
        await hrPage.IdleAsync();
        Assert.Equal(new[] { "hr-attendance", "hr-late", "hr-absent", "hr-employee", "hr-employee-detail" }, hrPage.Reports.Select(r => r.Key));
        Assert.True(hrPage.UsesDepartment && hrPage.UsesEmployee && !hrPage.UsesItem);
        Assert.Equal("كل الأقسام", hrPage.Department!.Label);
        await hrPage.TodayCommand.ExecuteAsync();
        Assert.Equal("حضور اليوم", hrPage.Last!.Title);
        hrPage.SelectedReport = hrPage.Reports.Single(r => r.Key == "hr-employee-detail");
        Assert.False(hrPage.UsesDepartment);
        await hrPage.MonthCommand.ExecuteAsync();
        Assert.Contains("اختر الموظف", hrPage.Last!.Subtitle);

        // موظف المبيعات بلا صلاحية الكلفة: لا يرى تقرير الربح، والإنتاج بلا كلفة
        var (clerkShell, _) = await _f.LoginAsync(AppFixture.ClerkUser, AppFixture.ClerkPassword);
        Assert.Equal(new[] { "sales-by-product" }, new SalesReportsSectionViewModel(clerkShell.Session, dialogs).Reports.Select(r => r.Key));
        Assert.False(new ProductionReportsSectionViewModel(clerkShell.Session, dialogs).Filter.WithCost);

        page.From = DateTime.Today.AddDays(1);
        await page.RunCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("بداية الفترة بعد نهايتها"));
        Assert.Empty(_f.Unhandled);
    }
}
