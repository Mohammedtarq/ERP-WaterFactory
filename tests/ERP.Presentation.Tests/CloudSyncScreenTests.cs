using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.ViewModels.Settings;
using ERP.Presentation.ViewModels.Shell;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// شاشة «المزامنة السحابية»: عنوان مشفّر إلزامًا، ومفتاح يُعرض مرة واحدة، ثم مؤشر الشريط الجانبي يعكس الحالة:
/// «لم تتم أي مزامنة» بعد التفعيل، و«متعذّرة» حين لا يُوصل للخادم.
/// </summary>
[Collection("app")]
public class CloudSyncScreenTests
{
    private readonly AppFixture _f;
    public CloudSyncScreenTests(AppFixture f) => _f = f;

    [Fact]
    public async Task Settings_key_once_enable_and_sidebar_indicator_follows_state()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        Assert.False(shell.HasCloudStatus);                                     // غير مفعّلة: لا مؤشر
        var settings = shell.Open<SettingsModuleViewModel>(ModuleCode.SystemSettings);
        var screen = settings.Section<CloudSyncSectionViewModel>();
        settings.SelectedTab = screen;
        await settings.IdleAsync();
        await screen.IdleAsync();
        Assert.Equal("المزامنة السحابية غير مفعّلة", screen.StateText);
        screen.ClientFactory = (url, key) => CloudSyncService.CreateClient("http://127.0.0.1:1", key);

        try
        {
            screen.ServerUrl = "http://api.example.com";
            screen.IsEnabled = true;
            await screen.SaveCommand.ExecuteAsync();
            Assert.Contains(dialogs.Errors, e => e.Contains("مشفّرًا"));

            screen.ServerUrl = "https://api.example.com";
            await screen.GenerateKeyCommand.ExecuteAsync();
            Assert.Equal(64, screen.NewKey!.Length);
            Assert.Equal(screen.NewKey, screen.KeyText);
            await screen.SaveCommand.ExecuteAsync();
            Assert.Null(screen.NewKey);
            Assert.Contains("المزامنة مفعّلة", screen.StatusMessage);
            Assert.StartsWith("مضبوط", screen.KeyText);                         // لا يُعرض بعد الحفظ وإعادة التحميل
            Assert.Equal(CloudSyncLevel.Warning, screen.Level);

            await shell.RefreshCloudStatusAsync();
            Assert.Contains("لم تتم أي مزامنة", shell.CloudStatusText);

            var errors = dialogs.Errors.Count;
            await screen.TestCommand.ExecuteAsync();
            Assert.Contains("تعذّر الاتصال بالخادم السحابي", dialogs.Errors[errors]);
            await screen.SyncNowCommand.ExecuteAsync();
            Assert.Equal(CloudSyncLevel.Error, screen.Level);
            Assert.Contains("آخر خطأ", screen.Details);
            await shell.RefreshCloudStatusAsync();
            Assert.Equal("#F87171", shell.CloudStatusColor);
        }
        finally
        {
            screen.IsEnabled = false;
            await screen.SaveCommand.ExecuteAsync();
        }
        await shell.RefreshCloudStatusAsync();
        Assert.False(shell.HasCloudStatus);
        Assert.Empty(_f.Unhandled);
    }
}
