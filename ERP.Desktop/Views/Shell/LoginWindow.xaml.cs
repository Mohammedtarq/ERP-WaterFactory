using System.Windows;
using ERP.Presentation.ViewModels.Shell;

namespace ERP.Desktop.Views.Shell;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
    }

    // PasswordBox لا يدعم الربط (لأمان كلمة المرور)، فتُمرَّر لحظة الضغط فقط
    private void OnLoginClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm)
            vm.LoginCommand.Execute(PasswordBox.Password);
    }
}
