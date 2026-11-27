using ERP.Data.ProjectDb.Entities;
using ERP.Presentation.ViewModels.Finance;
using ERP.Presentation.ViewModels.HR;
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
    public async Task Changing_role_reloads_only_the_rows_and_keeps_the_roles_list()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var settings = shell.Open<SettingsModuleViewModel>(ModuleCode.SystemSettings);
        var layout = settings.Section<SectionLayoutSectionViewModel>();
        await Open(settings, layout);
        var count = layout.Roles.Count;
        var names = layout.Roles.Select(r => r.Name).ToList();
        layout.Role = layout.Roles.Single(r => r.Name == "موظف مبيعات");
        await layout.IdleAsync();
        layout.Role = layout.Roles.First(r => r.Name != "موظف مبيعات");
        await layout.IdleAsync();
        Assert.Equal(count, layout.Roles.Count);
        Assert.Equal(names, layout.Roles.Select(r => r.Name).ToList());
        Assert.NotEmpty(layout.Rows);
        Assert.Empty(dialogs.Errors);
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
            Assert.Equal((ModuleCode.Sales, "الإنتاج"), (moved.Module, moved.MovedFrom));
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
    // ===== تبويبات الصفحات المدموجة: تُدرج في «توزيع الأقسام» لإخفائها عن دور أو نقلها إلى وحدة أخرى =====

    private async Task<(SectionLayoutSectionViewModel layout, RecordingDialogs dialogs)> OpenLayoutAsync(string role)
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var settings = shell.Open<SettingsModuleViewModel>(ModuleCode.SystemSettings);
        var layout = settings.Section<SectionLayoutSectionViewModel>();
        await Open(settings, layout);
        layout.Role = layout.Roles.Single(r => r.Name == role);
        await layout.IdleAsync();
        return (layout, dialogs);
    }

    [Fact]
    public async Task Hub_tabs_are_listed_in_the_layout_with_their_page_name_but_not_as_standalone_search_entries()
    {
        // الوضع الحقيقي: الشاشات داخل الصفحات المدموجة فقط (الاختبارات تعرضها منفصلة أيضًا بحكم الإعداد العام)
        HrModuleViewModel.ShowSeparateScreens = false;
        try
        {
            var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
            var entries = SectionCatalog.Entries(shell.Session, dialogs);
            var late = entries.Single(e => e.Key == SectionCatalog.KeyOf(typeof(LateRulesSectionViewModel)));
            Assert.Equal(ModuleCode.HR, late.HomeModule);
            Assert.Equal("الحضور", late.HubTitle);
            Assert.Contains("(ضمن: الحضور)", late.DisplayTitle);
            // الصفحة نفسها تبقى سطرًا، لكن تبويبها لا يظهر مستقلًا في البحث السريع و«مفضلتي»
            Assert.Contains(entries, e => e.Key == "AttendanceHub");
            Assert.DoesNotContain(SectionCatalog.QuickEntries(shell.Session, dialogs), q => q.Key == late.Key);
            // العلاوة السنوية لا تُنقل (قاعدة البيانات تتحقق من صلاحية الموارد البشرية)
            Assert.False(entries.Single(e => e.Key == SectionCatalog.KeyOf(typeof(AnnualRaisesSectionViewModel))).Movable);
        }
        finally { HrModuleViewModel.ShowSeparateScreens = true; }
    }

    [Fact]
    public async Task Hiding_a_hub_tab_removes_it_from_the_page_and_hiding_every_tab_removes_the_page()
    {
        var (layout, dialogs) = await OpenLayoutAsync("مدير عام");
        layout.ModuleFilter = "الموارد البشرية";
        SectionLayoutRow Row<T>() => layout.Rows.Single(r => r.Entry.Key == SectionCatalog.KeyOf(typeof(T)));
        try
        {
            Row<LateRulesSectionViewModel>().Visible = false;
            Row<LeavesSectionViewModel>().Visible = false;
            Row<LeaveSettingsSectionViewModel>().Visible = false;
            await layout.SaveCommand.ExecuteAsync();
            Assert.Empty(dialogs.Errors);

            var (shell, _) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
            var hr = shell.Open<HrModuleViewModel>(ModuleCode.HR);
            var attendance = hr.Hubs.Single(h => h is AttendanceHubSectionViewModel);
            Assert.DoesNotContain(attendance.Tabs, t => t.Child is LateRulesSectionViewModel);
            Assert.Contains(attendance.AllTabs, t => t.Child is LateRulesSectionViewModel);       // الكائن متاح للروابط الداخلية
            Assert.NotNull(attendance.Child<LateRulesSectionViewModel>());
            Assert.Contains(hr.Tabs, t => t is AttendanceHubSectionViewModel);
            // أُخفيت كل تبويبات «الإجازات»: لا تظهر الصفحة فارغة، وخصائص الوحدة الداخلية لا تنهار
            Assert.DoesNotContain(hr.Tabs, t => t is LeavesHubSectionViewModel);
            Assert.NotNull(hr.Leaves);
        }
        finally
        {
            Row<LateRulesSectionViewModel>().Visible = true;
            Row<LeavesSectionViewModel>().Visible = true;
            Row<LeaveSettingsSectionViewModel>().Visible = true;
            await layout.SaveCommand.ExecuteAsync();
            Assert.Equal(0, layout.HiddenCount);
        }
    }

    [Fact]
    public async Task Moving_a_hub_tab_to_finance_drops_it_from_the_page_and_shows_it_in_finance()
    {
        var (layout, dialogs) = await OpenLayoutAsync("مدير عام");
        layout.ModuleFilter = "الموارد البشرية";
        SectionLayoutRow Row<T>() => layout.Rows.Single(r => r.Entry.Key == SectionCatalog.KeyOf(typeof(T)));
        try
        {
            // العلاوة السنوية لا تُنقل
            Row<AnnualRaisesSectionViewModel>().Target = ModuleCode.Finance;
            await layout.SaveCommand.ExecuteAsync();
            Assert.Contains(dialogs.Errors, e => e.Contains("لا يُنقل"));
            dialogs.Errors.Clear();
            Row<AnnualRaisesSectionViewModel>().Target = ModuleCode.HR;

            Row<LateRulesSectionViewModel>().Target = ModuleCode.Finance;
            await layout.SaveCommand.ExecuteAsync();
            Assert.Empty(dialogs.Errors);
            Assert.Equal(1, layout.MovedCount);

            var (shell, _) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
            var hr = shell.Open<HrModuleViewModel>(ModuleCode.HR);
            Assert.DoesNotContain(hr.Hubs.Single(h => h is AttendanceHubSectionViewModel).Tabs, t => t.Child is LateRulesSectionViewModel);
            var finance = shell.Open<FinanceModuleViewModel>(ModuleCode.Finance);
            var moved = Assert.Single(finance.Tabs.OfType<LateRulesSectionViewModel>());
            Assert.Equal((ModuleCode.Finance, "الموارد البشرية"), (moved.Module, moved.MovedFrom));
            await Open(finance, moved);
        }
        finally
        {
            Row<LateRulesSectionViewModel>().Target = ModuleCode.HR;
            await layout.SaveCommand.ExecuteAsync();
            Assert.Equal(0, layout.MovedCount);
        }
    }
}
