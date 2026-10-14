using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.HR;
using ERP.Presentation.ViewModels.Settings;
using ERP.Presentation.ViewModels.Shell;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// الواجهة الجديدة: ستة مظاهر لكل مستخدم (تُطبَّق فورًا وتتبعه)، و«مفضلتي» بالنجمة، والبحث السريع (Ctrl+K) عن أي شاشة،
/// وشاشة الدخول بالمستخدمين الأخيرين وصورهم ومظهر آخر من دخل.
/// </summary>
[Collection("app")]
public class NewUiTests
{
    private readonly AppFixture _f;
    public NewUiTests(AppFixture f) => _f = f;

    private sealed class FakeThemeHost : IThemeHost
    {
        public List<string> Applied { get; } = new();
        public void Apply(ThemeInfo theme) => Applied.Add(theme.Key);
    }

    private sealed class MemoryRecent : IRecentUsersStore
    {
        public List<RecentUser> Items { get; private set; } = new();
        public List<RecentUser> Load() => Items.ToList();
        public void Remember(RecentUser user) => Items = RecentUsers.Merge(Items, user);
        public void Forget(string username) => Items.RemoveAll(u => u.Username == username);
    }

    [Fact]
    public async Task Themes_favorites_quick_search_and_recent_users()
    {
        Assert.Equal(new[] { "الرحمة", "أزرق الماء", "فيروزي", "رصاصي", "زجاجي", "زيتوني" }, Themes.All.Select(t => t.Name));
        Assert.Equal("rahma", Themes.Get("unknown").Key);
        Assert.True(Themes.Get("aurora").Glass);

        var host = new FakeThemeHost();
        var recent = new MemoryRecent();
        ThemeHost.Current = host;
        RecentUsers.Store = recent;
        try
        {
            var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
            await shell.IdleAsync();
            Assert.Equal("rahma", host.Applied.Last());                       // الافتراضي

            // ---- المظهر لكل مستخدم: يُحفظ ويُطبَّق فورًا، ويعود معه عند الدخول ----
            await shell.SetThemeCommand.ExecuteAsync(Themes.Get("teal"));
            Assert.Equal(("teal", "teal"), (shell.ThemeKey, host.Applied.Last()));
            await using (var db = shell.Session.NewDb())
                Assert.Equal("teal", (await new UserPreferencesService(db).GetAsync(shell.Session.UserId)).Theme);

            // ---- «مفضلتي»: النجمة على الشاشة الظاهرة، وتظهر أعلى القائمة وتفتحها ----
            var hr = shell.Open<HrModuleViewModel>(ModuleCode.HR);
            await hr.IdleAsync();
            var reports = hr.Section<HrReportsSectionViewModel>();
            hr.SelectedTab = reports;
            await shell.ToggleFavoriteCommand.ExecuteAsync(null);              // الشاشة الظاهرة
            Assert.True(reports.IsFavorite);
            var fav = Assert.Single(shell.Favorites);
            Assert.Equal(("HrReports", "تقارير الموارد البشرية", "الموارد البشرية"), (fav.Key, fav.Title, fav.ModuleTitle));
            shell.Open<ERP.Presentation.ViewModels.Sales.SalesModuleViewModel>(ModuleCode.Sales);
            Assert.Same(reports, shell.OpenEntry(fav));
            Assert.Same(hr, shell.CurrentModule);
            await shell.ToggleFavoriteCommand.ExecuteAsync(reports);
            Assert.False(reports.IsFavorite);
            Assert.False(shell.HasFavorites);

            // ---- البحث السريع: بالاسم ولو بهمزة أو تاء مربوطة مختلفة، ثم Enter يفتح الأول ----
            shell.OpenSearchCommand.Execute(null);
            Assert.True(shell.IsSearchOpen);
            shell.SearchText = "تقارير المبيعات";
            Assert.Equal("SalesReports", shell.SearchResults.First().Key);
            shell.SearchText = "مظهر";
            Assert.Contains(shell.SearchResults, r => r.Key == "Appearance");
            shell.SearchText = "الرواتب";
            Assert.Equal("Payroll", shell.SearchResults.First().Key);
            shell.OpenFirstResultCommand.Execute(null);
            Assert.False(shell.IsSearchOpen);
            Assert.Equal("Payroll", SectionCatalog.KeyOf((SectionViewModel)((ModuleViewModel)shell.CurrentModule!).SelectedTab!));

            // ---- قسم «المظهر» في الإعدادات ----
            var settings = shell.Open<SettingsModuleViewModel>(ModuleCode.SystemSettings);
            await settings.IdleAsync();
            var appearance = settings.Section<AppearanceSectionViewModel>();
            settings.SelectedTab = appearance;
            await settings.IdleAsync();
            await appearance.IdleAsync();
            Assert.Equal("teal", appearance.Selected.Key);
            appearance.Selected = appearance.Options.Single(t => t.Key == "olive");
            await appearance.ApplyCommand.ExecuteAsync();
            Assert.Equal("olive", host.Applied.Last());

            // ---- شاشة الدخول: المستخدم الأخير بصورته ووظيفته ومظهره ----
            var me = Assert.Single(recent.Items);
            Assert.Equal((AppFixture.AdminUser, shell.Session.FullName), (me.Username, me.FullName));
            var login = new LoginViewModel(new AuthService(_f.ControlConnection), dialogs, new RecordingNavigator());
            Assert.True(login.HasRecent);
            Assert.Equal("olive", host.Applied.Last());                       // آخر مظهر اختاره (من الإعدادات)
            login.PickUserCommand.Execute(login.Recent[0]);
            Assert.Equal(AppFixture.AdminUser, login.Username);
            login.OtherUserCommand.Execute(null);
            Assert.Equal("", login.Username);
            Assert.Equal(new[] { "b", "a" }, RecentUsers.Merge(new[] { new RecentUser("a", "A", null, null, null, null, DateTime.UtcNow) },
                                                                new RecentUser("b", "B", null, null, null, null, DateTime.UtcNow)).Select(u => u.Username));

            // ---- «الواجهة الجديدة»: مفتاح لكل مستخدم، والوحدة تفتح لوحة مربعات أقسامها ----
            Assert.True(shell.UseNewUi);                                       // الجديدة افتراضيًا (51_new_ui_default.sql)
            await shell.ToggleNewUiCommand.ExecuteAsync();
            Assert.False(shell.UseNewUi);
            await using (var db = shell.Session.NewDb())
                Assert.False(await new UserPreferencesService(db).UsesNewUiAsync(shell.Session.UserId));
            await shell.ToggleNewUiCommand.ExecuteAsync();
            Assert.True(shell.UseNewUi);
            var hrNav = shell.NavItems.Single(n => n.ModuleCode == ModuleCode.HR);
            shell.NavigateCommand.Execute(shell.NavItems.Single(n => n.ModuleCode == ModuleCode.Sales));
            shell.NavigateCommand.Execute(hrNav);
            Assert.True(shell.IsFlyoutOpen);
            Assert.Equal(new[] { "الموظفون", "الحضور والإجازات", "الحوافز", "الرواتب", "التحليل", "التقارير" }, shell.FlyoutGroups.Select(g => g.Name));
            Assert.Equal(2, shell.FlyoutColumns.Count);                         // عمودان بلا شريط تمرير
            Assert.Equal(shell.FlyoutGroups.Count, shell.FlyoutColumns.Sum(c => c.Groups.Count));
            shell.FlyoutFilter = "تقارير";
            var tile = Assert.Single(Assert.Single(shell.FlyoutGroups).Items);
            Assert.Equal("تقارير الموارد البشرية", ((SectionViewModel)tile).Title);
            shell.OpenTileCommand.Execute(tile);
            Assert.False(shell.IsFlyoutOpen);
            Assert.Same(tile, hr.SelectedTab);
            shell.NavigateCommand.Execute(hrNav);
            Assert.Equal("", shell.FlyoutFilter);                              // اللوحة تُفتح نظيفة
            shell.OpenTileCommand.Execute(shell.FlyoutGroups.Single(g => g.Name == "التحليل").Items[0]);   // لوحة الوحدة
            Assert.IsType<HomeSectionViewModel>(hr.SelectedTab);
            await shell.ToggleNewUiCommand.ExecuteAsync();
            Assert.False(shell.UseNewUi);
            shell.NavigateCommand.Execute(shell.NavItems.Single(n => n.ModuleCode == ModuleCode.Sales));
            Assert.False(shell.IsFlyoutOpen);
            await shell.ToggleNewUiCommand.ExecuteAsync();                      // إعادة الافتراضي لبقية الاختبارات

            // إعادة المظهر الافتراضي لبقية الاختبارات
            await shell.SetThemeCommand.ExecuteAsync(Themes.Get("rahma"));
            Assert.Empty(_f.Unhandled);
        }
        finally
        {
            ThemeHost.Current = null;
            RecentUsers.Store = null;
        }
    }
}
