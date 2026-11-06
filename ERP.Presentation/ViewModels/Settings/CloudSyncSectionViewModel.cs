using System.Collections.ObjectModel;
using ERP.Cloud.Contracts;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;

namespace ERP.Presentation.ViewModels.Settings;

/// <summary>
/// «المزامنة»: نظامان معًا — الخادم المحلي على حاسبة السيرفر (واي فاي المعمل، يثبّته المثبّت ويُضبط عنوانه تلقائيًا)
/// والخادم السحابي (الإنترنت، بملفات نشر جاهزة). مفتاح المعمل يُولَّد ويصل الخادم المحلي تلقائيًا؛ لا نسخ ولا لصق.
/// الخدمة الدائمة تعمل على جهاز السيرفر (ERP.SyncAgent)؛ «مزامنة الآن» تجري دورة من هذا الجهاز للتجربة.
/// </summary>
public class CloudSyncSectionViewModel : SectionViewModel
{
    private string _serverUrl = "";
    private bool _isEnabled;
    private int _intervalSeconds = 20;
    private bool _hasKey;
    private string? _newKey;
    private string _stateText = "";
    private CloudSyncLevel _level;
    private string _details = "";
    private bool _localEnabled;
    private string _localUrl = "";
    private string _localText = "";
    private CloudSyncLevel _localLevel;
    private string _cloudText = "";
    private CloudSyncLevel _cloudLevel;

