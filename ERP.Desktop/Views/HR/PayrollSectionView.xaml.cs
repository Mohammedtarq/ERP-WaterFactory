using System.Windows;
using System.Windows.Controls;

namespace ERP.Desktop.Views.HR;

public partial class PayrollSectionView : UserControl
{
    // أعمدة الجدول: 3 للتعريف، 9 للمستحقات (أساسي، بدلات، إضافي أيام ومبلغها وساعات ومبلغها وإجماليه، حوافز، مجموع)، 9 للاستقطاعات، ثم الصافي
    private const int IdentityColumns = 3, EarningColumns = 9, DeductionColumns = 9;

    public PayrollSectionView()
    {
        InitializeComponent();
        PayrollGrid.LayoutUpdated += (_, _) => SyncGroupBand();
    }

    /// <summary>ضغطتان على سطر موظف: قصاصة راتبه.</summary>
    private void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is DataGridRow { Item: var row } && DataContext is ERP.Presentation.ViewModels.HR.PayrollSectionViewModel vm && vm.PrintPayslipCommand.CanExecute(row))
            vm.PrintPayslipCommand.Execute(row);
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
