using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;

namespace ERP.Presentation.ViewModels.Settings;

/// <summary>
/// «المزامنة السحابية»: عنوان الخادم ومفتاح المعمل والتفعيل، واختبار الاتصال، والمزامنة الآن، وحالة آخر مزامنة.
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

    public CloudSyncSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.SystemSettings, "المزامنة السحابية", Icons.Backup, "#0369A1", "ربط تطبيق المندوبين بالخادم السحابي: العنوان والمفتاح وحالة المزامنة")
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        GenerateKeyCommand = new AsyncRelayCommand(GenerateKeyAsync);
        TestCommand = new AsyncRelayCommand(TestAsync);
        SyncNowCommand = new AsyncRelayCommand(SyncNowAsync);
    }

    /// <summary>عميل الاتصال (تستبدله الاختبارات بخادم في الذاكرة).</summary>
    public Func<string, string, HttpClient> ClientFactory { get; set; } = (url, key) => CloudSyncService.CreateClient(url, key);

    protected override bool HasPendingInput => true;

    public string ServerUrl { get => _serverUrl; set => SetProperty(ref _serverUrl, value); }
    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
    public int IntervalSeconds { get => _intervalSeconds; set => SetProperty(ref _intervalSeconds, value); }
    public bool HasKey { get => _hasKey; private set { if (SetProperty(ref _hasKey, value)) OnPropertyChanged(nameof(KeyText)); } }
    /// <summary>المفتاح الجديد يُعرض مرة واحدة بعد توليده ليُلصق في إعدادات الخادم.</summary>
    public string? NewKey { get => _newKey; private set { if (SetProperty(ref _newKey, value)) OnPropertyChanged(nameof(KeyText)); } }
    public string KeyText => NewKey ?? (HasKey ? "مضبوط (مخفي) — ولّد مفتاحًا جديدًا إن فُقد" : "لم يُولَّد بعد");
    public string StateText { get => _stateText; private set => SetProperty(ref _stateText, value); }
    public CloudSyncLevel Level { get => _level; private set { if (SetProperty(ref _level, value)) OnPropertyChanged(nameof(LevelColor)); } }
    public string LevelColor => Level switch
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

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var s = await new CloudSyncService(db).SettingsAsync();
        (ServerUrl, IsEnabled, IntervalSeconds, HasKey) = (s.ServerUrl ?? "", s.IsEnabled, s.IntervalSeconds, !string.IsNullOrEmpty(s.AgentKey));
        NewKey = null;   // المفتاح يُعرض مرة واحدة فقط بعد توليده
        (Level, StateText) = CloudSyncStatus.Describe(s, DateTime.UtcNow);
        Details = string.Join("\n", new[]
        {
            $"آخر مزامنة ناجحة: {Local(s.LastSuccessAt)}",
            $"آخر محاولة: {Local(s.LastAttemptAt)}{(s.AgentMachine is null ? "" : $" — من الجهاز {s.AgentMachine}")}",
            $"آخر تحديث لنسخ عمل المندوبين: {Local(s.LastSnapshotAt)}",
            $"حركات وصلت من الهواتف عبر الخادم: {s.ReceivedCount:N0}",
            s.LastError is null ? "" : $"آخر خطأ ({Local(s.LastErrorAt)}): {s.LastError}",
        }.Where(x => x.Length > 0));
    }

    private static string Local(DateTime? utc) => utc is DateTime t ? t.ToLocalTime().ToString("yyyy/MM/dd HH:mm") : "—";

    private async Task SaveAsync()
    {
        if (!Require(CanEdit, "تعديل إعداد المزامنة")) return;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new CloudSyncService(db).SaveSettingsAsync(ServerUrl, IsEnabled, IntervalSeconds, Session.UserId),
                                    IsEnabled ? "حُفظ الإعداد — المزامنة مفعّلة" : "حُفظ الإعداد — المزامنة موقوفة"))
            await LoadAsync();
    }

    private async Task GenerateKeyAsync()
    {
        if (!Require(CanEdit, "توليد مفتاح المعمل")) return;
        if (HasKey && !Dialogs.Confirm("توليد مفتاح جديد يوقف المزامنة حتى يُلصق في إعدادات الخادم السحابي. متابعة؟")) return;
        await using var db = Session.NewDb();
        NewKey = await new CloudSyncService(db).GenerateKeyAsync(Session.UserId);
        HasKey = true;
        StatusMessage = "انسخ المفتاح الآن والصقه في إعداد الخادم Relay__AgentKey — لن يُعرض مرة أخرى";
    }

    private async Task<(string url, string key)?> ConnectionAsync()
    {
        if (CloudSyncService.ValidateUrl(ServerUrl) is string bad) { Dialogs.Error(bad); return null; }
        await using var db = Session.NewDb();
        var key = (await new CloudSyncService(db).SettingsAsync()).AgentKey;
        if (string.IsNullOrEmpty(key)) { Dialogs.Error("ولّد مفتاح المعمل أولًا"); return null; }
        return (ServerUrl.Trim(), key);
    }

    private async Task TestAsync()
    {
        if (await ConnectionAsync() is not { } c) return;
        using var http = ClientFactory(c.url, c.key);
        var (ok, message) = await CloudSyncService.TestAsync(http);
        if (ok) Dialogs.Info(message); else Dialogs.Error(message);
        StatusMessage = message;
    }

    private async Task SyncNowAsync()
    {
        if (!Require(CanEdit, "المزامنة الآن")) return;
        if (await ConnectionAsync() is not { } c) return;
        using var http = ClientFactory(c.url, c.key);
        await using var db = Session.NewDb();
        var report = await new CloudSyncService(db).RunOnceAsync(http, Environment.MachineName);
        if (report.Ok) StatusMessage = report.Text; else Dialogs.Error(report.Text);
        if (report.Received > 0 || report.Updated > 0) Session.MarkDataChanged();
        await LoadAsync();
    }
}
