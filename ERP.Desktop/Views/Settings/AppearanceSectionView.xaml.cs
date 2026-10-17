using System.Windows;
using System.Windows.Controls;

namespace ERP.Desktop.Views.Settings;

public partial class AppearanceSectionView : UserControl
{
    public AppearanceSectionView() => InitializeComponent();

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this)!, "إغلاق البرنامج؟", "نظام معمل المياه", MessageBoxButton.YesNo, MessageBoxImage.Question,
                            MessageBoxResult.No, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) == MessageBoxResult.Yes)
            Application.Current.Shutdown();
    }
}
