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
        RefreshCommand = new AsyncRelayCommand(LoadCoalescedAsync);
    }

    private Task? _inflight;

    /// <summary>تحميل واحد في الوقت نفسه للشاشة: الفتح التلقائي وزر التحديث يشتركان فيه بدل تحميلين متداخلين.</summary>
    public Task LoadCoalescedAsync()
    {
        if (_inflight is { IsCompleted: false }) return _inflight;
        return _inflight = LoadAsync();
    }

    public string Title { get; }
    public string Glyph { get; }
    public string Color { get; }
    public string Description { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    /// <summary>اسم الوحدة الأصلية إن كانت الشاشة منقولة بقرار الإدارة.</summary>
    public string? MovedFrom { get; internal set; }

    private long _loadedVersion = -1;

    /// <summary>الأقسام التي تعرض أرصدة/كشوفًا تتغير من شاشات أخرى تُحدَّث عند كل فتح.</summary>
    protected virtual bool ReloadOnActivate => false;

    /// <summary>
    /// نموذج إدخال قيد التعبئة (فاتورة فيها سطور، محرر مفتوح...): لا يُعاد تحميل القوائم تحته حتى لا يضيع الاختيار؛
    /// يُحدَّث بعد الحفظ.
    /// </summary>
    protected virtual bool HasPendingInput => false;

    /// <summary>
    /// يُحمَّل عند أول فتح، ثم يُحدَّث تلقائيًا عند الفتح إن حُفظت أي عملية في أي شاشة منذ آخر تحميل
    /// (ودائمًا للأقسام التي تطلب ذلك) — بلا زر تحديث وبلا استعلامات زائدة إن لم يتغير شيء.
    /// </summary>
    private Task? _activation;

    /// <summary>تفعيلان متزامنان (نقرات سريعة بين الشاشات) يشتركان في تحميل واحد بدل تحميلين يكرران الصفوف.</summary>
    public Task ActivateAsync()
    {
        if (_activation is { IsCompleted: false }) return _activation;
        return _activation = ActivateCoreAsync();
    }

    private DateTime _loadedAt;

    /// <summary>
    /// شاشات الأرصدة والكشوف تُحدَّث عند الفتح حتى بلا تغيير من هذا الجهاز (لترى عمليات المستخدمين الآخرين)،
    /// لكن ليس أكثر من مرة كل هذه المدة عند التنقل السريع بين التبويبات.
    /// </summary>
    public static TimeSpan IdleRefreshInterval { get; set; } = TimeSpan.FromSeconds(20);

    private async Task ActivateCoreAsync()
    {
        var changed = _loadedVersion != Session.DataVersion;
        if (_loaded && !changed && (!ReloadOnActivate || DateTime.UtcNow - _loadedAt < IdleRefreshInterval)) return;
        if (_loaded && changed && HasPendingInput && !ReloadOnActivate) return;
        _loaded = true;
        _loadedVersion = Session.DataVersion;
        _loadedAt = DateTime.UtcNow;
        await LoadCoalescedAsync();
        _loadedVersion = Session.DataVersion;
    }

    public abstract Task LoadAsync();

    /// <summary>طباعة مستند محفوظ: يُبنى من قاعدة البيانات ثم يُعرض للمعاينة والطباعة.</summary>
    protected async Task PrintAsync(Func<Data.ProjectDb.ProjectDbContext, Task<ReportDocument?>> build)
    {
        await using var db = Session.NewDb();
        var report = await build(db);
        if (report is null) { Dialogs.Error("المستند غير موجود"); return; }
        Dialogs.ShowReport(report);
    }
}

/// <summary>تبويب "الرئيسية" داخل كل وحدة: بطاقات ملوّنة تفتح الأقسام الفرعية.</summary>
public class HomeSectionViewModel : ObservableObject
{
    public HomeSectionViewModel(ModuleViewModel owner)
    {
        Owner = owner;
        OpenSectionCommand = new RelayCommand(p => { if (p is SectionViewModel s) owner.SelectedTab = s; });
        _showSections = Services.UiPreferences.Get(PreferenceKey, true);
        ToggleSectionsCommand = new RelayCommand(() => ShowSections = !ShowSections);
    }

    private bool _showSections;
    private string PreferenceKey => $"home.sections.{Owner.Title}";

