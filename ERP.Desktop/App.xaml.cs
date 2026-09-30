using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using ERP.Data.Services;
using ERP.Desktop.Services;
using ERP.Presentation.Mvvm;

namespace ERP.Desktop;

public partial class App : Application
{
    private readonly WpfDialogService _dialogs = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // أي خطأ غير متوقع يظهر كرسالة عربية بدل إغلاق البرنامج
        DispatcherUnhandledException += OnUnhandled;
        AsyncRelayCommand.UnhandledErrorHandler = ex => Dispatcher.Invoke(() => ShowUnexpected(ex));

        string? controlCs;
        try
        {
            controlCs = ReadControlConnectionString();
        }
        catch (Exception ex)
        {
            _dialogs.Error($"تعذّر قراءة ملف الإعدادات appsettings.json:\n{ex.Message}");
            Shutdown(1);
            return;
        }

        // التدفق: تسجيل الدخول ← اختيار المشروع ← الواجهة الرئيسية
        new WpfNavigator(new AuthService(controlCs), _dialogs).ShowLogin();
    }

    private static string ReadControlConnectionString()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("ControlDbConnectionString").GetString()
               ?? throw new InvalidOperationException("ControlDbConnectionString فارغ");
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowUnexpected(e.Exception);
        e.Handled = true;
    }

    private void ShowUnexpected(Exception ex)
    {
        var root = ex.GetBaseException();
        var hint = root is Microsoft.Data.SqlClient.SqlException
            ? "تحقق من اتصال قاعدة البيانات، ثم أعد المحاولة."
            : "أرسل نص هذه الرسالة كاملًا للدعم الفني.";
        _dialogs.Error($"حدث خطأ غير متوقع:\n{root.Message}\n\n{hint}");
    }
}
