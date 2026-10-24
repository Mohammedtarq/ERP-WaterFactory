using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace ERP.Desktop.Views.Production;

public partial class ProductionMonthSectionView : UserControl
{
    public ProductionMonthSectionView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>تجميع الأرصدة تحت رأس «الصنف · الرمز» لتظهر أعمدة العبوة والمحتوى والمتغير بلا تكرار اسم الصنف.</summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (StockGrid.ItemsSource is null) return;
        var view = CollectionViewSource.GetDefaultView(StockGrid.ItemsSource);
        if (view is null || view.GroupDescriptions.Count > 0) return;
        view.GroupDescriptions.Add(new PropertyGroupDescription("Group"));
    }
}
