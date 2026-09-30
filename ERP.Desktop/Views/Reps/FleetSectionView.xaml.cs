using System.Windows.Controls;

namespace ERP.Desktop.Views.Reps;

public partial class FleetSectionView : UserControl
{
    public FleetSectionView()
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
