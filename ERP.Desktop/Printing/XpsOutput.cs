using System.IO;
using System.Windows.Documents;
using System.Windows.Xps.Packaging;

namespace ERP.Desktop.Printing;

/// <summary>كتابة المستند إلى ملف XPS (صيغة طابور الطباعة في Windows) — للاختبارات وللحفظ.</summary>
public static class XpsOutput
{
    public static void WriteFile(DocumentPaginator paginator, string path)
    {
        if (File.Exists(path)) File.Delete(path);
        using var xps = new XpsDocument(path, FileAccess.ReadWrite);
        XpsDocument.CreateXpsDocumentWriter(xps).Write(paginator);
    }

    /// <summary>عدد صفحات ملف XPS مكتوب (قراءة من القرص كما يفعل عارض/طابور Windows).</summary>
    public static int CountPages(string path)
    {
        using var xps = new XpsDocument(path, FileAccess.Read);
        var seq = xps.GetFixedDocumentSequence();
        return seq.References.Sum(r => r.GetDocument(false)!.Pages.Count);
    }
}
