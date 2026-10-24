using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ERP.Presentation.ViewModels.Settings;

namespace ERP.Desktop.Views.Settings;

public partial class CloudSyncSectionView : UserControl
{
    public CloudSyncSectionView()
    {
        InitializeComponent();
        // كلمة المرور تُمسح من الحقل بعد الحفظ (المصدر: النموذج)
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged old) old.PropertyChanged -= OnVmChanged;
            if (e.NewValue is INotifyPropertyChanged vm) vm.PropertyChanged += OnVmChanged;
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CloudSyncSectionViewModel.ManagerPassword) && sender is CloudSyncSectionViewModel { ManagerPassword.Length: 0 })
            ManagerPasswordBox.Clear();
    }

    private void ManagerPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is CloudSyncSectionViewModel vm) vm.ManagerPassword = ManagerPasswordBox.Password;
    }
}
