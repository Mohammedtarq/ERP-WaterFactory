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
            new NavItem(ModuleCode.Production, "الإنتاج والمختبر", Icons.Production, ModuleColors.Production,
                        () => new ProductionModuleViewModel(session, dialogs)),
            new NavItem(ModuleCode.SystemSettings, "إعدادات النظام", Icons.Settings, ModuleColors.Settings,
                        () => new SettingsModuleViewModel(session, dialogs)),
        };

        NavItems = all.Where(n => session.Permissions.CanView(n.ModuleCode)).ToList();
        NavigateCommand = new RelayCommand(p => { if (p is NavItem n) SelectedItem = n; });
        LogoutCommand = new RelayCommand(Logout);
        SelectedItem = NavItems.FirstOrDefault();
        Background(RefreshCloudStatusAsync());
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
            CloudStatusText = level == CloudSyncLevel.Off ? null : $"السحابة: {text}";
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
            if (_selectedItem is not null) _selectedItem.IsSelected = false;
            if (!SetProperty(ref _selectedItem, value) || value is null) return;
            value.IsSelected = true;
            if (!_modules.TryGetValue(value.ModuleCode, out var module))
            {
                _modules[value.ModuleCode] = module = value.Factory();
                if (module is ModuleViewModel created) created.AdoptMovedSections(Session, _dialogs, value.ModuleCode);
            }
            else
            {
                // العودة لوحدة مفتوحة سابقًا: التبويب الظاهر يُحدَّث إن حُفظت عمليات في شاشات أخرى
                if (module is ModuleViewModel m) m.Reactivate();
                else if (module is DashboardViewModel dash) Background(dash.RefreshIfChangedAsync());
            }
            CurrentModule = module;
            Background(RefreshCloudStatusAsync());
        }
    }

    public object? CurrentModule { get => _currentModule; private set => SetProperty(ref _currentModule, value); }

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
                foreach (var s in m.Tabs.OfType<SectionViewModel>().ToList()) await s.IdleAsync();
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
