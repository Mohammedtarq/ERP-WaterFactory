using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.ViewModels.Settings;
using ERP.Presentation.ViewModels.Shell;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// شاشة «المزامنة»: الخادمان المحلي والسحابي، عنوان مشفّر إلزامًا للسحابي، ومفتاح المعمل يُولَّد تلقائيًا (لا نسخ ولا لصق)،
/// وملفات نشر السحابي جاهزة، ثم مؤشر الشريط الجانبي يعكس الحالة: «لم يتصل بعد» بعد التفعيل، و«متعذّر» حين لا يُوصل للخادم.
/// </summary>
[Collection("app")]
public class CloudSyncScreenTests
{
    private readonly AppFixture _f;
    public CloudSyncScreenTests(AppFixture f) => _f = f;

    [Fact]
    public async Task Manager_account_is_saved_hashed_toggled_and_deleted_from_the_sync_screen()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var settings = shell.Open<SettingsModuleViewModel>(ModuleCode.SystemSettings);
        var screen = settings.Section<CloudSyncSectionViewModel>();
        settings.SelectedTab = screen;
        await settings.IdleAsync();
        await screen.IdleAsync();

        screen.ManagerUsername = "mgr.phone";
        screen.ManagerPassword = "short";
        await screen.SaveManagerCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("8 أحرف"));
        Assert.Empty(screen.ManagerAccounts);

        screen.ManagerPassword = "Phone#2026";
        await screen.SaveManagerCommand.ExecuteAsync();
        var account = Assert.Single(screen.ManagerAccounts);
        Assert.Equal("mgr.phone", account.Username);
        Assert.True(account.IsActive);
        Assert.NotEqual("Phone#2026", account.PasswordHash);                    // مجزّأة لا نصًّا
        Assert.Equal("", screen.ManagerPassword);                                // تُمسح بعد الحفظ

        await screen.ToggleManagerCommand.ExecuteAsync(account);
        Assert.False(Assert.Single(screen.ManagerAccounts).IsActive);
        dialogs.ConfirmAnswer = true;
        await screen.DeleteManagerCommand.ExecuteAsync(Assert.Single(screen.ManagerAccounts));
        Assert.Empty(screen.ManagerAccounts);
    }

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
        Assert.Equal("المزامنة غير مفعّلة", screen.StateText);
        screen.ClientFactory = (url, key) => CloudSyncService.CreateClient("http://127.0.0.1:1", key);
        var deploy = Path.Combine(Path.GetTempPath(), "erp-deploy-" + Guid.NewGuid().ToString("N"));
        screen.CloudFilesFolder = () => deploy;

        try
        {
            screen.ServerUrl = "http://api.example.com";
            screen.IsEnabled = true;
            await screen.SaveCommand.ExecuteAsync();
            Assert.Contains(dialogs.Errors, e => e.Contains("مشفّرًا"));
            screen.ServerUrl = "";
            await screen.SaveCommand.ExecuteAsync();
            Assert.Contains(dialogs.Errors, e => e.Contains("فعّل الخادم المحلي أو اكتب عنوان الخادم السحابي"));

            // الخادمان معًا، والمفتاح يُولَّد وحده عند أول تفعيل
            Assert.StartsWith("يُولَّد تلقائيًا", screen.KeyText);
            screen.ServerUrl = "https://api.example.com";
            screen.LocalEnabled = true;
            await screen.SaveCommand.ExecuteAsync();
            Assert.Contains("المزامنة مفعّلة", screen.StatusMessage);
            Assert.True(screen.HasKey);
            Assert.Null(screen.NewKey);
            Assert.StartsWith("مضبوط", screen.KeyText);
            Assert.Equal(CloudSyncLevel.Warning, screen.Level);
            Assert.Equal("لم يُعرف بعد", screen.LocalUrl);                      // تكتبه خدمة المزامنة على حاسبة السيرفر
            Assert.Contains("لم يتصل بعد", screen.LocalText);
            Assert.Contains("لم يتصل بعد", screen.CloudText);

            await shell.RefreshCloudStatusAsync();
            Assert.Contains("لم يتصل بعد", shell.CloudStatusText);

            // ملفات نشر السحابي: النطاق والمفتاح جاهزان
            await screen.CloudFilesCommand.ExecuteAsync();
            Assert.Contains("RELAY_AGENT_KEY=", File.ReadAllText(Path.Combine(deploy, ".env")));
            Assert.Contains("DOMAIN=api.example.com", File.ReadAllText(Path.Combine(deploy, ".env")));
            Assert.Contains("لا تشاركها", screen.StatusMessage);

            var errors = dialogs.Errors.Count;
            await screen.TestCommand.ExecuteAsync();                                // المحلي بلا عنوان بعد: يُختبر السحابي
            Assert.Contains("الخادم السحابي (https://api.example.com): تعذّر الاتصال بالخادم", dialogs.Errors[errors]);
            await screen.SyncNowCommand.ExecuteAsync();
            Assert.Equal(CloudSyncLevel.Error, screen.Level);
            Assert.Equal(CloudSyncLevel.Error, screen.CloudLevel);
            Assert.Contains("متعذّر", screen.CloudText);
            await shell.RefreshCloudStatusAsync();
            Assert.Equal("#F87171", shell.CloudStatusColor);

            // مفتاح جديد: لا يُعرض للنسخ، والخادم المحلي يأخذه وحده
            dialogs.ConfirmAnswer = true;
            await screen.GenerateKeyCommand.ExecuteAsync();
            Assert.Contains("الخادم المحلي يأخذه تلقائيًا", screen.StatusMessage);
            Assert.Null(screen.NewKey);
        }
        finally
        {
            screen.IsEnabled = false;
            screen.LocalEnabled = false;
            screen.ServerUrl = "";
            await screen.SaveCommand.ExecuteAsync();
            try { Directory.Delete(deploy, true); } catch (IOException) { }
        }
        await shell.RefreshCloudStatusAsync();
        Assert.False(shell.HasCloudStatus);
        Assert.Empty(_f.Unhandled);
    }
}