    public CloudSyncSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.SystemSettings, "المزامنة السحابية", Icons.Backup, "#0369A1", "ربط تطبيق المندوبين: الخادم المحلي (واي فاي المعمل) والخادم السحابي (الإنترنت) معًا")
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        GenerateKeyCommand = new AsyncRelayCommand(GenerateKeyAsync);
        TestCommand = new AsyncRelayCommand(TestAsync);
        SyncNowCommand = new AsyncRelayCommand(SyncNowAsync);
        CloudFilesCommand = new AsyncRelayCommand(CloudFilesAsync);
        SaveManagerCommand = new AsyncRelayCommand(SaveManagerAsync);
        ToggleManagerCommand = new AsyncRelayCommand(o => ToggleManagerAsync(o as ManagerAccount));
        DeleteManagerCommand = new AsyncRelayCommand(o => DeleteManagerAsync(o as ManagerAccount));
        SaveTvMessageCommand = new AsyncRelayCommand(SaveTvMessageAsync);
    }

    // ============================ لوحة المدير على الهاتف (قراءة فقط) ============================
    private string _managerUsername = "";
    private string _managerPassword = "";
    public ObservableCollection<ManagerAccount> ManagerAccounts { get; } = new();
    public string ManagerUsername { get => _managerUsername; set => SetProperty(ref _managerUsername, value); }
    /// <summary>تُكتب من حقل كلمة المرور وتُمسح بعد الحفظ؛ لا تُحفظ نصًّا في أي مكان.</summary>
    public string ManagerPassword { get => _managerPassword; set => SetProperty(ref _managerPassword, value); }
    public string ManagerLinkText => string.IsNullOrWhiteSpace(ServerUrl) ? "اكتب عنوان الخادم السحابي واحفظ ليظهر رابط اللوحة" : $"رابط اللوحة: {ServerUrl.Trim().TrimEnd('/')}/manager/";
    private bool _isTvAccount;
    private string _tvMessage = "";
    /// <summary>حساب شاشة التلفاز (بيانات غير حساسة فقط) بدل حساب المدير.</summary>
    public bool IsTvAccount { get => _isTvAccount; set => SetProperty(ref _isTvAccount, value); }
    public string TvMessage { get => _tvMessage; set => SetProperty(ref _tvMessage, value); }
    public string TvLinkText => string.IsNullOrWhiteSpace(ServerUrl) ? "" : $"رابط شاشة التلفاز: {ServerUrl.Trim().TrimEnd('/')}/tv/";
    public AsyncRelayCommand SaveTvMessageCommand { get; }
    public AsyncRelayCommand SaveManagerCommand { get; }
    public AsyncRelayCommand ToggleManagerCommand { get; }
    public AsyncRelayCommand DeleteManagerCommand { get; }

    private async Task SaveManagerAsync()
    {
        if (!Require(CanEdit, "حساب لوحة المدير")) return;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new ManagerSummaryService(db).SaveAccountAsync(ManagerUsername, ManagerPassword, Session.UserId, IsTvAccount ? ManagerKinds.Tv : ManagerKinds.Manager),
                                    IsTvAccount ? "حُفظ حساب شاشة التلفاز — يصل الخادم السحابي خلال دقيقة" : "حُفظ حساب لوحة المدير — يصل الخادم السحابي خلال دقيقة"))
        {
            ManagerPassword = "";
            ManagerUsername = "";
            await LoadAsync();
        }
    }

    private async Task SaveTvMessageAsync()
    {
        if (!Require(CanEdit, "رسالة شاشة التلفاز")) return;
        await using var db = Session.NewDb();
        await RunOperationAsync(() => new CloudSyncService(db).SaveTvMessageAsync(TvMessage, Session.UserId), "حُفظت رسالة الشاشة — تظهر خلال دقيقة");
    }

    private async Task ToggleManagerAsync(ManagerAccount? a)
    {
        if (a is null || !Require(CanEdit, "حساب لوحة المدير")) return;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new ManagerSummaryService(db).SetActiveAsync(a.Id, !a.IsActive, Session.UserId), a.IsActive ? "أُوقف الحساب" : "فُعّل الحساب"))
            await LoadAsync();
    }

    private async Task DeleteManagerAsync(ManagerAccount? a)
    {
        if (a is null || !Require(CanEdit, "حساب لوحة المدير")) return;
        if (!Dialogs.Confirm($"حذف حساب «{a.Username}»؟ تنتهي جلساته على الهاتف خلال دقيقة.")) return;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new ManagerSummaryService(db).DeleteAccountAsync(a.Id, Session.UserId), "حُذف الحساب"))
            await LoadAsync();
    }

    /// <summary>مجلد ملفات النشر (الاختبارات تغيّره) والخادم المنشور بجانب البرنامج.</summary>
    public Func<string> CloudFilesFolder { get; set; } = () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                                                                          $"الخادم السحابي - {DateTime.Now:yyyy-MM-dd HHmm}");
    public string CloudAppSource { get; set; } = Path.Combine(AppContext.BaseDirectory, "CloudServer");

    /// <summary>عميل الاتصال (تستبدله الاختبارات بخادم في الذاكرة).</summary>
    public Func<string, string, HttpClient> ClientFactory { get; set; } = (url, key) => CloudSyncService.CreateClient(url, key);

    protected override bool HasPendingInput => true;

    /// <summary>عنوان الخادم السحابي (اختياري).</summary>
    public string ServerUrl { get => _serverUrl; set { if (SetProperty(ref _serverUrl, value)) { OnPropertyChanged(nameof(ManagerLinkText)); OnPropertyChanged(nameof(TvLinkText)); } } }
    public bool LocalEnabled { get => _localEnabled; set => SetProperty(ref _localEnabled, value); }
    /// <summary>عنوان الخادم المحلي كما تكتبه خدمة المزامنة (للعرض فقط).</summary>
    public string LocalUrl { get => _localUrl; private set => SetProperty(ref _localUrl, value); }
    public string LocalText { get => _localText; private set => SetProperty(ref _localText, value); }
    public CloudSyncLevel LocalLevel { get => _localLevel; private set { if (SetProperty(ref _localLevel, value)) OnPropertyChanged(nameof(LocalColor)); } }
    public string LocalColor => ColorOf(LocalLevel);
    public string CloudText { get => _cloudText; private set => SetProperty(ref _cloudText, value); }
    public CloudSyncLevel CloudLevel { get => _cloudLevel; private set { if (SetProperty(ref _cloudLevel, value)) OnPropertyChanged(nameof(CloudColor)); } }
    public string CloudColor => ColorOf(CloudLevel);
    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
    public int IntervalSeconds { get => _intervalSeconds; set => SetProperty(ref _intervalSeconds, value); }
    public bool HasKey { get => _hasKey; private set { if (SetProperty(ref _hasKey, value)) OnPropertyChanged(nameof(KeyText)); } }
    /// <summary>المفتاح الجديد يُعرض مرة واحدة بعد توليده ليُلصق في إعدادات الخادم.</summary>
    public string? NewKey { get => _newKey; private set { if (SetProperty(ref _newKey, value)) OnPropertyChanged(nameof(KeyText)); } }
    public string KeyText => NewKey ?? (HasKey ? "مضبوط (مخفي) — يصل الخادم المحلي تلقائيًا، والسحابي ضمن ملفات النشر" : "يُولَّد تلقائيًا عند تفعيل المزامنة");
    public string StateText { get => _stateText; private set => SetProperty(ref _stateText, value); }
    public CloudSyncLevel Level { get => _level; private set { if (SetProperty(ref _level, value)) OnPropertyChanged(nameof(LevelColor)); } }
    public string LevelColor => ColorOf(Level);
    private static string ColorOf(CloudSyncLevel level) => level switch
    {
        CloudSyncLevel.Ok => "#16A34A",
        CloudSyncLevel.Warning => "#D97706",
        CloudSyncLevel.Error => "#DC2626",
        _ => "#94A3B8"
    };
    public string Details { get => _details; private set => SetProperty(ref _details, value); }

    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand GenerateKeyCommand { get; }
    public AsyncRelayCommand TestCommand { get; }
    public AsyncRelayCommand SyncNowCommand { get; }
    public AsyncRelayCommand CloudFilesCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var s = await new CloudSyncService(db).SettingsAsync();
        (ServerUrl, IsEnabled, IntervalSeconds, HasKey) = (s.ServerUrl ?? "", s.IsEnabled, s.IntervalSeconds, !string.IsNullOrEmpty(s.AgentKey));
        (LocalEnabled, LocalUrl) = (s.LocalEnabled, s.LocalUrl ?? "لم يُعرف بعد");
        ManagerAccounts.Clear();
        foreach (var a in await new ManagerSummaryService(db).AccountsAsync()) ManagerAccounts.Add(a);
        TvMessage = s.TvMessage ?? "";
        OnPropertyChanged(nameof(ManagerLinkText));
        OnPropertyChanged(nameof(TvLinkText));
        NewKey = null;   // المفتاح يُعرض مرة واحدة فقط بعد توليده
        var now = DateTime.UtcNow;
        (Level, StateText) = CloudSyncStatus.Describe(s, now);
        (LocalLevel, LocalText) = CloudSyncStatus.DescribeLocal(s, now);
        (CloudLevel, CloudText) = CloudSyncStatus.DescribeCloud(s, now);
        Details = string.Join("\n", new[]
        {
            $"آخر محاولة: {Local(s.LastAttemptAt)}{(s.AgentMachine is null ? "" : $" — من الجهاز {s.AgentMachine}")}",
            $"آخر تحديث لنسخ عمل المندوبين: {Local(s.LastSnapshotAt)}",
            $"حركات وصلت من الهواتف: {s.ReceivedCount:N0}",
        });
    }

    private static string Local(DateTime? utc) => utc is DateTime t ? t.ToLocalTime().ToString("yyyy/MM/dd HH:mm") : "—";

    private async Task SaveAsync()
    {
        if (!Require(CanEdit, "تعديل إعداد المزامنة")) return;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new CloudSyncService(db).SaveSettingsAsync(ServerUrl, IsEnabled, IntervalSeconds, Session.UserId, LocalEnabled),
                                    IsEnabled ? "حُفظ الإعداد — المزامنة مفعّلة" : "حُفظ الإعداد — المزامنة موقوفة"))
            await LoadAsync();
    }

    private async Task GenerateKeyAsync()
    {
        if (!Require(CanEdit, "توليد مفتاح المعمل")) return;
        if (HasKey && !Dialogs.Confirm("مفتاح جديد يصل الخادم المحلي تلقائيًا خلال دقيقة، لكن الخادم السحابي يتوقف حتى تُنشر ملفاته من جديد بزر «ملفات نشر الخادم السحابي». متابعة؟")) return;
        await using var db = Session.NewDb();
        await new CloudSyncService(db).GenerateKeyAsync(Session.UserId);
        await LoadAsync();
        StatusMessage = "وُلّد مفتاح جديد — الخادم المحلي يأخذه تلقائيًا" + (string.IsNullOrEmpty(ServerUrl) ? "" : "، وجهّز ملفات النشر للخادم السحابي من جديد");
    }

    /// <summary>الخوادم المضبوطة من هذا الجهاز: المحلي بعنوانه المسجّل، والسحابي.</summary>
    private async Task<List<(SyncTargetKind kind, string url, string key)>?> TargetsAsync()
    {
        await using var db = Session.NewDb();
        var s = await new CloudSyncService(db).SettingsAsync();
        if (string.IsNullOrEmpty(s.AgentKey)) { Dialogs.Error("فعّل المزامنة واحفظ أولًا (يُولَّد مفتاح المعمل تلقائيًا)"); return null; }
        var list = new List<(SyncTargetKind, string, string)>();
        if (s.LocalEnabled && !string.IsNullOrEmpty(s.LocalUrl)) list.Add((SyncTargetKind.Local, s.LocalUrl, s.AgentKey));
        if (!string.IsNullOrEmpty(s.ServerUrl)) list.Add((SyncTargetKind.Cloud, s.ServerUrl, s.AgentKey));
        if (list.Count == 0)
        {
            Dialogs.Error(s.LocalEnabled
                ? "لم يُعرف عنوان الخادم المحلي بعد — يكتبه تلقائيًا خادم المعمل حين يعمل (ثبّت النظام على حاسبة السيرفر بوضع «السيرفر»)"
                : "فعّل الخادم المحلي أو اكتب عنوان الخادم السحابي ثم احفظ");
            return null;
        }
        return list;
    }

    private async Task TestAsync()
    {
        if (await TargetsAsync() is not { } targets) return;
        var lines = new List<string>();
        var allOk = true;
        foreach (var (kind, url, key) in targets)
        {
            using var http = ClientFactory(url, key);
            var (ok, message) = await CloudSyncService.TestAsync(http);
            allOk &= ok;
            lines.Add($"{(kind == SyncTargetKind.Local ? "الخادم المحلي" : "الخادم السحابي")} ({url}): {message}");
        }
        var text = string.Join("\n", lines);
        if (allOk) Dialogs.Info(text); else Dialogs.Error(text);
        StatusMessage = text;
    }

    private async Task SyncNowAsync()
    {
        if (!Require(CanEdit, "المزامنة الآن")) return;
        if (await TargetsAsync() is not { } list) return;
        var clients = list.Select(t => new SyncTarget(t.kind, ClientFactory(t.url, t.key))).ToList();
        try
        {
            await using var db = Session.NewDb();
            var report = await new CloudSyncService(db).RunOnceAsync(clients, Environment.MachineName);
            if (report.Ok) StatusMessage = report.Text; else Dialogs.Error(report.Text);
            if (report.Received > 0 || report.Updated > 0) Session.MarkDataChanged();
        }
        finally
        {
            foreach (var c in clients) c.Http.Dispose();
        }
        await LoadAsync();
    }

    /// <summary>ملفات نشر الخادم السحابي: كل شيء جاهز (النطاق والمفتاح والخادم)، تُرفع كما هي.</summary>
    private async Task CloudFilesAsync()
    {
        if (!Require(CanEdit, "تجهيز ملفات الخادم السحابي")) return;
        await using var db = Session.NewDb();
        var s = await new CloudSyncService(db).SettingsAsync();
        var url = string.IsNullOrWhiteSpace(ServerUrl) ? s.ServerUrl : ServerUrl.Trim().TrimEnd('/');
        if (CloudDeployBundle.Validate(url, s.AgentKey) is string bad) { Dialogs.Error(bad); return; }
        if (!string.Equals(url, s.ServerUrl, StringComparison.OrdinalIgnoreCase)) { Dialogs.Error("احفظ عنوان الخادم السحابي أولًا"); return; }
        var folder = CloudFilesFolder();
        CloudDeployBundle.Write(folder, url!, s.AgentKey!, CloudAppSource);
        StatusMessage = $"جُهّزت ملفات النشر في: {folder} — فيها مفتاح المعمل فلا تشاركها";
        Dialogs.OpenUrl(folder);
    }
}
