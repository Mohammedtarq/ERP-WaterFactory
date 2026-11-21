using System.Windows;
using ERP.Presentation.ViewModels.Shell;

namespace ERP.Desktop.Views.Shell;

public partial class SetupWindow : Window
{
    public SetupWindow()
    {
        InitializeComponent();
    }

    private SetupViewModel? Vm => DataContext as SetupViewModel;

    // حقول كلمات المرور لا تُربط (أمانًا)؛ تُنسخ لحظة الاستخدام فقط
    private void PushPasswords()
    {
        if (Vm is null) return;
        Vm.SqlPassword = SqlPasswordBox.Password;
        if (Vm.IsTraineeMode && AdminPasswordBox.Password.Length == 0) return;   // كلمة مرور المتدرب المعروفة
        Vm.AdminPassword = AdminPasswordBox.Password;
        Vm.AdminPasswordConfirm = AdminPasswordConfirmBox.Password;
    }

    private void OnTraineeClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        Vm.UseTraineeMode();
        AdminPasswordBox.Password = AdminPasswordConfirmBox.Password = SetupViewModel.TraineePassword;
    }

    private void OnTestClick(object sender, RoutedEventArgs e) => PushPasswords();
    private void OnFinishClick(object sender, RoutedEventArgs e) => PushPasswords();

    private void OnSqlAuthChecked(object sender, RoutedEventArgs e)
    {
        if (Vm is not null) Vm.UseWindowsAuth = false;
    }
}
