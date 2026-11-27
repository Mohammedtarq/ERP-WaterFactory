using System.Data;
using System.Windows.Controls;

namespace ERP.Desktop.Views.Shell;

public partial class ReportsSectionView : UserControl
{
    public ReportsSectionView() => InitializeComponent();

    /// <summary>عناوين الأعمدة من التقرير (أسماؤها الداخلية c0، c1…)، وعمود المنتج أعرض.</summary>
    private void OnAutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (sender is DataGrid { ItemsSource: DataView view } && view.Table?.Columns[e.PropertyName] is { } column)
        {
            e.Column.Header = column.Caption;
            e.Column.Width = column.Ordinal == 0 ? new DataGridLength(2, DataGridLengthUnitType.Star) : new DataGridLength(1, DataGridLengthUnitType.Star);
        }
    }
}
