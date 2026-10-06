using System.Windows;
using ERP.Data.Services;
using ERP.Desktop.Views.Shell;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;

namespace ERP.Desktop.Services;

/// <summary>كل شاشة في التدفق تفتح التالية ثم تُغلق نفسها: الإعداد ← الدخول ← المشروع ← الواجهة الرئيسية.</summary>
public class WpfNavigator : INavigator
{
    private readonly IDialogService _dialogs;
    private readonly IConfigStore _config;
    private AuthService? _auth;

    public WpfNavigator(IDialogService dialogs, IConfigStore config, string? controlConnectionString)
    {
        _dialogs = dialogs;
        _config = config;
        if (controlConnectionString is not null) _auth = new AuthService(controlConnectionString);
    }

    public void ShowLogin()
    {
        if (_auth is null) { ShowSetup(null); return; }
        Replace(new LoginWindow { DataContext = new LoginViewModel(_auth, _dialogs, this) });
    }

    public void ShowSetup(string? reason) => Replace(new SetupWindow { DataContext = new SetupViewModel(this, _config, reason) });

    /// <summary>تثبيت جهاز متدرب تلقائيًا (من سكربت التثبيت بالوسيط ‎--trainee): قاعدة LocalDB ببيانات تجريبية.</summary>
    public void ShowTraineeSetup()
    {
        var vm = new SetupViewModel(this, _config, "تثبيت جهاز متدرب: قاعدة تدريب محلية ببيانات تجريبية…");
        vm.UseTraineeMode();
        Replace(new SetupWindow { DataContext = vm });
        vm.FinishCommand.Execute(null);
    }
    public void ShowProjectSelection(ProjectSelectionViewModel vm) => Replace(new ProjectSelectionWindow { DataContext = vm });
    public void ShowMainShell(MainShellViewModel vm) => Replace(new MainWindow { DataContext = vm });

    public void UseControlConnection(string controlConnectionString)
    {
        _auth = new AuthService(controlConnectionString);
        ShowLogin();
    }

    private static void Replace(Window next)
    {
        var app = Application.Current;
        var previous = app.Windows.OfType<Window>().Where(w => w != next).ToList();
        app.MainWindow = next;
        next.Show();
        foreach (var w in previous) w.Close();
    }
}