    /// <summary>إظهار/إخفاء شبكة "القوائم الفرعية" في رئيسية الوحدة — يُحفظ لكل وحدة على هذا الجهاز.</summary>
    public bool ShowSections
    {
        get => _showSections;
        set
        {
            if (!SetProperty(ref _showSections, value)) return;
            Services.UiPreferences.Set(PreferenceKey, value);
            OnPropertyChanged(nameof(ToggleSectionsText));
        }
    }
    public string ToggleSectionsText => ShowSections ? "إخفاء القوائم الفرعية" : "إظهار القوائم الفرعية";
    public RelayCommand ToggleSectionsCommand { get; }

    public ModuleViewModel Owner { get; }
    public string Title => "الرئيسية";
    public string Glyph => Icons.Home;
    public string Color => Owner.Color;
    public IEnumerable<SectionViewModel> Sections => Owner.Tabs.OfType<SectionViewModel>();
    public ModuleDashboardViewModel? Dashboard => Owner.Dashboard;
    public bool HasDashboard => Owner.Dashboard is not null;
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
            if (!SetProperty(ref _selectedTab, value)) return;
            if (value is SectionViewModel s)
            {
                LastActivation = s.ActivateAsync();
                Background(LastActivation);
            }
            else if (value is HomeSectionViewModel && Dashboard is { } d)
            {
                // العودة للرئيسية بعد أي عملية تحدّث مؤشرات اللوحة ورسومها
                LastActivation = d.RefreshIfChangedAsync();
                Background(LastActivation);
            }
        }
    }

    /// <summary>
    /// يضيف القسم إلا إن أخفته الإدارة عن دور المستخدم أو نقلته إلى وحدة أخرى
    /// (يبقى الكائن متاحًا للروابط الداخلية، ولا يظهر تبويبًا).
    /// </summary>
    protected T Add<T>(T section) where T : SectionViewModel
    {
        var layout = section.SessionRef.Layout;
        var key = SectionCatalog.KeyOf(section);
        var movedAway = layout.Moves.TryGetValue(key, out var target) && target != section.HomeModule && SectionCatalog.IsMovable(section.GetType(), section.HomeModule);
        var hidden = layout.Hidden.Contains(key) && SectionCatalog.IsConfigurable(section.HomeModule);
        if (!movedAway && !hidden) Tabs.Add(section);
        return section;
    }

    /// <summary>يُدرج الأقسام التي نقلتها الإدارة إلى هذه الوحدة، وصلاحياتها تُحسب على هذه الوحدة.</summary>
    internal void AdoptMovedSections(AppSession session, IDialogService dialogs, string moduleCode)
    {
        foreach (var (key, target) in session.Layout.Moves)
        {
            if (target != moduleCode || session.Layout.Hidden.Contains(key)) continue;
            if (SectionCatalog.Create(key, session, dialogs) is not { } section || !SectionCatalog.IsMovable(section.GetType(), section.HomeModule)) continue;
            section.Module = moduleCode;
            section.MovedFrom = SectionCatalog.ModuleTitle(section.HomeModule);
            Tabs.Add(section);
        }
    }

    /// <summary>يعيد تفعيل التبويب الظاهر (يُحدَّث فقط إن تغيّرت البيانات منذ آخر تحميل).</summary>
    public void Reactivate()
    {
        if (SelectedTab is SectionViewModel s) LastActivation = s.ActivateAsync();
        else if (Dashboard is { } d) LastActivation = d.RefreshIfChangedAsync();
        else return;
        Background(LastActivation);
    }

    /// <summary>لوحة القسم (مؤشرات ورسوم النشاط اليومي) أعلى "الرئيسية".</summary>
    public ModuleDashboardViewModel? Dashboard { get; private set; }

    protected void UseDashboard(AppSession session, IDialogService dialogs, string moduleCode,
                                Func<Data.ProjectDb.ProjectDbContext, ModuleDashboardViewModel, Task> loader)
    {
        Dashboard = new ModuleDashboardViewModel(session, dialogs, moduleCode, loader);
        Background(Dashboard.LoadAsync());
    }

    /// <summary>أقسام تُبنى من البيانات (مثل تبويب لكل مخزن) تُدرج بعد "الرئيسية" مباشرة.</summary>
    protected void InsertSection(int index, SectionViewModel section) => Tabs.Insert(Math.Min(index, Tabs.Count), section);

    protected void RemoveSection(SectionViewModel section)
    {
        if (ReferenceEquals(SelectedTab, section)) SelectedTab = Home;
        Tabs.Remove(section);
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
