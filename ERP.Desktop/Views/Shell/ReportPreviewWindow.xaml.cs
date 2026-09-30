using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using ERP.Desktop.Printing;
using ERP.Presentation.Services;

namespace ERP.Desktop.Views.Shell;

public partial class ReportPreviewWindow : Window
{
    private readonly ReportDocument _report;

    public ReportPreviewWindow(ReportDocument report)
    {
        InitializeComponent();
        _report = report;
        Title = $"معاينة — {report.Title}";
        Viewer.Document = ReportRenderer.Render(report);
    }

    private void OnPrint(object sender, RoutedEventArgs e)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;
        // مستند جديد بحجم الورق المختار (المعروض في المعاينة يبقى كما هو)
        var doc = ReportRenderer.Render(_report);
        doc.PageWidth = dialog.PrintableAreaWidth;
        doc.PageHeight = dialog.PrintableAreaHeight;
        doc.ColumnWidth = dialog.PrintableAreaWidth;
        dialog.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, _report.Title);
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
