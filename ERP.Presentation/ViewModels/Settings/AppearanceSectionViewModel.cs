using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;

namespace ERP.Presentation.ViewModels.Settings;

/// <summary>
/// «المظهر»: كل مستخدم يختار مظهره من ستة (الرحمة، أزرق الماء، فيروزي، رصاصي، زجاجي، زيتوني)، ويتبعه على أي جهاز.
/// يُطبَّق فورًا. ألوان أيقونات الأقسام ثابتة في كل المظاهر. (متاح أيضًا من قائمة المستخدم أسفل الشريط الجانبي.)
/// </summary>
public class AppearanceSectionViewModel : SectionViewModel
{
    private ThemeInfo _selected = Themes.Get(null);

    public AppearanceSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.SystemSettings, "حسابي والمظهر", Icons.Star, "#2B8FD6", "صورتك، والواجهة، والخروج، ومظهرك من ستة ألوان")
    {
        ApplyCommand = new AsyncRelayCommand(ApplyAsync);
    }

    public ObservableCollection<ThemeInfo> Options { get; } = new(Themes.All);
    public ThemeInfo Selected { get => _selected; set => SetProperty(ref _selected, value); }
    public AsyncRelayCommand ApplyCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        Selected = Themes.Get((await new UserPreferencesService(db).GetAsync(Session.UserId)).Theme);
    }

    private async Task ApplyAsync()
    {
        await using var db = Session.NewDb();
        await new UserPreferencesService(db).SetThemeAsync(Session.UserId, Selected.Key);
        ThemeHost.Apply(Selected);
        RecentUsers.UpdateTheme(Session.GlobalUsername, Selected.Key);
        StatusMessage = $"طُبّق مظهر «{Selected.Name}»";
    }
}
