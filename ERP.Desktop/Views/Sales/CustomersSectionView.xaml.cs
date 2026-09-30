using System.Windows.Controls;

namespace ERP.Desktop.Views.Sales;

public partial class CustomersSectionView : UserControl
{
    public CustomersSectionView()
    {
        InitializeComponent();
    }

    // زر "بدون" بجانب القوائم الاختيارية: يفرّغ القيمة (null) في الحقل المرتبط
    private void ClearCombo(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { Parent: DockPanel panel })
            foreach (var combo in panel.Children.OfType<ComboBox>()) combo.SelectedValue = null;
    }
}
