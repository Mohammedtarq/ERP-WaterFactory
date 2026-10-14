using System.Collections.ObjectModel;
using Microsoft.EntityFrameworkCore;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Finance;
using ERP.Presentation.ViewModels.HR;
using ERP.Presentation.ViewModels.Production;
using ERP.Presentation.ViewModels.Reps;
using ERP.Presentation.ViewModels.Sales;
using ERP.Presentation.ViewModels.Settings;
using ERP.Presentation.ViewModels.Suppliers;
using ERP.Presentation.ViewModels.Warehouse;

namespace ERP.Presentation.ViewModels.Shell;

public class NavItem : ObservableObject
{
    private bool _isSelected;

    public NavItem(string moduleCode, string title, string glyph, string color, Func<object> factory)
    {
        ModuleCode = moduleCode;
        Title = title;
        Glyph = glyph;
        Color = color;
        Factory = factory;
    }

    public string ModuleCode { get; }
    public string Title { get; }
    public string Glyph { get; }
    public string Color { get; }
    internal Func<object> Factory { get; }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

/// <summary>
/// الواجهة الرئيسية: شريط جانبي بالوحدات التي يملك المستخدم صلاحية عرضها
/// فقط، ومحتوى الوحدة المختارة (يُنشأ عند أول فتح ويُحتفظ به طوال الجلسة).
/// </summary>
public class MainShellViewModel : ViewModelBase
{
    private readonly IDialogService _dialogs;
    private readonly INavigator _navigator;
    private readonly Dictionary<string, object> _modules = new();
    private NavItem? _selectedItem;
    private object? _currentModule;

    public MainShellViewModel(AppSession session, IDialogService dialogs, INavigator navigator)
    {
        Session = session;
        _dialogs = dialogs;
        _navigator = navigator;
        // هوية الطباعة (شعار، اسم، اتصال، لون) لكل المستندات المطبوعة في هذه الجلسة
        ReportBranding.Current = ReportBranding.Fallback(session.ProjectName);
        Background(ReportBranding.RefreshAsync(session));

        var all = new[]
        {
            new NavItem(ModuleCode.Dashboard, "لوحة المعلومات", Icons.Dashboard, ModuleColors.Dashboard,
                        () => new DashboardViewModel(session, dialogs)),
            new NavItem(ModuleCode.Warehouse, "المخازن", Icons.Warehouse, ModuleColors.Warehouse,
                        () => new WarehouseModuleViewModel(session, dialogs)),
            new NavItem(ModuleCode.Sales, "المبيعات", Icons.Sales, ModuleColors.Sales,
                        () => new SalesModuleViewModel(session, dialogs)),
            new NavItem(ModuleCode.Suppliers, "الموردون والمشتريات", Icons.Suppliers, ModuleColors.Suppliers,
                        () => new SuppliersModuleViewModel(session, dialogs)),
            new NavItem(ModuleCode.Finance, "المالية", Icons.Finance, ModuleColors.Finance,
                        () => new FinanceModuleViewModel(session, dialogs)),
            new NavItem(ModuleCode.HR, "الموارد البشرية", Icons.HR, ModuleColors.HR,
                        () => new HrModuleViewModel(session, dialogs)),
            new NavItem(ModuleCode.Reps, "المندوبون", Icons.Reps, ModuleColors.Reps,
                        () => new RepsModuleViewModel(session, dialogs)),
            new NavItem(ModuleCode.Production, "الإنتاج", Icons.Production, ModuleColors.Production,
                        () => new ProductionModuleViewModel(session, dialogs)),
            new NavItem(ModuleCode.Lab, "المختبر", Icons.Lab, ModuleColors.Lab,
                        () => new LabModuleViewModel(session, dialogs)),
            new NavItem(ModuleCode.SystemSettings, "إعدادات النظام", Icons.Settings, ModuleColors.Settings,
                        () => new SettingsModuleViewModel(session, dialogs)),
        };

        var visible = all.Where(n => session.Permissions.CanView(n.ModuleCode)).ToList();
        // مستخدم عمله في وحدة واحدة (مثل مسؤول الموارد البشرية): لا حاجة للوحة المعلومات العامة، يفتح على وحدته مباشرة
        if (visible.Count(n => n.ModuleCode != ModuleCode.Dashboard) == 1) visible.RemoveAll(n => n.ModuleCode == ModuleCode.Dashboard);
        NavItems = visible;
        NavigateCommand = new RelayCommand(p =>
        {
            if (p is not NavItem n) return;
            SelectedItem = n;
            // الواجهة الجديدة: الوحدة تفتح لوحة أقسامها المنبثقة (لوحة المعلومات تُفتح مباشرة)
            if (UseNewUi && CurrentModule is ModuleViewModel) OpenFlyout();
        });
        ToggleNewUiCommand = new AsyncRelayCommand(ToggleNewUiAsync);
        CloseFlyoutCommand = new RelayCommand(() => IsFlyoutOpen = false);
        OpenTileCommand = new RelayCommand(OpenTile);
        LogoutCommand = new RelayCommand(Logout);
        ToggleAlertsCommand = new RelayCommand(() => IsAlertsOpen = !IsAlertsOpen);
        OpenAlertCommand = new RelayCommand(p => { if (p is AlertItem a) OpenAlert(a); });
        ChangeMyPhotoCommand = new AsyncRelayCommand(ChangeMyPhotoAsync);
        RemoveMyPhotoCommand = new AsyncRelayCommand(() => SaveMyPhotoAsync(null));
        session.PhotoChanged += () => Background(RefreshMyPhotoAsync());
        ToggleFavoriteCommand = new AsyncRelayCommand(ToggleFavoriteAsync);
        OpenEntryCommand = new RelayCommand(p => { if (p is QuickEntry e) OpenEntry(e); });
        OpenSearchCommand = new RelayCommand(() => { SearchText = ""; IsSearchOpen = true; });
        CloseSearchCommand = new RelayCommand(() => IsSearchOpen = false);
        OpenFirstResultCommand = new RelayCommand(() => { if (SearchResults.FirstOrDefault() is { } e) OpenEntry(e); });
        SelectedItem = NavItems.FirstOrDefault();
        Background(LoadPreferencesAsync());
        Background(RefreshCloudStatusAsync());
        Background(RefreshAlertsAsync());
        Background(RefreshMyPhotoAsync());
    }

