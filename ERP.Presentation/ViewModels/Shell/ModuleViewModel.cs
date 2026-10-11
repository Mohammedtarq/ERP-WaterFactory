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
        ClearScreenCommand = new AsyncRelayCommand(ClearAsync);
    }

    private Task? _inflight;

    /// <summary>«تفريغ»: يعيد الشاشة كما فُتحت أول مرة (يُلغى ما أُدخل ولم يُحفظ).</summary>
    public AsyncRelayCommand ClearScreenCommand { get; }

    /// <summary>
    /// ما أُدخل في الشاشة ولم يُحفظ (العميل المختار، السطور، المبلغ…) يُفرَّغ هنا. تُعيده كل شاشة إدخال إلى حالتها الأولى؛
    /// يُستدعى عند الخروج من الشاشة دون حفظ (قرار المدير: تعود نظيفة تلقائيًا) وعند «تفريغ».
    /// </summary>
    protected virtual void ResetInput() { }

    private bool _forceReload;

    /// <summary>الخروج من الشاشة (إلى تبويب أو وحدة أخرى): يُفرَّغ ما لم يُحفظ، وتُحمَّل من جديد عند العودة.</summary>
    internal void Leave()
    {
        ResetInput();
        _forceReload = _loaded;
    }

    private async Task ClearAsync()
    {
        ResetInput();
        StatusMessage = null;
        await LoadCoalescedAsync();
    }

    /// <summary>تحميل واحد في الوقت نفسه للشاشة: الفتح التلقائي وزر التحديث يشتركان فيه بدل تحميلين متداخلين.</summary>
    public Task LoadCoalescedAsync()
    {
        if (_inflight is { IsCompleted: false }) return _inflight;
        return _inflight = LoadAsync();
    }

    private bool _isCurrent;
    /// <summary>القسم الظاهر الآن في وحدته (لتظليله في القائمة الجانبية).</summary>
    public bool IsCurrent { get => _isCurrent; internal set => SetProperty(ref _isCurrent, value); }

    private string _groupColor = "#64748B";
    /// <summary>لون مجموعة القسم في القائمة الجانبية (يُظلَّل به عند اختياره).</summary>
    public string GroupColor { get => _groupColor; internal set => SetProperty(ref _groupColor, value); }

    public string Title { get; }
    /// <summary>رقم الخطوة في تسلسل العمل (مثل الإنتاج ← الفحص ← التعبئة) — يظهر قبل العنوان.</summary>
    public int? Step { get; internal set; }
    public string DisplayTitle => Step is int n ? $"{n}. {Title}" : Title;
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
        if (_forceReload) _forceReload = false;   // خرج المستخدم وفيها إدخال لم يُحفظ: تُحمَّل نظيفة
        else
        {
            if (_loaded && !changed && (!ReloadOnActivate || DateTime.UtcNow - _loadedAt < IdleRefreshInterval)) return;
            if (_loaded && changed && HasPendingInput && !ReloadOnActivate) return;
        }
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

    private bool _isCurrent;
    public bool IsCurrent { get => _isCurrent; internal set => SetProperty(ref _isCurrent, value); }

    public ModuleViewModel Owner { get; }
    public string Title => "الرئيسية";
    public string DisplayTitle => Title;
    public string Glyph => Icons.Home;
    public string Color => Owner.Color;
    public IEnumerable<SectionViewModel> Sections => Owner.Tabs.OfType<SectionViewModel>();
    /// <summary>بطاقات الأقسام مقسّمة بنفس مجموعات القائمة الجانبية وألوانها.</summary>
    public ObservableCollection<SectionGroup> Groups => Owner.Groups;
    public ModuleDashboardViewModel? Dashboard => Owner.Dashboard;
    public bool HasDashboard => Owner.Dashboard is not null;
    public RelayCommand OpenSectionCommand { get; }
}

public abstract class ModuleViewModel : ViewModelBase
{
    private object? _selectedTab;
    private string? _moduleCode;

    protected ModuleViewModel(string title, string glyph, string color)
    {
        Tabs.CollectionChanged += (_, _) => RebuildGroups();
        OpenGroupCommand = new RelayCommand(p => { if (p is SectionGroup g) OpenGroup(g); });
        SelectTabCommand = new RelayCommand(p => { if (p is not null) SelectedTab = p; });
        GoHomeCommand = new RelayCommand(() => SelectedTab = Home);
        Title = title;
        Glyph = glyph;
        Color = color;
        Home = new HomeSectionViewModel(this);
        Tabs.Add(Home);
        _selectedTab = Home;
        Home.IsCurrent = true;
        ClearCurrentCommand = new AsyncRelayCommand(() => SelectedTab is SectionViewModel s ? s.ClearScreenCommand.ExecuteAsync() : Task.CompletedTask);
    }

    /// <summary>زر «تفريغ» أعلى الوحدة: يعيد الشاشة الظاهرة نظيفة.</summary>
    public AsyncRelayCommand ClearCurrentCommand { get; }
    public bool CanClearCurrent => SelectedTab is SectionViewModel;

    public string Title { get; }
    public string Glyph { get; }
    public string Color { get; }
    public HomeSectionViewModel Home { get; }

