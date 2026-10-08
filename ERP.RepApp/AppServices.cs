using ERP.RepApp.Core;

namespace ERP.RepApp;

/// <summary>خدمات التطبيق: الحفظ المحلي، وعمل المندوب، والمزامنة (تلقائيًا كل دقيقة والشاشة الرئيسية ظاهرة، وبعد كل حفظ).</summary>
public sealed class AppServices
{
    public static AppServices Instance { get; } = new();

    public LocalStore Store { get; }
    public RepWork Work { get; }
    public PhoneSyncResult? LastSync { get; private set; }
    public event Action? Changed;

    private readonly SyncEngine _sync;
    private RepCloudClient? _client;

    private AppServices()
    {
        Store = new LocalStore(Path.Combine(FileSystem.AppDataDirectory, "rep.db"));
        Work = new RepWork(Store);
        _sync = new SyncEngine(Store, () => _client ??= new RepCloudClient(Store.Link!));
    }

    /// <summary>بعد ربط جديد: اتصال جديد بالعنوان والمفتاح الجديدين.</summary>
    public void ResetClient() => _client = null;

    public async Task<PhoneSyncResult> SyncNowAsync()
    {
        var result = await _sync.RunAsync();
        LastSync = result;
        MainThread.BeginInvokeOnMainThread(() => Changed?.Invoke());
        return result;
    }

    /// <summary>مزامنة في الخلفية بعد الحفظ: لا تنتظرها الشاشة.</summary>
    public void SyncSoon() => _ = Task.Run(SyncNowAsync);
}