    // ============================ صورة المستخدم ============================

    private byte[]? _myPhoto;
    private bool _hasOwnPhoto;
    /// <summary>صورة المستخدم أسفل القائمة: الخاصة وإلا صورة موظفه المربوط.</summary>
    public byte[]? MyPhoto { get => _myPhoto; private set => SetProperty(ref _myPhoto, value); }
    /// <summary>له صورة خاصة (فيظهر «إزالة صورتي»).</summary>
    public bool HasOwnPhoto { get => _hasOwnPhoto; private set => SetProperty(ref _hasOwnPhoto, value); }
    /// <summary>كل مستخدم يغيّر صورته بنفسه (لا تمس الصلاحيات أو الحسابات) — ويُسجَّل التغيير في سجل التدقيق.</summary>
    public AsyncRelayCommand ChangeMyPhotoCommand { get; }
    public AsyncRelayCommand RemoveMyPhotoCommand { get; }

    public async Task RefreshMyPhotoAsync()
    {
        try
        {
            await using var db = Session.NewDb();
            var photos = new PhotoService(db);
            HasOwnPhoto = await photos.HasOwnUserPhotoAsync(Session.UserId);
            MyPhoto = await photos.GetUserDisplayAsync(Session.UserId);
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException) { }
    }

    private async Task ChangeMyPhotoAsync()
    {
        var bytes = _dialogs.PickPhoto();
        if (bytes is null) return;
        await SaveMyPhotoAsync(bytes);
    }

    private async Task SaveMyPhotoAsync(byte[]? bytes)
    {
        await using (var db = Session.NewDb())
        {
            var result = await new PhotoService(db).SetUserAsync(Session.UserId, bytes, Session.UserId);
            if (!result.Success) { _dialogs.Error(result.ErrorMessage!); return; }
        }
        await RefreshMyPhotoAsync();
    }

