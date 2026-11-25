using ERP.Data.ProjectDb.Entities;
using ERP.Presentation.ViewModels.Finance;
using ERP.Presentation.ViewModels.Settings;
using ERP.Presentation.ViewModels.Shell;
using ERP.Presentation.ViewModels.Warehouse;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>«المخازن»: إحدى وعشرون بطاقة صارت ثلاث مساحات + ست صفحات بتبويبات ملوّنة (عملياتها مترابطة) + التقارير منفردة.</summary>
[Collection("app")]
public class WarehouseHubsTests
{
    private readonly AppFixture _f;
    public WarehouseHubsTests(AppFixture f) => _f = f;

    [Fact]
    public async Task Warehouse_screens_merge_into_six_related_pages_and_reports_stay_alone()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        WarehouseModuleViewModel.ShowSeparateScreens = false;
        try
        {
            var module = new WarehouseModuleViewModel(shell.Session, dialogs);
            Assert.Equal(6, module.Hubs.Count);
            Assert.All(module.Hubs, h => Assert.Contains(h, module.Tabs));
            Assert.Contains(module.Tabs, t => t is WarehouseReportsSectionViewModel);
            // لا شاشة من المدموجة تظهر مستقلة
            Assert.DoesNotContain(module.Tabs, t => WarehouseModuleViewModel.MergedScreens.Contains(t.GetType()));
            Assert.Equal(17, module.Hubs.Sum(h => h.Tabs.Count));

            string[] Titles<T>() where T : HubSectionViewModel => module.Hubs.OfType<T>().Single().Tabs.Select(t => t.Title).ToArray();
            Assert.Equal(new[] { "طلبات التجهيز", "سيارات المندوبين", "تسوية المنتجات", "تأكيد التسويات" }, Titles<WarehouseRepsHubSectionViewModel>());
            Assert.Equal(new[] { "الجرد", "التلف والمسحوب وفرق الجرد", "بيع المواد التالفة", "جهات المسحوب المجاني" }, Titles<StockLossesHubSectionViewModel>());
            Assert.Equal(new[] { "الرصيد الحالي", "إعدادات التنبيهات" }, Titles<StockBalancesHubSectionViewModel>());
            Assert.Equal(new[] { "احتياجات التصنيع", "قوائم المواد" }, Titles<ManufacturingHubSectionViewModel>());
            Assert.Equal(new[] { "الأصناف", "هيكلية التعبئة", "قوالب التعبئة" }, Titles<ItemsPackagingHubSectionViewModel>());
            Assert.Equal(new[] { "تعريف المخازن", "مواقع المخزن" }, Titles<WarehouseSetupHubSectionViewModel>());

            // خصائص الوحدة تشير إلى الشاشات داخل الصفحات (للروابط الداخلية)
            var children = module.Hubs.SelectMany(h => h.AllTabs).Select(t => t.Child).ToList();
            Assert.Contains(children, c => ReferenceEquals(c, module.Stocktake));
            Assert.Contains(children, c => ReferenceEquals(c, module.Vans));
            Assert.Contains(children, c => ReferenceEquals(c, module.PrepareLoads));
            Assert.Contains(children, c => ReferenceEquals(c, module.ProductSettlement));
            Assert.Contains(children, c => ReferenceEquals(c, module.DamagedSales));

            // البحث السريع يرى الصفحات لا تبويباتها
            var keys = SectionCatalog.QuickEntries(shell.Session, dialogs).Select(e => e.Key).ToList();
            Assert.Contains("WarehouseRepsHub", keys);
            Assert.DoesNotContain("Stocktake", keys);
            Assert.Empty(dialogs.Errors);
        }
        finally { WarehouseModuleViewModel.ShowSeparateScreens = true; }
    }

    [Fact]
    public async Task Settlement_confirm_tab_can_be_moved_to_finance_for_the_cashier_and_leaves_the_reps_page()
    {
        WarehouseModuleViewModel.ShowSeparateScreens = false;
        SectionLayoutSectionViewModel? layout = null;
        SectionLayoutRow? row = null;
        try
        {
            var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
            var settings = shell.Open<SettingsModuleViewModel>(ModuleCode.SystemSettings);
            layout = settings.Section<SectionLayoutSectionViewModel>();
            settings.SelectedTab = layout;
            await settings.IdleAsync();
            await layout.IdleAsync();
            layout.Role = layout.Roles.Single(r => r.Name == "مدير عام");
            await layout.IdleAsync();
            layout.ModuleFilter = "المخازن";
            row = layout.Rows.Single(r => r.Entry.Key == "SettlementConfirm");
            Assert.True(row.Movable);
            Assert.Contains("(ضمن: المندوبون)", row.DisplayTitle);

            row.Target = ModuleCode.Finance;
            await layout.SaveCommand.ExecuteAsync();
            Assert.Empty(dialogs.Errors);

            var (shell2, _) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
            var warehouse = new WarehouseModuleViewModel(shell2.Session, dialogs);
            var reps = warehouse.Hubs.OfType<WarehouseRepsHubSectionViewModel>().Single();
            Assert.Equal(new[] { "طلبات التجهيز", "سيارات المندوبين", "تسوية المنتجات" }, reps.Tabs.Select(t => t.Title));
            var finance = shell2.Open<FinanceModuleViewModel>(ModuleCode.Finance);
            var moved = Assert.Single(finance.Tabs.OfType<SettlementConfirmSectionViewModel>());
            Assert.Equal((ModuleCode.Finance, "المخازن"), (moved.Module, moved.MovedFrom));
            finance.SelectedTab = moved;
            await finance.IdleAsync();
            await moved.IdleAsync();
            Assert.Empty(dialogs.Errors);
        }
        finally
        {
            // إعادة التوزيع الافتراضي لباقي الاختبارات
            if (layout is not null && row is not null)
            {
                row.Target = ModuleCode.Warehouse;
                await layout.SaveCommand.ExecuteAsync();
            }
            WarehouseModuleViewModel.ShowSeparateScreens = true;
        }
    }
}