    /// <summary>مهمة تحميل آخر تبويب فُتح (تنتظرها الاختبارات).</summary>
    public Task LastActivation { get; private set; } = Task.CompletedTask;

    /// <summary>أول عنصر دائمًا الرئيسية، ثم الأقسام الفرعية.</summary>
    public ObservableCollection<object> Tabs { get; } = new();

    // ---------------- التنقل الجانبي: مجموعات الأقسام ----------------
    /// <summary>أقسام الوحدة موزعة على مجموعات ملوّنة (تُعاد بنايتها كلما تغيّرت التبويبات).</summary>
    public ObservableCollection<SectionGroup> Groups { get; } = new();
    public RelayCommand OpenGroupCommand { get; }
    public RelayCommand SelectTabCommand { get; }
    public RelayCommand GoHomeCommand { get; }
    public string? ModuleCodeValue => _moduleCode;

    /// <summary>مسار القسم الظاهر: «الحوافز ‹ حافز المندوب» (أو «الرئيسية»).</summary>
    public string Breadcrumb => SelectedTab is SectionViewModel s
        ? (Groups.FirstOrDefault(x => x.Items.Contains(s))?.Name is { } groupName ? $"{groupName} ‹ {s.DisplayTitle}" : s.DisplayTitle)
        : "الرئيسية";

    protected void UseGroups(string moduleCode)
    {
        _moduleCode = moduleCode;
        RebuildGroups();
    }

    private void RebuildGroups()
    {
        var open = Groups.Where(g => g.IsOpen).Select(g => g.Name).ToHashSet();
        var sections = Tabs.ToArray().OfType<SectionViewModel>().ToList();
        var placed = new HashSet<SectionViewModel>();
        var built = new List<SectionGroup>();
        foreach (var def in SectionGrouping.For(_moduleCode))
        {
            var group = new SectionGroup(def.Name, def.Color);
            foreach (var key in def.Keys)
                foreach (var s in sections.Where(s => SectionCatalog.KeyOf(s) == key && placed.Add(s))) group.Items.Add(s);
            if (group.Count > 0) built.Add(group);
        }
        foreach (var g in built) foreach (var s in g.Items) s.GroupColor = g.Color;
        var others = sections.Where(s => !placed.Contains(s)).ToList();
        if (others.Count > 0 && _moduleCode is not null)
        {
            var group = new SectionGroup(SectionGrouping.OtherName, SectionGrouping.OtherColor);
            foreach (var s in others) group.Items.Add(s);
            built.Add(group);
        }
        // وحدة بلا توزيع: كل أقسامها مجموعة واحدة حتى لا تُحجب
        if (_moduleCode is null && sections.Count > 0)
        {
            var group = new SectionGroup("الأقسام", Color);
            foreach (var s in sections) group.Items.Add(s);
            built.Add(group);
        }
        foreach (var s in others) s.GroupColor = SectionGrouping.OtherColor;
        Groups.Clear();
        foreach (var g in built) { g.IsOpen = open.Contains(g.Name); Groups.Add(g); }
        ApplySelection();
    }

    /// <summary>المجموعة التي فيها القسم الظاهر تُفتح، وتُغلق البقية (الرئيسية تغلق الكل).</summary>
    private void ApplySelection()
    {
        foreach (var g in Groups)
            g.IsOpen = SelectedTab is SectionViewModel s ? g.Items.Contains(s) : false;
        foreach (var s in Tabs.OfType<SectionViewModel>()) s.IsCurrent = ReferenceEquals(s, SelectedTab);
        Home.IsCurrent = ReferenceEquals(Home, SelectedTab);
        OnPropertyChanged(nameof(Breadcrumb));
    }

    /// <summary>ضغطة على مجموعة: تُطوى إن كانت مفتوحة، وإلا تُفتح وحدها ويظهر أول أقسامها.</summary>
    private void OpenGroup(SectionGroup group)
    {
        if (group.IsOpen) { group.IsOpen = false; return; }
        foreach (var g in Groups) g.IsOpen = ReferenceEquals(g, group);
        if (group.Items.Count > 0 && (SelectedTab is not SectionViewModel cur || !group.Items.Contains(cur)))
            SelectedTab = group.Items[0];
    }

    public object? SelectedTab
    {
        get => _selectedTab;
        set
        {
            var previous = _selectedTab;
            if (!SetProperty(ref _selectedTab, value)) return;
            if (previous is SectionViewModel left) left.Leave();
            OnPropertyChanged(nameof(CanClearCurrent));
            ApplySelection();
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

    /// <summary>إضافة شاشة برقم خطوتها في تسلسل العمل.</summary>
    protected T AddStep<T>(int step, T section) where T : SectionViewModel
    {
        section.Step = step;
        return Add(section);
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

    /// <summary>الخروج من الوحدة كلها (إلى وحدة أخرى): الشاشة الظاهرة تُفرَّغ مما لم يُحفظ.</summary>
    public void LeaveCurrent()
    {
        if (SelectedTab is SectionViewModel s) s.Leave();
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
        UseGroups(moduleCode);
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

    // نسخة ثابتة من القائمة: أقسام تُبنى في الخلفية (مساحات المخازن) قد تُضاف أثناء القراءة
    public T Section<T>() where T : SectionViewModel => Tabs.ToArray().OfType<T>().Single();
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