    // ============================ المظهر و«مفضلتي» والبحث السريع ============================

    private List<string> _favoriteKeys = new();
    private List<QuickEntry>? _entries;
    private string _searchText = "";
    private bool _isSearchOpen;

    /// <summary>كل الشاشات المسموحة (تُبنى مرة عند أول حاجة).</summary>
    public IReadOnlyList<QuickEntry> Entries => _entries ??= SectionCatalog.QuickEntries(Session, _dialogs);

    /// <summary>«مفضلتي»: أقسام ثبّتها المستخدم بالنجمة، أعلى القائمة الجانبية (تتبعه على أي جهاز).</summary>
    public ObservableCollection<QuickEntry> Favorites { get; } = new();
    public bool HasFavorites => Favorites.Count > 0;
    public AsyncRelayCommand ToggleFavoriteCommand { get; }
    public RelayCommand OpenEntryCommand { get; }

    /// <summary>البحث السريع (Ctrl+K): اكتب جزءًا من اسم الشاشة فتظهر من كل الوحدات.</summary>
    public bool IsSearchOpen { get => _isSearchOpen; set => SetProperty(ref _isSearchOpen, value); }
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            SearchResults.Clear();
            var q = SectionCatalog.Normalize(value.Trim());
            if (q.Length == 0) return;
            foreach (var e in Entries.Where(e => SectionCatalog.Normalize(e.Title).Contains(q) || SectionCatalog.Normalize(e.ModuleTitle).Contains(q)
                                                 || SectionCatalog.Normalize(e.Description).Contains(q))
                                     .OrderBy(e => SectionCatalog.Normalize(e.Title).StartsWith(q) ? 0 : SectionCatalog.Normalize(e.Title).Contains(q) ? 1 : 2)
                                     .Take(12))
                SearchResults.Add(e);
        }
    }
    public ObservableCollection<QuickEntry> SearchResults { get; } = new();
    public RelayCommand OpenSearchCommand { get; }
    public RelayCommand CloseSearchCommand { get; }
    public RelayCommand OpenFirstResultCommand { get; }

    /// <summary>المظاهر الستة (قائمة المستخدم ← المظهر)، والمختار الآن.</summary>
    public IReadOnlyList<ThemeInfo> ThemeOptions => Themes.All;
    private string _themeKey = Themes.Default;
    public string ThemeKey { get => _themeKey; private set => SetProperty(ref _themeKey, value); }
    public AsyncRelayCommand SetThemeCommand => _setTheme ??= new AsyncRelayCommand(async p =>
    {
        if (p is not ThemeInfo t) return;
        await using var db = Session.NewDb();
        await new UserPreferencesService(db).SetThemeAsync(Session.UserId, t.Key);
        ThemeKey = t.Key;
        ThemeHost.Apply(t);
        RecentUsers.UpdateTheme(Session.GlobalUsername, t.Key);
    });
    private AsyncRelayCommand? _setTheme;

    /// <summary>مظهر المستخدم ومفضلته عند الدخول.</summary>
    public async Task LoadPreferencesAsync()
    {
        try
        {
            await using var db = Session.NewDb();
            var (theme, favorites) = await new UserPreferencesService(db).GetAsync(Session.UserId);
            if (ThemeHost.LoginChoice is { } chosen)
            {
                // اختاره في شاشة الدخول: يصبح مظهره
                await new UserPreferencesService(db).SetThemeAsync(Session.UserId, chosen);
                theme = chosen;
                ThemeHost.LoginChoice = null;
            }
            ThemeKey = Themes.Get(theme).Key;
            ThemeHost.Apply(Themes.Get(theme));
            UseNewUi = await new UserPreferencesService(db).UsesNewUiAsync(Session.UserId);
            SetFavorites(favorites);
            await RememberUserAsync();
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException) { }
    }

    /// <summary>يظهر هذا المستخدم بصورته ووظيفته في شاشة الدخول على هذا الجهاز.</summary>
    private async Task RememberUserAsync()
    {
        if (RecentUsers.Store is not { } store) return;
        await RefreshMyPhotoAsync();
        await using var db = Session.NewDb();
        var company = await new CompanyProfileService(db).GetAsync();
        var job = await db.Users.Where(u => u.Id == Session.UserId).Select(u => u.Employee != null ? u.Employee.JobTitle : null).FirstOrDefaultAsync();
        store.Remember(new RecentUser(Session.GlobalUsername, Session.FullName, job ?? Session.RoleName, Session.ProjectName, MyPhoto, ThemeKey, DateTime.UtcNow,
                                      string.IsNullOrWhiteSpace(company?.NameAr) ? null : company.NameAr, company?.Logo));
    }

    private void SetFavorites(List<string> keys)
    {
        _favoriteKeys = keys;
        Favorites.Clear();
        foreach (var k in keys)
            if (Entries.FirstOrDefault(e => e.Key == k) is { } e) Favorites.Add(e);
        OnPropertyChanged(nameof(HasFavorites));
        foreach (var m in _modules.Values.OfType<ModuleViewModel>())
            foreach (var t in m.Tabs.ToArray().OfType<SectionViewModel>()) t.IsFavorite = _favoriteKeys.Contains(SectionCatalog.KeyOf(t));
    }

    /// <summary>تثبيت الشاشة في «مفضلتي» أو إزالتها (الشاشة الظاهرة، أو المعطاة).</summary>
    private async Task ToggleFavoriteAsync(object? parameter)
    {
        var key = parameter switch
        {
            SectionViewModel s => SectionCatalog.KeyOf(s),
            QuickEntry e => e.Key,
            string k => k,
            _ => CurrentModuleVm?.SelectedTab is SectionViewModel cur ? SectionCatalog.KeyOf(cur) : null
        };
        if (key is null) return;
        await using var db = Session.NewDb();
        SetFavorites(await new UserPreferencesService(db).ToggleFavoriteAsync(Session.UserId, key));
    }

    /// <summary>فتح شاشة بمفتاحها: في وحدتها الأصلية، وإلا حيث نقلتها الإدارة.</summary>
    public SectionViewModel? OpenEntry(QuickEntry entry)
    {
        IsSearchOpen = false;
        foreach (var nav in NavItems.Where(n => n.ModuleCode != ModuleCode.Dashboard)
                                    .OrderBy(n => n.ModuleCode == entry.HomeModule ? 0 : _modules.ContainsKey(n.ModuleCode) ? 1 : 2))
        {
            SelectedItem = nav;
            if (CurrentModule is ModuleViewModel m && m.Tabs.ToArray().OfType<SectionViewModel>().FirstOrDefault(t => SectionCatalog.KeyOf(t) == entry.Key) is { } section)
            {
                m.SelectedTab = section;
                return section;
            }
        }
        return null;
    }

    // ============================ «الواجهة الجديدة»: اللوحة المنبثقة ============================

    private bool _useNewUi;
    private bool _isFlyoutOpen;
    private string _flyoutFilter = "";

    /// <summary>«الواجهة الجديدة» لهذا المستخدم: الوحدة تفتح لوحة منبثقة بمربعات أقسامها بدل القائمة داخل الشريط.</summary>
    public bool UseNewUi { get => _useNewUi; private set => SetProperty(ref _useNewUi, value); }
    public AsyncRelayCommand ToggleNewUiCommand { get; }
    public bool IsFlyoutOpen { get => _isFlyoutOpen; set => SetProperty(ref _isFlyoutOpen, value); }
    /// <summary>بحث داخل لوحة الوحدة.</summary>
    public string FlyoutFilter { get => _flyoutFilter; set { if (SetProperty(ref _flyoutFilter, value ?? "")) BuildFlyout(); } }
    /// <summary>مجموعات الوحدة بمربعاتها (بعد البحث)، و«نظرة عامة» أولًا.</summary>
    public ObservableCollection<FlyoutGroup> FlyoutGroups { get; } = new();
    public RelayCommand CloseFlyoutCommand { get; }
    public RelayCommand OpenTileCommand { get; }

    private async Task ToggleNewUiAsync()
    {
        await using var db = Session.NewDb();
        await new UserPreferencesService(db).SetNewUiAsync(Session.UserId, !UseNewUi);
        UseNewUi = !UseNewUi;
        IsFlyoutOpen = false;
    }

    public void OpenFlyout()
    {
        _flyoutFilter = "";
        OnPropertyChanged(nameof(FlyoutFilter));
        BuildFlyout();
        IsFlyoutOpen = true;
    }

    private void BuildFlyout()
    {
        FlyoutGroups.Clear();
        if (CurrentModuleVm is not { } m) return;
        var q = SectionCatalog.Normalize(FlyoutFilter.Trim());
        bool Match(SectionViewModel s) => q.Length == 0 || SectionCatalog.Normalize(s.Title).Contains(q) || SectionCatalog.Normalize(s.Description).Contains(q);
        var groups = new List<FlyoutGroup>();
        foreach (var g in m.Groups)
        {
            var items = g.Items.Where(Match).Cast<object>().ToList();
            if (items.Count > 0) groups.Add(new FlyoutGroup(g.Name, g.Color, items));
        }
        // «التحليل» (لوحة الوحدة: مؤشرات ورسوم) مجموعة تُختار، قبل «التقارير»
        if (q.Length == 0 || SectionCatalog.Normalize(m.Home.Title).Contains(q) || SectionCatalog.Normalize(m.Home.Description).Contains(q))
        {
            var analysis = new FlyoutGroup(AnalysisGroup, "#2563EB", new List<object> { m.Home });
            var reports = groups.FindIndex(g => g.Name == "التقارير");
            groups.Insert(reports < 0 ? groups.Count : reports, analysis);
        }
        foreach (var g in groups) FlyoutGroups.Add(g);
        FlyoutColumns = Balance(groups);
    }

    public const string AnalysisGroup = "التحليل";

    /// <summary>عمودا اللوحة: توزيع المجموعات بالترتيب على العمود الأقصر فتتسع بلا شريط تمرير.</summary>
    private static IReadOnlyList<FlyoutColumn> Balance(List<FlyoutGroup> groups)
    {
        var cols = new[] { new FlyoutColumn(new()), new FlyoutColumn(new()) };
        var weight = new int[2];
        foreach (var g in groups)
        {
            var i = weight[0] <= weight[1] ? 0 : 1;
            cols[i].Groups.Add(g);
            weight[i] += 1 + g.Items.Count;
        }
        return cols;
    }

    private IReadOnlyList<FlyoutColumn> _flyoutColumns = Array.Empty<FlyoutColumn>();
    public IReadOnlyList<FlyoutColumn> FlyoutColumns { get => _flyoutColumns; private set => SetProperty(ref _flyoutColumns, value); }

    /// <summary>مربع في اللوحة: القسم (أو «نظرة عامة») يُفتح وتُغلق اللوحة.</summary>
    private void OpenTile(object? p)
    {
        if (CurrentModuleVm is not { } m) return;
        if (p is SectionViewModel s) m.SelectedTab = s;
        else if (p is HomeSectionViewModel) m.GoHomeCommand.Execute(null);
        IsFlyoutOpen = false;
    }

    // ============================ التنبيهات ============================

    private bool _isAlertsOpen;
    /// <summary>تنبيهات المستخدم حسب صلاحياته (طلبات التجهيز، المرتجع، الاستلام، نقص المواد...).</summary>
    public ObservableCollection<AlertItem> Alerts { get; } = new();
    public int AlertsCount => Alerts.Sum(a => a.Count);
    public bool HasAlerts => Alerts.Count > 0;
    public string AlertsText => Alerts.Count == 0 ? "لا تنبيهات" : $"التنبيهات ({Alerts.Count})";
    public string AlertsColor => Alerts.Any(a => a.Level == AlertLevel.Danger) ? "#F87171" : Alerts.Count > 0 ? "#FBBF24" : "#94A3B8";
    public bool IsAlertsOpen { get => _isAlertsOpen; set => SetProperty(ref _isAlertsOpen, value); }
    public RelayCommand ToggleAlertsCommand { get; }
    public RelayCommand OpenAlertCommand { get; }

    private readonly SemaphoreSlim _alertsGate = new(1, 1);

    public async Task RefreshAlertsAsync()
    {
        // تحديث واحد في كل مرة (من التنقل والمؤقّت معًا): لا تتكرر التنبيهات
        await _alertsGate.WaitAsync();
        try
        {
            List<AlertItem> list;
            try
            {
                await using var db = Session.NewDb();
                list = await new AlertsService(db).ForAsync(Session.Permissions);
            }
            catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
            {
                return;   // قاعدة غير متاحة الآن: يبقى آخر ما عُرض
            }
            Alerts.Clear();
            foreach (var a in list) Alerts.Add(a);
            OnPropertyChanged(nameof(AlertsCount));
            OnPropertyChanged(nameof(HasAlerts));
            OnPropertyChanged(nameof(AlertsText));
            OnPropertyChanged(nameof(AlertsColor));
        }
        finally { _alertsGate.Release(); }
    }

    /// <summary>تحديث المؤشرات (الجرس والسحابة): من النافذة كل دقيقة.</summary>
    public void RefreshIndicators()
    {
        Background(RefreshAlertsAsync());
        Background(RefreshCloudStatusAsync());
    }

    /// <summary>فتح الشاشة المعنية بالتنبيه (وإن نُقلت لوحدة أخرى فمن هناك).</summary>
    public SectionViewModel? OpenAlert(AlertItem alert)
    {
        IsAlertsOpen = false;
        foreach (var nav in NavItems.OrderBy(n => n.ModuleCode == alert.ModuleCode ? 0 : 1))
        {
            if (nav.ModuleCode != alert.ModuleCode && !_modules.ContainsKey(nav.ModuleCode)) continue;
            SelectedItem = nav;
            // نسخة من التبويبات: وحدة المخازن قد تُحدّث تبويبات مخازنها في الخلفية لحظة فتحها
            if (CurrentModule is ModuleViewModel m && m.Tabs.ToArray().OfType<SectionViewModel>().FirstOrDefault(t => t.GetType().Name == alert.Section) is { } section)
            {
                m.SelectedTab = section;
                return section;
            }
        }
        _dialogs.Info($"{alert.Title}\n{alert.Detail}");
        return null;
    }

    private string? _cloudStatusText;
    private string _cloudStatusColor = "#94A3B8";

    /// <summary>مؤشر المزامنة السحابية في الشريط الجانبي (يظهر فقط إن فُعّلت).</summary>
    public string? CloudStatusText { get => _cloudStatusText; private set { if (SetProperty(ref _cloudStatusText, value)) OnPropertyChanged(nameof(HasCloudStatus)); } }
    public string CloudStatusColor { get => _cloudStatusColor; private set => SetProperty(ref _cloudStatusColor, value); }
    public bool HasCloudStatus => CloudStatusText is not null;

    public async Task RefreshCloudStatusAsync()
    {
        try
        {
            await using var db = Session.NewDb();
            var (level, text) = CloudSyncStatus.Describe(await new CloudSyncService(db).SettingsAsync(), DateTime.UtcNow);
            CloudStatusColor = level switch { CloudSyncLevel.Ok => "#4ADE80", CloudSyncLevel.Warning => "#FBBF24", _ => "#F87171" };
            CloudStatusText = level == CloudSyncLevel.Off ? null : $"المزامنة: {text}";
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            CloudStatusText = null;   // قاعدة غير متاحة الآن: لا مؤشر بدل رسالة خطأ
        }
    }

    public AppSession Session { get; }
    public string WindowTitle => $"نظام إدارة الأعمال المتكامل — {Session.ProjectName}";
    public string UserLine => $"{Session.FullName} · {Session.RoleName}";
    public IReadOnlyList<NavItem> NavItems { get; }
    public bool HasNoModules => NavItems.Count == 0;

    public NavItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            // الضغط على الوحدة المفتوحة نفسها لا يُخفي أقسامها من القائمة الجانبية
            if (value is null || ReferenceEquals(value, _selectedItem)) return;
            if (_selectedItem is not null) _selectedItem.IsSelected = false;
            var previousModule = CurrentModule;
            SetProperty(ref _selectedItem, value);
            // الخروج من وحدة: الشاشة الظاهرة فيها تعود نظيفة عند الرجوع
            if (previousModule is ModuleViewModel left) left.LeaveCurrent();
            value.IsSelected = true;
            if (!_modules.TryGetValue(value.ModuleCode, out var module))
            {
                _modules[value.ModuleCode] = module = value.Factory();
                if (module is ModuleViewModel created)
                {
                    created.AdoptMovedSections(Session, _dialogs, value.ModuleCode);
                    foreach (var t in created.Tabs.ToArray().OfType<SectionViewModel>()) t.IsFavorite = _favoriteKeys.Contains(SectionCatalog.KeyOf(t));
                }
            }
            else
            {
                // العودة لوحدة مفتوحة سابقًا: التبويب الظاهر يُحدَّث إن حُفظت عمليات في شاشات أخرى
                if (module is ModuleViewModel m) m.Reactivate();
                else if (module is DashboardViewModel dash) Background(dash.RefreshIfChangedAsync());
            }
            CurrentModule = module;
            Background(RefreshCloudStatusAsync());
            Background(RefreshAlertsAsync());
        }
    }

    public object? CurrentModule { get => _currentModule; private set { if (SetProperty(ref _currentModule, value)) OnPropertyChanged(nameof(CurrentModuleVm)); } }
    /// <summary>الوحدة الظاهرة إن كانت وحدة أقسام (لأقسامها ومجموعاتها في القائمة الجانبية).</summary>
    public ModuleViewModel? CurrentModuleVm => CurrentModule as ModuleViewModel;

    public RelayCommand NavigateCommand { get; }
    public RelayCommand LogoutCommand { get; }

    /// <summary>فتح وحدة بكودها (للاختبارات وللروابط بين الشاشات).</summary>
    /// <summary>ينتظر كل أعمال الخلفية في الوحدات المفتوحة (لوحاتها وتبويباتها) — قبل الخروج أو حذف قاعدة تجريبية.</summary>
    public async Task IdleAllAsync()
    {
        await IdleAsync();
        foreach (var module in _modules.Values.ToList())
        {
            if (module is ViewModelBase vm) await vm.IdleAsync();
            if (module is ModuleViewModel m)
            {
                if (m.Dashboard is { } d) await d.IdleAsync();
                foreach (var s in m.Tabs.ToArray().OfType<SectionViewModel>()) await s.IdleAsync();
            }
        }
    }

    public T Open<T>(string moduleCode) where T : class
    {
        SelectedItem = NavItems.Single(n => n.ModuleCode == moduleCode);
        return (T)CurrentModule!;
    }

    /// <summary>الخروج التلقائي بعد هذه المدة بلا أي نشاط (لوحة مفاتيح أو فأرة) — حماية الجهاز المتروك مفتوحًا.</summary>
    public static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private bool _loggedOut;

    public void ReportActivity() => _lastActivityUtc = DateTime.UtcNow;

    /// <summary>يُستدعى دوريًا من النافذة: يعيد true إن خرج المستخدم بسبب الخمول.</summary>
    public bool CheckIdle(DateTime nowUtc)
    {
        if (_loggedOut || nowUtc - _lastActivityUtc < IdleTimeout) return false;
        _loggedOut = true;
        _navigator.ShowLogin();
        _dialogs.Info($"خرجت تلقائيًا بعد {IdleTimeout.TotalMinutes:0} دقيقة بلا نشاط. سجّل الدخول للمتابعة.");
        return true;
    }

    private void Logout()
    {
        if (_dialogs.Confirm("هل تريد تسجيل الخروج؟"))
            _navigator.ShowLogin();
    }
}

/// <summary>مجموعة في اللوحة المنبثقة: اسمها ولونها ومربعاتها (أقسام، أو «نظرة عامة»).</summary>
public sealed record FlyoutGroup(string Name, string Color, List<object> Items);

/// <summary>عمود في اللوحة المنبثقة: مجموعاته بالترتيب.</summary>
public sealed record FlyoutColumn(List<FlyoutGroup> Groups);
