using System.IO;
using System.IO.Packaging;
using System.Windows.Documents;
using System.Windows.Xps.Packaging;

namespace ERP.Desktop.Printing;

/// <summary>
/// يكتب المستند المقسّم صفحات إلى XPS (صيغة طابور الطباعة في Windows) في الذاكرة أو في ملف.
/// المعاينة تعرض هذا الناتج نفسه، فلا فرق بين ما يُرى وما يُطبع.
/// </summary>
public sealed class XpsOutput : IDisposable
{
    private readonly MemoryStream _stream = new();
    private readonly Package _package;
    private readonly Uri _uri;
    private readonly XpsDocument _xps;

    public XpsOutput(DocumentPaginator paginator)
    {
        _uri = new Uri($"pack://erp-print-{Guid.NewGuid():N}.xps");
        _package = Package.Open(_stream, FileMode.Create, FileAccess.ReadWrite);
        PackageStore.AddPackage(_uri, _package);
        _xps = new XpsDocument(_package, CompressionOption.Normal, _uri.AbsoluteUri);
        XpsDocument.CreateXpsDocumentWriter(_xps).Write(paginator);
        Document = _xps.GetFixedDocumentSequence();
    }

    public FixedDocumentSequence Document { get; }
    public int PageCount => Document.DocumentPaginator.PageCount;

    /// <summary>حفظ ملف XPS على القرص (يفتح بعارض XPS ويُطبع منه، أو يُحوّل PDF).</summary>
    public static void WriteFile(DocumentPaginator paginator, string path)
    {
        if (File.Exists(path)) File.Delete(path);
        using var xps = new XpsDocument(path, FileAccess.ReadWrite);
        XpsDocument.CreateXpsDocumentWriter(xps).Write(paginator);
    }

    public void Dispose()
    {
        _xps.Close();
        PackageStore.RemovePackage(_uri);
        _package.Close();
        _stream.Dispose();
    }
}
