using System.Windows;
using ERP.Desktop.Views;

namespace ERP.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // التدفق: تسجيل الدخول ← اختيار المشروع ← الواجهة الرئيسية.
        // كل شاشة تُغلق نفسها وتفتح التالية بعد نجاحها (انظر أكواد الشاشات).
        var login = new LoginWindow();
        this.MainWindow = login;
        login.Show();
    }
}
