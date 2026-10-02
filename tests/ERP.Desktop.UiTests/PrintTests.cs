using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ERP.Desktop.Printing;
using ERP.Desktop.Views.Shell;
using ERP.Presentation;
using ERP.Presentation.Services;
using Xunit;
using Xunit.Abstractions;

namespace ERP.Desktop.UiTests;

/// <summary>
/// الطباعة الفعلية: كل اختبار يكتب ناتج الطباعة الحقيقي (XPS = صيغة طابور الطباعة في Windows)
/// ويحفظ صورة لكل صفحة/إيصال في ui-screenshots/print للمراجعة البصرية.
/// </summary>
[Collection("ui")]
public class PrintTests
{
    private readonly ITestOutputHelper _out;
    public PrintTests(ITestOutputHelper output) => _out = output;

    private static string PrintDir
    {
        get { var d = Path.Combine(UiThread.OutputDir, "print"); Directory.CreateDirectory(d); return d; }
    }

    /// <summary>شعار تجريبي PNG (دائرة بلون العلامة) لاختبار الترويسة بالشعار.</summary>
    private static byte[] SampleLogo()
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x0F, 0x76, 0x6E)), null, new Point(60, 60), 58, 58);
            dc.DrawEllipse(Brushes.White, null, new Point(60, 60), 30, 30);
        }
        var bmp = new RenderTargetBitmap(120, 120, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static ReportBranding Branding(bool logo = true) => new()
    {
        CompanyName = "مصنع مياه البصرة للصناعات الغذائية", CompanyNameEn = "Basra Water Factory",
        Phones = new[] { "0780 000 0000", "0770 111 2222" }, Address = "البصرة — الزبير — الحي الصناعي",
        TaxNumber = "100-200-300", BrandColor = "#0F766E", FooterText = "شكرًا لتعاملكم معنا", Logo = logo ? SampleLogo() : null
    };

    private static ReportDocument StockReport(int rows)
    {
        var r = new ReportDocument
        {
            CompanyName = "مصنع", Title = "تقرير حركة مخزن المواد الأولية", PrintedBy = "المدير", Branding = Branding()
        };
        r.Field("المخزن", "مخزن المواد الأولية").Field("الفترة", "2026/10/01 — 2026/10/31");
        r.Columns.AddRange(new[] { "الكود", "الصنف", "أول المدة", "وارد", "صادر", "تالف", "مجاني", "المتبقي" });
        for (var i = 1; i <= rows; i++)
            r.Rows.Add(new[] { $"RM-{i:000}", $"مادة أولية رقم {i}", $"{i * 10:N0}", i % 5 == 0 ? "0" : $"{i * 3:N0}", $"{i:N0}", "0", "0", i % 7 == 0 ? "0" : $"{i * 12:N0}" });
        r.Total("إجمالي الوارد", "9,999 قطعة").Total("المتبقي آخر المدة", "12,345 قطعة", true);
        r.Signatures.AddRange(new[] { "أمين المخزن", "المدقق", "المدير" });
        return r;
    }

    private static ReportDocument Invoice() 
    {
        var r = new ReportDocument
        {
            CompanyName = "مصنع", Title = "فاتورة مبيعات", PrintedBy = "سارة", Key = "SalesInvoice", ReceiptCapable = true,
            ReceiptColumns = new[] { 1, 3, 5, 6 }, Branding = Branding()
        };
        r.Field("رقم الفاتورة", "SI-2026-001024").Field("التاريخ", "2026/10/02").Field("العميل", "محل أبو حيدر").Field("طريقة الدفع", "نقدي");
        r.Columns.AddRange(new[] { "#", "الصنف", "الوحدة", "الكمية", "القطع", "السعر", "المبلغ" });
        r.Rows.Add(new[] { "1", "ماء 500 مل (W-500)", "كارتون", "10", "120", "2,400", "24,000" });
        r.Rows.Add(new[] { "2", "ماء 1.5 لتر (W-1500)", "شرنك", "5", "30", "3,000", "15,000" });
        r.Total("المجموع", "39,000 د.ع").Total("الإجمالي", "39,000 د.ع", true).Total("الإجمالي كتابةً", ArabicNumberWords.Amount(39000));
        r.Signatures.AddRange(new[] { "المستلم", "المحاسب" });
        return r;
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var x in Find<T>(c)) yield return x;
        }
    }

    private static void SavePage(Visual visual, Size size, string path)
    {
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>الخلل العاجل: عنوان التقرير كان يظهر حرفًا تحت حرف. الآن سطر أفقي واحد بعرض كافٍ.</summary>
    [Fact]
    public async Task Report_title_is_one_horizontal_line()
    {
        await UiThread.RunAsync(() =>
        {
            foreach (var title in new[] { "تقرير حركة مخزن المواد الأولية", "فاتورة مبيعات", "كشف صندوق — الصندوق الرئيسي", "شهادة فحص مختبري" })
            {
                var r = new ReportDocument { CompanyName = "مصنع", Title = title, Branding = Branding() };
                var header = ReportRenderer.BuildHeader(r, ReportRenderer.BrandingOf(r), ReportRenderer.PageWidth - 84);
                var tb = Find<TextBlock>(header).Single(t => t.Name == "DocumentTitle");
                Assert.Equal(title, tb.Text);
                // سطر واحد: الارتفاع أقل من سطرين، والعرض أكبر بكثير من حرف واحد
                Assert.True(tb.ActualHeight < tb.FontSize * 2.2, $"\"{title}\" ارتفاعه {tb.ActualHeight:0} — انكسر لأكثر من سطر");
                Assert.True(tb.ActualWidth > title.Length * 4, $"\"{title}\" عرضه {tb.ActualWidth:0} فقط");
            }
        });
    }

    /// <summary>A4 متعدد الصفحات: الترويسة في كل صفحة، "صفحة س من ص" صحيحة، وملف XPS حقيقي للطباعة.</summary>
    [Fact]
    public async Task A4_report_repeats_letterhead_and_page_numbers_on_every_page()
    {
        await UiThread.RunAsync(() =>
        {
            var report = StockReport(120);
            var paginator = ReportRenderer.A4(report);
            var pages = paginator.PageCount;
            Assert.True(pages >= 3, $"120 صفًا في {pages} صفحة فقط");

            XpsOutput.WriteFile(ReportRenderer.A4(report), Path.Combine(PrintDir, "A4_stock_report.xps"));
            for (var i = 0; i < pages; i++)
            {
                var page = paginator.GetPage(i);
                Assert.Equal(ReportRenderer.PageWidth, page.Size.Width, 1);
                var texts = Find<TextBlock>(page.Visual).ToList();
                Assert.Contains(texts, t => t.Name == "DocumentTitle" && t.Text == report.Title);               // الترويسة
                Assert.Contains(texts, t => t.Text == "مصنع مياه البصرة للصناعات الغذائية");                    // اسم الشركة
                Assert.Contains(texts, t => t.Name == "PageNumber" && t.Text == $"صفحة {i + 1} من {pages}");    // التذييل
                Assert.Contains(texts, t => t.Text == "شكرًا لتعاملكم معنا");
                Assert.Contains(Find<Image>(page.Visual), img => img.Source is not null);                    // الشعار
                SavePage(page.Visual, page.Size, Path.Combine(PrintDir, $"A4_stock_report_page{i + 1}.png"));
            }
            using var xps = new XpsOutput(ReportRenderer.A4(report));
            Assert.Equal(pages, xps.PageCount);
            _out.WriteLine($"A4: {pages} صفحات");
        });
    }

    /// <summary>إخفاء الأعمدة والصفوف الصفرية يُطبَّق على الناتج المطبوع نفسه.</summary>
    [Fact]
    public async Task Hidden_columns_and_zero_rows_are_removed_from_the_printout()
    {
        await UiThread.RunAsync(() =>
        {
            var report = StockReport(30);
            var hidden = new HashSet<int> { report.Columns.IndexOf("تالف"), report.Columns.IndexOf("مجاني") };
            var visible = report.WithVisibility(hidden, hideZeroRows: false);
            Assert.DoesNotContain("تالف", visible.Columns);
            Assert.Equal(6, visible.Columns.Count);
            Assert.All(visible.Rows, r => Assert.Equal(6, r.Count));

            var paginator = ReportRenderer.A4(visible);
            var texts = Find<TextBlock>(paginator.GetPage(0).Visual).Select(t => t.Text).ToList();
            SavePage(paginator.GetPage(0).Visual, paginator.PageSize, Path.Combine(PrintDir, "A4_hidden_columns.png"));
            Assert.DoesNotContain("تالف", texts);
        });
    }

    /// <summary>كاشير 80mm: عرض 80mm، طول بقدر المحتوى، والأعمدة المختصرة فقط.</summary>
    [Fact]
    public async Task Receipt_80mm_is_narrow_and_shows_only_receipt_columns()
    {
        await UiThread.RunAsync(() =>
        {
            var receipt = ReportRenderer.Receipt(Invoice());
            Assert.Equal(ReportRenderer.ReceiptWidth, receipt.ActualWidth, 1);
            Assert.InRange(receipt.ActualHeight, 250, 1400);
            var texts = Find<TextBlock>(receipt).Select(t => t.Text).ToList();
            Assert.Contains("الصنف", texts);
            Assert.Contains("المبلغ", texts);
            Assert.DoesNotContain("الوحدة", texts);              // أعمدة A4 فقط لا تظهر على الكاشير
            Assert.DoesNotContain("القطع", texts);
            Assert.Contains("مصنع مياه البصرة للصناعات الغذائية", texts);
            Assert.Contains(texts, t => t.StartsWith("فقط "));
            SavePage(receipt, new Size(receipt.ActualWidth, receipt.ActualHeight), Path.Combine(PrintDir, "Receipt_80mm_invoice.png"));
            _out.WriteLine($"إيصال 80mm بطول {receipt.ActualHeight / 96 * 25.4:0} مم");
        });
    }

    /// <summary>نافذة المعاينة: A4 من ناتج XPS نفسه، والتبديل للكاشير، وحفظ الإعداد الافتراضي لكل نوع مستند.</summary>
    [Fact]
    public async Task Preview_switches_printer_and_remembers_defaults_per_document_type()
    {
        var settings = Path.Combine(PrintDir, "print-settings-test.json");
        if (File.Exists(settings)) File.Delete(settings);
        Environment.SetEnvironmentVariable("ERP_PRINT_SETTINGS", settings);
        PrintPreferences.Reload();
        try
        {
            await UiThread.RunAsync(async () =>
            {
                var w = new ReportPreviewWindow(Invoice()) { ShowActivated = false, ShowInTaskbar = false, Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
                w.Show();
                await UiThread.SettleAsync();
                Assert.Equal(PrinterKind.A4, w.Printer);
                Assert.True(w.Viewer.Document is not null);
                UiThread.Save((FrameworkElement)w.Content, "print/Preview_A4.png");

                w.ReceiptRadio.IsChecked = true;
                await UiThread.SettleAsync();
                Assert.Equal(Visibility.Visible, w.ReceiptHost.Visibility);
                UiThread.Save((FrameworkElement)w.Content, "print/Preview_Receipt80.png");

                // إخفاء عمود + حفظ كافتراضي ← فتح فاتورة أخرى يعيد نفس الإعداد
                ((System.Collections.Generic.IEnumerable<ColumnOption>)w.ColumnsList.ItemsSource).Single(c => c.Name == "القطع").IsVisible = false;
                w.SaveAsDefault();
                w.Close();

                PrintPreferences.Reload();
                var again = new ReportPreviewWindow(Invoice()) { ShowActivated = false, ShowInTaskbar = false, Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
                again.Show();
                await UiThread.SettleAsync();
                Assert.Equal(PrinterKind.Receipt80, again.Printer);
                Assert.DoesNotContain("القطع", again.Visible.Columns);
                again.Close();

                // التقارير غير الصالحة للكاشير تبقى A4
                var report = new ReportPreviewWindow(StockReport(5)) { ShowActivated = false, ShowInTaskbar = false, Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
                report.Show();
                await UiThread.SettleAsync();
                Assert.False(report.ReceiptRadio.IsEnabled);
                Assert.Equal(PrinterKind.A4, report.Printer);
                report.Close();
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("ERP_PRINT_SETTINGS", null);
            PrintPreferences.Reload();
        }
    }
}
