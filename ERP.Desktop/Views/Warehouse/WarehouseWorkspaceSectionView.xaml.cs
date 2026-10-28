using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace ERP.Desktop.Views.Warehouse;

public partial class WarehouseWorkspaceSectionView : UserControl
{
    public WarehouseWorkspaceSectionView()
    {
        InitializeComponent();
        // على الجدول نفسه لا على الشاشة: محتوى التبويب غير المختار لا يرث DataContext عند تحميل الشاشة،
        // فيكون ItemsSource فارغًا وقتها ويضيع التجميع (فيختفي اسم الصنف) — الخلل الذي ظهر في أرصدة المواد الأولية.
        StockGrid.Loaded += (_, _) => GroupByItem(StockGrid);
    }

    /// <summary>تجميع الأرصدة تحت رأس «الصنف · الرمز» لتظهر أعمدة العبوة والمحتوى والمتغير بلا تكرار اسم الصنف.</summary>
    private static void GroupByItem(DataGrid grid)
    {
        if (grid.ItemsSource is null) return;
        var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
        if (view is null || view.GroupDescriptions.Count > 0) return;
        view.GroupDescriptions.Add(new PropertyGroupDescription("Group"));
    }
}
