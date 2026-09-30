using System.Windows;
using ERP.Data.Services;
using ERP.Desktop.Views.Shell;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;

namespace ERP.Desktop.Services;

/// <summary>كل شاشة في التدفق تفتح التالية ثم تُغلق نفسها.</summary>
public class WpfNavigator : INavigator
{
    private readonly AuthService _auth;
    private readonly IDialogService _dialogs;

    public WpfNavigator(AuthService auth, IDialogService dialogs)
    {
        _auth = auth;
        _dialogs = dialogs;
    }

    public void ShowLogin() => Replace(new LoginWindow { DataContext = new LoginViewModel(_auth, _dialogs, this) });
    public void ShowProjectSelection(ProjectSelectionViewModel vm) => Replace(new ProjectSelectionWindow { DataContext = vm });
    public void ShowMainShell(MainShellViewModel vm) => Replace(new MainWindow { DataContext = vm });

    private static void Replace(Window next)
    {
        var app = Application.Current;
        var previous = app.Windows.OfType<Window>().ToList();
        app.MainWindow = next;
        next.Show();
        foreach (var w in previous) w.Close();
    }
}
