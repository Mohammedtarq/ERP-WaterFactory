using System.Windows;
using System.Windows.Controls;

namespace ERP.Desktop.Views.HR;

public partial class PayrollSectionView : UserControl
{
    // أعمدة الجدول: 3 للتعريف، 5 للمستحقات، 8 للاستقطاعات، ثم الصافي
    private const int IdentityColumns = 3, EarningColumns = 5, DeductionColumns = 8;

    public PayrollSectionView()
    {
        InitializeComponent();
        PayrollGrid.LayoutUpdated += (_, _) => SyncGroupBand();
    }

    /// <summary>شريط «المستحقات / الاستقطاعات» فوق الجدول بعرض أعمدة كل مجموعة (يتبع تغيير العرض وتمرير الأعمدة).</summary>
    private void SyncGroupBand()
    {
        var cols = PayrollGrid.Columns;
        if (cols.Count < IdentityColumns + EarningColumns + DeductionColumns) return;
        double Sum(int from, int count) => cols.Skip(from).Take(count).Sum(c => c.ActualWidth);
        var scroll = FindScroll(PayrollGrid)?.HorizontalOffset ?? 0;
        var lead = Math.Max(0, PayrollGrid.RowHeaderActualWidth + Sum(0, IdentityColumns) - scroll);
        Set(BandLead, 0);
        Set(BandIdentity, lead);
        Set(BandEarnings, Sum(IdentityColumns, EarningColumns));
        Set(BandDeductions, Sum(IdentityColumns + EarningColumns, DeductionColumns));
    }

    private static void Set(ColumnDefinition c, double width)
    {
        if (Math.Abs(c.Width.Value - width) > 0.5) c.Width = new GridLength(width);
    }

    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScroll(child) is { } found) return found;
        }
        return null;
    }
}
