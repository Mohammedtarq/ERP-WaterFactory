using System.Windows;
using System.Windows.Controls;
using ERP.Presentation.ViewModels.Shell;

namespace ERP.Desktop.Views.Shell;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        // المستخدم الأخير مختار مسبقًا: يبقى إدخال كلمة المرور فقط؛ ولا أحد = اسم المستخدم أولًا
        Loaded += (_, _) =>
        {
            if (DataContext is LoginViewModel { HasRecent: true } vm && string.IsNullOrEmpty(vm.Username)) vm.PickedUser = vm.Recent[0];
            FocusInput();
        };
    }

    private string CurrentPassword => ShowPassword.IsChecked == true ? PlainPassword.Text : PasswordBox.Password;

    // PasswordBox لا يدعم الربط (لأمان كلمة المرور)، فتُمرَّر لحظة الضغط فقط
    private void OnLoginClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm)
            vm.LoginCommand.Execute(CurrentPassword);
    }

    // اختيار مستخدم من الصور: الاسم يُملأ والمؤشر ينتقل لكلمة المرور
    private void OnPickUser(object sender, SelectionChangedEventArgs e) => FocusInput();
    private void OnOtherUser(object sender, RoutedEventArgs e) => Dispatcher.BeginInvoke(new Action(() => UserBox.Focus()));

    private void FocusInput() => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (DataContext is LoginViewModel { IsOtherUser: true }) UserBox.Focus();
        else if (ShowPassword.IsChecked == true) PlainPassword.Focus();
        else PasswordBox.Focus();
    }));

    private void OnPasswordChanged(object sender, RoutedEventArgs e) =>
        PasswordHint.Visibility = PasswordBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnShowPassword(object sender, RoutedEventArgs e)
    {
        PlainPassword.Text = PasswordBox.Password;
        PasswordBox.Visibility = Visibility.Collapsed;
        PlainPassword.Visibility = Visibility.Visible;
        PasswordHint.Visibility = Visibility.Collapsed;
        PlainPassword.Focus();
        PlainPassword.CaretIndex = PlainPassword.Text.Length;
    }

    private void OnHidePassword(object sender, RoutedEventArgs e)
    {
        PasswordBox.Password = PlainPassword.Text;
        PlainPassword.Text = "";
        PlainPassword.Visibility = Visibility.Collapsed;
        PasswordBox.Visibility = Visibility.Visible;
        OnPasswordChanged(sender, e);
        PasswordBox.Focus();
    }
}
