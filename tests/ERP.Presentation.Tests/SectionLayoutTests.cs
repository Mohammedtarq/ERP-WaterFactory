using ERP.Data.ProjectDb.Entities;
using ERP.Presentation.ViewModels.Production;
using ERP.Presentation.ViewModels.Sales;
using ERP.Presentation.ViewModels.Settings;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>توزيع الأقسام بقرار الإدارة: نقل شاشة إلى وحدة أخرى يعمل عليها من يملكها، وإخفاء شاشات عن دور.</summary>
[Collection("app")]
public class SectionLayoutTests
{
    private readonly AppFixture _f;
    public SectionLayoutTests(AppFixture f) => _f = f;

    private static async Task Open(ModuleViewModel module, SectionViewModel section)
    {
        module.SelectedTab = section;
        await module.IdleAsync();
        await section.IdleAsync();
    }

    [Fact]
    public async Task Admin_moves_a_screen_to_sales_and_hides_a_screen_from_the_clerk_role()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var settings = shell.Open<SettingsModuleViewModel>(ModuleCode.SystemSettings);
        var layout = settings.Section<SectionLayoutSectionViewModel>();
        await Open(settings, layout);
        layout.Role = layout.Roles.Single(r => r.Name == "موظف مبيعات");
        await layout.IdleAsync();
        Assert.DoesNotContain(layout.Rows, r => r.Entry.HomeModule == ModuleCode.SystemSettings);   // الإعدادات خارج التوزيع

        SectionLayoutRow Row<T>() => layout.Rows.Single(r => r.Entry.Key == SectionCatalog.KeyOf(typeof(T)));
        // فاتورة البيع لا تُنقل
        Row<SalesInvoiceSectionViewModel>().Target = ModuleCode.Reps;
        await layout.SaveCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("لا يُنقل"));
        dialogs.Errors.Clear();
        Row<SalesInvoiceSectionViewModel>().Target = ModuleCode.Sales;

        // نقل «متغيرات المنتج» إلى المبيعات، وإخفاء كشف الحساب عن موظف المبيعات
        Row<VariantStockSectionViewModel>().Target = ModuleCode.Sales;
        Row<CustomerStatementSectionViewModel>().Visible = false;
        layout.ModuleFilter = "المبيعات";
        Assert.All(layout.Rows, r => Assert.Equal("المبيعات", r.HomeTitle));
        await layout.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Equal((1, 1), (layout.MovedCount, layout.HiddenCount));
        await using (var db = _f.NewDb())
        {
            var recent = await db.AuditLogs.AsNoTracking().OrderByDescending(a => a.Id).Take(6).Select(a => a.TableName + ":" + a.Summary).ToListAsync();
            Assert.True(recent.Any(a => a.StartsWith("SectionPlacements") || a.StartsWith("RoleHiddenSections")), string.Join(" | ", recent));
        }

        try
        {
            // موظف المبيعات (بلا صلاحية الإنتاج): يرى الشاشة المنقولة في المبيعات، ولا يرى كشف الحساب
            var (clerkShell, _) = await _f.LoginAsync(AppFixture.ClerkUser, AppFixture.ClerkPassword);
            var sales = clerkShell.Open<SalesModuleViewModel>(ModuleCode.Sales);
            var moved = Assert.Single(sales.Tabs.OfType<VariantStockSectionViewModel>());
            Assert.Equal((ModuleCode.Sales, "الإنتاج والمختبر"), (moved.Module, moved.MovedFrom));
            Assert.DoesNotContain(sales.Tabs, t => t is CustomerStatementSectionViewModel);
            Assert.Contains(sales.Tabs, t => t is SalesInvoiceSectionViewModel);
            await Open(sales, moved);
            Assert.NotNull(moved.Rows);

            // المدير: الشاشة انتقلت من الإنتاج، وكشف الحساب ظاهر له (الإخفاء لدور الموظف فقط)
            var (adminShell, _) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
            Assert.DoesNotContain(adminShell.Open<ProductionModuleViewModel>(ModuleCode.Production).Tabs, t => t is VariantStockSectionViewModel);
            Assert.Contains(adminShell.Open<SalesModuleViewModel>(ModuleCode.Sales).Tabs, t => t is CustomerStatementSectionViewModel);
        }
        finally
        {
            // إعادة التوزيع الافتراضي لباقي الاختبارات
            layout.ModuleFilter = "";
            Row<VariantStockSectionViewModel>().Target = ModuleCode.Production;
            Row<CustomerStatementSectionViewModel>().Visible = true;
            await layout.SaveCommand.ExecuteAsync();
            Assert.Equal((0, 0), (layout.MovedCount, layout.HiddenCount));
        }
    }
}
