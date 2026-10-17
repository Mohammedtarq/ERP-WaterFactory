using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using ERP.Desktop.Printing;
using ERP.Presentation.Services;

namespace ERP.Desktop.Views.Shell;

public class ColumnOption : INotifyPropertyChanged
{
    private bool _isVisible = true;
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public bool IsVisible { get => _isVisible; set { _isVisible = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// معاينة قبل الطباعة: اختيار A4 أو كاشير 80mm، إظهار/إخفاء الأعمدة، إخفاء الصفوف الصفرية، وحفظ الإعداد
/// كافتراضي لهذا النوع من المستندات. المعاينة هي ناتج الطباعة نفسه (XPS)، لا رسم تقريبي.
/// </summary>
public partial class ReportPreviewWindow : Window
{
    private readonly ReportDocument _report;
    private readonly ObservableCollection<ColumnOption> _columns;
    private bool _ready;

    public ReportPreviewWindow(ReportDocument report)
    {
        InitializeComponent();
        _report = report;
        Title = $"معاينة — {report.Title}";
        DocTitle.Text = report.Title;

        var pref = PrintPreferences.For(report.Key);
        _columns = new ObservableCollection<ColumnOption>(report.Columns.Select((c, i) => new ColumnOption
        {
            Index = i, Name = c == "#" ? "# (الترقيم)" : c, IsVisible = !pref.HiddenColumns.Contains(c)
        }));
        ColumnsList.ItemsSource = _columns;
        ColumnsPanel.Visibility = report.Columns.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HideZeroBox.IsChecked = pref.HideZeroRows;
        ReceiptRadio.IsEnabled = report.ReceiptCapable;
        ReceiptHint.Visibility = report.ReceiptCapable ? Visibility.Collapsed : Visibility.Visible;
        (pref.Printer == PrinterKind.Receipt80 && report.ReceiptCapable ? ReceiptRadio : A4Radio).IsChecked = true;
        _ready = true;
        Render();
    }

    public PrinterKind Printer => ReceiptRadio.IsChecked == true ? PrinterKind.Receipt80 : PrinterKind.A4;

    /// <summary>المستند بعد تطبيق الأعمدة الظاهرة وإخفاء الصفوف الصفرية.</summary>
    public ReportDocument Visible =>
        _report.WithVisibility(_columns.Where(c => !c.IsVisible).Select(c => c.Index).ToHashSet(), HideZeroBox.IsChecked == true);

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_ready) Render();
    }

    private void Render()
    {
        var doc = Visible;
        if (Printer == PrinterKind.A4)
        {
            var paginator = ReportRenderer.A4(doc);
            Viewer.Document = paginator.ToFixedDocument();
            Viewer.Visibility = Visibility.Visible;
            ReceiptHost.Visibility = Visibility.Collapsed;
            PagesText.Text = $"عدد الصفحات: {paginator.PageCount} (A4)";
        }
        else
        {
            var receipt = ReportRenderer.Receipt(doc);
            ReceiptFrame.Child = receipt;
            Viewer.Visibility = Visibility.Collapsed;
            ReceiptHost.Visibility = Visibility.Visible;
            PagesText.Text = $"إيصال 80mm — الطول {receipt.ActualHeight / 96 * 25.4:0} مم";
        }
        StatusText.Text = "";
    }

    private void OnSaveDefault(object sender, RoutedEventArgs e) => SaveAsDefault();

    /// <summary>حفظ نوع الطابعة والأعمدة المخفية وإخفاء الصفوف الصفرية كافتراضي لهذا النوع من المستندات.</summary>
    public void SaveAsDefault()
    {
        PrintPreferences.Save(_report.Key, new ReportPreference
        {
            Printer = Printer, HideZeroRows = HideZeroBox.IsChecked == true,
            HiddenColumns = _columns.Where(c => !c.IsVisible).Select(c => _report.Columns[c.Index]).ToList()
        });
        StatusText.Text = "حُفظ كإعداد افتراضي لهذا النوع من المستندات";
    }

    private void OnPrint(object sender, RoutedEventArgs e) => Print(null);

    private void OnSavePdf(object sender, RoutedEventArgs e) => Print("Microsoft Print to PDF");

    private void Print(string? queueName)
    {
        var dialog = new PrintDialog();
        if (queueName is not null)
        {
            try { dialog.PrintQueue = new LocalPrintServer().GetPrintQueue(queueName); }
            catch (Exception ex) when (ex is PrintQueueException or PrintSystemException or ArgumentException) { /* يختار المستخدم الطابعة */ }
        }
        var doc = Visible;
        if (Printer == PrinterKind.A4)
        {
            try { dialog.PrintTicket.PageMediaSize = new PageMediaSize(PageMediaSizeName.ISOA4); } catch (Exception) { }
        }
        else
        {
            ReportPrinter.BuildReceipt(doc, out var size);
            try { dialog.PrintTicket.PageMediaSize = new PageMediaSize(size.Width, size.Height); } catch (Exception) { }
        }
        if (dialog.ShowDialog() != true) return;
        try
        {
            ReportPrinter.Print(dialog.PrintQueue, dialog.PrintTicket, doc, Printer, _report.Title);
        }
        catch (Exception ex) when (ex is PrintSystemException or PrintQueueException or InvalidOperationException or ArgumentException)
        {
            // إلغاء نافذة حفظ ملف PDF أو طابعة غير متاحة: رسالة واضحة بدل خطأ غير متوقع
            StatusText.Text = "لم تتم الطباعة: " + ex.GetBaseException().Message;
            return;
        }
        // آخر نوع طابعة لهذا المستند يُتذكّر تلقائيًا
        var pref = PrintPreferences.For(_report.Key);
        pref.Printer = Printer;
        PrintPreferences.Save(_report.Key, pref);
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
