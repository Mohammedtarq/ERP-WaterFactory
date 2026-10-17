using System.Printing;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using ERP.Presentation.Services;

namespace ERP.Desktop.Printing;

/// <summary>
/// الإرسال الفعلي للطابعة. يُرسل المستند صفحاتٍ ثابتة (FixedDocument) — الصيغة التي يتعامل معها طابور طباعة
/// Windows بأمان — بدل مقسّم الصفحات الخام أو مرئية مجردة، اللذين قد يفشلان في مسار الطباعة الحديث لـ .NET
/// على بعض الطابعات ("Value cannot be null. (Parameter 'current')").
/// </summary>
public static class ReportPrinter
{
    public static FixedDocument BuildA4(ReportDocument doc) => Prepare(ReportRenderer.A4(doc).ToFixedDocument());

    /// <summary>الإيصال الحراري صفحة واحدة بعرض 80mm وبطول المحتوى.</summary>
    public static FixedDocument BuildReceipt(ReportDocument doc, out Size size)
    {
        var receipt = ReportRenderer.Receipt(doc);
        size = new Size(ReportRenderer.ReceiptWidth, Math.Ceiling(receipt.ActualHeight) + 16);
        var page = new FixedPage { Width = size.Width, Height = size.Height, Background = Brushes.White };
        page.Children.Add(receipt);
        var content = new PageContent();
        ((System.Windows.Markup.IAddChild)content).AddChild(page);
        var fixedDoc = new FixedDocument();
        fixedDoc.DocumentPaginator.PageSize = size;
        fixedDoc.Pages.Add(content);
        return Prepare(fixedDoc);
    }

    /// <summary>يطبع على طابور محدد بإعدادات الطباعة المختارة (من نافذة الطباعة أو للاختبار).</summary>
    public static void Print(PrintQueue queue, PrintTicket? ticket, ReportDocument doc, PrinterKind kind, string jobName)
    {
        var fixedDoc = kind == PrinterKind.A4 ? BuildA4(doc) : BuildReceipt(doc, out _);
        queue.CurrentJobSettings.Description = jobName;
        var writer = PrintQueue.CreateXpsDocumentWriter(queue);
        if (ticket is null) writer.Write(fixedDoc);
        else writer.Write(fixedDoc, ticket);
    }

    /// <summary>تخطيط كل صفحة قبل الإرسال (صفحات أُنشئت في الكود لم تُعرض بعد).</summary>
    private static FixedDocument Prepare(FixedDocument doc)
    {
        foreach (var content in doc.Pages)
        {
            var page = content.Child;
            if (page is null) continue;
            var size = new Size(page.Width, page.Height);
            page.Measure(size);
            page.Arrange(new Rect(size));
            page.UpdateLayout();
        }
        return doc;
    }
}
