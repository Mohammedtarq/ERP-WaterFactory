using System.Collections.ObjectModel;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;

namespace ERP.Presentation.ViewModels.Shell;

/// <summary>
/// قسم فرعي داخل وحدة (تبويب). يُحمَّل عند أول فتح فقط، ويمكن تحديثه يدويًا.
/// </summary>
public abstract class SectionViewModel : SessionViewModel
{
    private bool _loaded;

    protected SectionViewModel(AppSession session, IDialogService dialogs, string moduleCode,
                               string title, string glyph, string color, string description)
        : base(session, dialogs, moduleCode)
    {
        Title = title;
        Glyph = glyph;
        Color = color;
        Description = description;
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
    }

    public string Title { get; }
    public string Glyph { get; }
    public string Color { get; }
    public string Description { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>الأقسام التي تعرض أرصدة/كشوفًا تتغير من شاشات أخرى تُحدَّث عند كل فتح.</summary>
    protected virtual bool ReloadOnActivate => false;

    public async Task ActivateAsync()
    {
        if (_loaded && !ReloadOnActivate) return;
        _loaded = true;
        await LoadAsync();
    }

    public abstract Task LoadAsync();
}

/// <summary>تبويب "الرئيسية" داخل كل وحدة: بطاقات ملوّنة تفتح الأقسام الفرعية.</summary>
public class HomeSectionViewModel : ObservableObject
{
    public HomeSectionViewModel(ModuleViewModel owner)
    {
        Owner = owner;
        OpenSectionCommand = new RelayCommand(p => { if (p is SectionViewModel s) owner.SelectedTab = s; });
    }

    public ModuleViewModel Owner { get; }
    public string Title => "الرئيسية";
    public string Glyph => Icons.Home;
    public string Color => Owner.Color;
    public IEnumerable<SectionViewModel> Sections => Owner.Tabs.OfType<SectionViewModel>();
    public RelayCommand OpenSectionCommand { get; }
}

public abstract class ModuleViewModel : ViewModelBase
{
    private object? _selectedTab;

    protected ModuleViewModel(string title, string glyph, string color)
    {
        Title = title;
        Glyph = glyph;
        Color = color;
        Home = new HomeSectionViewModel(this);
        Tabs.Add(Home);
        _selectedTab = Home;
    }

    public string Title { get; }
    public string Glyph { get; }
    public string Color { get; }
    public HomeSectionViewModel Home { get; }

    /// <summary>مهمة تحميل آخر تبويب فُتح (تنتظرها الاختبارات).</summary>
    public Task LastActivation { get; private set; } = Task.CompletedTask;

    /// <summary>أول عنصر دائمًا الرئيسية، ثم الأقسام الفرعية.</summary>
    public ObservableCollection<object> Tabs { get; } = new();

    public object? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetProperty(ref _selectedTab, value) && value is SectionViewModel s)
            {
                LastActivation = s.ActivateAsync();
                Background(LastActivation);
            }
        }
    }

    protected T Add<T>(T section) where T : SectionViewModel
    {
        Tabs.Add(section);
        return section;
    }

    public T Section<T>() where T : SectionViewModel => Tabs.OfType<T>().Single();
}

/// <summary>وحدة لم تُبنَ بعد (الموارد البشرية، المندوبون، الإنتاج).</summary>
public class PlaceholderModuleViewModel : ViewModelBase
{
    public PlaceholderModuleViewModel(string title, string glyph, string color)
    {
        Title = title;
        Glyph = glyph;
        Color = color;
    }

    public string Title { get; }
    public string Glyph { get; }
    public string Color { get; }
    public string Message => $"وحدة \"{Title}\" لم تُبنَ بعد — قاعدة بياناتها جاهزة، وواجهتها في المرحلة القادمة.";
}
