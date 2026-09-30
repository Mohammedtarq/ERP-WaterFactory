using System.Windows;
using System.Windows.Threading;
using ERP.Data.Setup;
using ERP.Desktop.Services;
using ERP.Presentation.Mvvm;
using Microsoft.Data.SqlClient;

namespace ERP.Desktop;

public partial class App : Application
{
    private readonly WpfDialogService _dialogs = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // أي خطأ غير متوقع يظهر كرسالة عربية بدل إغلاق البرنامج
        DispatcherUnhandledException += OnUnhandled;
        AsyncRelayCommand.UnhandledErrorHandler = ex => Dispatcher.Invoke(() => ShowUnexpected(ex));

        var config = new AppConfigStore();
        var controlCs = config.LoadControlConnectionString();
        var navigator = new WpfNavigator(_dialogs, config, controlCs);

        // أول تشغيل: لا يوجد إعداد ← معالج الإعداد
        if (controlCs is null)
        {
            navigator.ShowSetup(null);
            return;
        }

        // إعداد موجود لكن السيرفر لا يستجيب أو قاعدة التحكم غير مثبّتة ← المعالج مع سبب واضح
        var problem = await CheckControlDatabaseAsync(controlCs);
        if (problem is not null) navigator.ShowSetup(problem);
        else navigator.ShowLogin();
    }

    private static async Task<string?> CheckControlDatabaseAsync(string controlCs)
    {
        var (ok, message) = await ProvisioningService.TestConnectionAsync(controlCs);
        if (!ok) return $"تعذّر الاتصال بالسيرفر المحفوظ. {message}";
        try
        {
            await using var conn = new SqlConnection(controlCs);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand("SELECT OBJECT_ID('Projects', 'U')", conn);
            return await cmd.ExecuteScalarAsync() is DBNull or null ? "قاعدة التحكم غير مثبّتة على هذا السيرفر." : null;
        }
        catch (SqlException ex)
        {
            return $"قاعدة التحكم غير متاحة: {ex.Message}";
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowUnexpected(e.Exception);
        e.Handled = true;
    }

    private void ShowUnexpected(Exception ex)
    {
        var root = ex.GetBaseException();
        var hint = root is SqlException
            ? "تحقق من اتصال قاعدة البيانات، ثم أعد المحاولة."
            : "أرسل نص هذه الرسالة كاملًا للدعم الفني.";
        _dialogs.Error($"حدث خطأ غير متوقع:\n{root.Message}\n\n{hint}");
    }
}
