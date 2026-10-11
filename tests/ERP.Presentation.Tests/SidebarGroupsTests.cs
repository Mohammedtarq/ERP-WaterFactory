using ERP.Data.ProjectDb.Entities;
using ERP.Presentation.ViewModels.HR;
using ERP.Presentation.ViewModels.Production;
using ERP.Presentation.ViewModels.Shell;
using ERP.Presentation.ViewModels.Warehouse;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// التنقل الجانبي: أقسام كل وحدة في مجموعات ملوّنة (تفتح مجموعة واحدة بأقسامها)، بدل شريط تبويبات طويل.
/// </summary>
[Collection("app")]
public class SidebarGroupsTests
{
    private readonly AppFixture _f;
    public SidebarGroupsTests(AppFixture f) => _f = f;

    [Fact]
    public async Task Every_section_of_every_module_belongs_to_a_named_group_and_selecting_opens_its_group()
    {
        var (shell, _) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        foreach (var nav in shell.NavItems.Where(n => n.ModuleCode != ModuleCode.Dashboard))
        {
            shell.SelectedItem = nav;
            await shell.IdleAllAsync();                          // أقسام تُبنى من البيانات (مساحات المخازن) تكتمل أولًا
            var module = Assert.IsAssignableFrom<ModuleViewModel>(shell.CurrentModule);
            Assert.Same(module, shell.CurrentModuleVm);
            var sections = module.Tabs.OfType<SectionViewModel>().ToList();

            // لا قسم خارج مجموعاته المسمّاة (يُضاف مفتاحه لـ SectionGrouping إن ظهر هنا)، ولا قسم في مجموعتين
            Assert.DoesNotContain(module.Groups, g => g.Name == SectionGrouping.OtherName);
            Assert.All(module.Groups, g => Assert.NotEmpty(g.Items));
            Assert.Equal(sections.Count, module.Groups.Sum(g => g.Count));
            Assert.Equal(sections.Count, module.Groups.SelectMany(g => g.Items).Distinct().Count());
            Assert.True(module.Groups.Count <= 6, $"{module.Title}: مجموعات كثيرة");

            // اختيار قسم يفتح مجموعته وحدها ويظلّله بلون المجموعة، والمسار يذكر المجموعة
            var target = module.Groups.Last().Items.Last();
            module.SelectedTab = target;
            await module.LastActivation;
            Assert.Equal(1, module.Groups.Count(g => g.IsOpen));
            Assert.True(module.Groups.Last().IsOpen);
            Assert.True(target.IsCurrent);
            Assert.Equal(module.Groups.Last().Color, target.GroupColor);
            Assert.Equal($"{module.Groups.Last().Name} ‹ {target.DisplayTitle}", module.Breadcrumb);

            module.GoHomeCommand.Execute(null);
            Assert.All(module.Groups, g => Assert.False(g.IsOpen));
            Assert.True(module.Home.IsCurrent);
            Assert.False(target.IsCurrent);
            Assert.Equal("الرئيسية", module.Breadcrumb);
        }
        await shell.IdleAllAsync();
        Assert.Empty(_f.Unhandled);
    }

    [Fact]
    public async Task Opening_a_group_shows_its_first_section_and_closing_it_keeps_the_screen()
    {
        var (shell, _) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var hr = shell.Open<HrModuleViewModel>(ModuleCode.HR);
        Assert.Equal(new[] { "الموظفون", "الحضور", "الحوافز", "الرواتب" }, hr.Groups.Select(g => g.Name));

        var attendance = hr.Groups.Single(g => g.Name == "الحضور");
        hr.OpenGroupCommand.Execute(attendance);
        await hr.LastActivation;
        Assert.Same(attendance.Items[0], hr.SelectedTab);
        Assert.IsType<AttendanceSectionViewModel>(hr.SelectedTab);
        Assert.True(attendance.IsOpen);
        Assert.Equal(1, hr.Groups.Count(g => g.IsOpen));

        // مجموعة أخرى: تُفتح هي وتُغلق السابقة
        var payroll = hr.Groups.Single(g => g.Name == "الرواتب");
        hr.OpenGroupCommand.Execute(payroll);
        await hr.LastActivation;
        Assert.True(payroll.IsOpen);
        Assert.False(attendance.IsOpen);
        Assert.Same(payroll.Items[0], hr.SelectedTab);

        // الضغط على مجموعة مفتوحة يطويها فقط، والشاشة الظاهرة تبقى
        var shown = hr.SelectedTab;
        hr.OpenGroupCommand.Execute(payroll);
        Assert.False(payroll.IsOpen);
        Assert.Same(shown, hr.SelectedTab);

        // الإنتاج: التصنيع ثم المختبر منفصل بمجموعته
        var prod = shell.Open<ProductionModuleViewModel>(ModuleCode.Production);
        Assert.Equal(new[] { "التصنيع", "المختبر", "المتابعة", "الإعداد" }, prod.Groups.Select(g => g.Name));
        Assert.Contains(prod.Qc, prod.Groups.Single(g => g.Name == "المختبر").Items);
        Assert.Contains(prod.Packing, prod.Groups.Single(g => g.Name == "التصنيع").Items);

        // المخازن: مساحات كل مخزن وسيارات المندوبين في مجموعة واحدة
        var wh = shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
        await shell.IdleAllAsync();                              // مساحات المخازن تُبنى من البيانات في الخلفية
        var spaces = wh.Groups.Single(g => g.Name == "مساحات المخازن");
        Assert.Contains(wh.Vans, spaces.Items);
        Assert.Contains(spaces.Items, s => s is WarehouseWorkspaceSectionViewModel);
        await shell.IdleAllAsync();
        Assert.Empty(_f.Unhandled);
    }
}
