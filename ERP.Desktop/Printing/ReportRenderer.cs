using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using ERP.Presentation.Services;

namespace ERP.Desktop.Printing;

/// <summary>يحوّل ReportDocument إلى FlowDocument عربي (A4، من اليمين لليسار) للمعاينة والطباعة.</summary>
public static class ReportRenderer
{
    private static readonly Brush Primary = new SolidColorBrush(Color.FromRgb(0x0F, 0x76, 0x6E));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
    private static readonly Brush HeaderFill = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
    private static readonly Brush Danger = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));

    public const double PageWidth = 793.7;   // A4 عند 96 نقطة/بوصة
    public const double PageHeight = 1122.5;

    public static FlowDocument Render(ReportDocument r)
    {
        var doc = new FlowDocument
        {
            FlowDirection = FlowDirection.RightToLeft,
            FontFamily = new FontFamily("Segoe UI, Tahoma, Arial"),
            FontSize = 12,
            PageWidth = PageWidth,
            PageHeight = PageHeight,
            ColumnWidth = PageWidth,
            PagePadding = new Thickness(40),
            Background = Brushes.White
        };

        // الترويسة: اسم الشركة + العنوان + الختم
        var head = new Table { CellSpacing = 0 };
        head.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        head.Columns.Add(new TableColumn { Width = GridLength.Auto });
        var hg = new TableRowGroup();
        var hr = new TableRow();
        hr.Cells.Add(new TableCell(new Paragraph(new Run(r.CompanyName)) { FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Primary, Margin = new Thickness(0) }));
        var title = new Paragraph(new Run(r.Title)) { FontSize = 18, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Left, Margin = new Thickness(0) };
        hr.Cells.Add(new TableCell(title));
        hg.Rows.Add(hr);
        head.RowGroups.Add(hg);
        doc.Blocks.Add(head);
        if (r.Stamp is not null)
            doc.Blocks.Add(new Paragraph(new Run(r.Stamp)) { Foreground = Danger, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Left, Margin = new Thickness(0, 2, 0, 0) });
        doc.Blocks.Add(new BlockUIContainer(new System.Windows.Shapes.Rectangle { Height = 2, Fill = Primary, Margin = new Thickness(0, 6, 0, 10) }));

        // حقول الرأس: عمودان من (عنوان: قيمة)
        if (r.HeaderFields.Count > 0)
        {
            var t = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 12) };
            for (var i = 0; i < 4; i++)
                t.Columns.Add(new TableColumn { Width = new GridLength(i % 2 == 0 ? 1 : 2, GridUnitType.Star) });
            var g = new TableRowGroup();
            for (var i = 0; i < r.HeaderFields.Count; i += 2)
            {
                var row = new TableRow();
                foreach (var f in r.HeaderFields.Skip(i).Take(2))
                {
                    row.Cells.Add(Cell(f.Label + ":", muted: true));
                    row.Cells.Add(Cell(f.Value, bold: true));
                }
                g.Rows.Add(row);
            }
            t.RowGroups.Add(g);
            doc.Blocks.Add(t);
        }

        // جدول البيانات
        if (r.Columns.Count > 0)
        {
            var t = new Table { CellSpacing = 0, BorderBrush = Line, BorderThickness = new Thickness(1, 1, 0, 0) };
            foreach (var c in r.Columns)
                t.Columns.Add(new TableColumn { Width = c is "#" ? new GridLength(32) : new GridLength(1, GridUnitType.Star) });
            var g = new TableRowGroup();
            var header = new TableRow { Background = HeaderFill };
            foreach (var c in r.Columns) header.Cells.Add(GridCell(c, bold: true));
            g.Rows.Add(header);
            foreach (var rowValues in r.Rows)
            {
                var row = new TableRow();
                foreach (var v in rowValues) row.Cells.Add(GridCell(v));
                g.Rows.Add(row);
            }
            if (r.Rows.Count == 0)
            {
                var empty = new TableRow();
                empty.Cells.Add(new TableCell(new Paragraph(new Run("لا توجد بيانات")) { Foreground = Muted, TextAlignment = TextAlignment.Center })
                { ColumnSpan = r.Columns.Count, BorderBrush = Line, BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(6) });
                g.Rows.Add(empty);
            }
            t.RowGroups.Add(g);
            doc.Blocks.Add(t);
        }

        // المجاميع (أقصى اليسار)
        if (r.Totals.Count > 0)
        {
            var t = new Table { CellSpacing = 0, Margin = new Thickness(0, 12, 0, 0) };
            t.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            t.Columns.Add(new TableColumn { Width = new GridLength(170) });
            t.Columns.Add(new TableColumn { Width = new GridLength(170) });
            var g = new TableRowGroup();
            foreach (var f in r.Totals)
            {
                var row = new TableRow();
                row.Cells.Add(new TableCell(new Paragraph()));
                row.Cells.Add(Cell(f.Label, muted: !f.Emphasis, bold: f.Emphasis));
                var v = Cell(f.Value, bold: true);
                if (f.Emphasis) { v.Background = HeaderFill; ((Paragraph)v.Blocks.FirstBlock).FontSize = 14; }
                row.Cells.Add(v);
                g.Rows.Add(row);
            }
            t.RowGroups.Add(g);
            doc.Blocks.Add(t);
        }

        if (!string.IsNullOrWhiteSpace(r.Notes))
            doc.Blocks.Add(new Paragraph(new Run("ملاحظات: " + r.Notes)) { Margin = new Thickness(0, 14, 0, 0) });

        // التواقيع
        if (r.Signatures.Count > 0)
        {
            var t = new Table { CellSpacing = 0, Margin = new Thickness(0, 48, 0, 0) };
            foreach (var _ in r.Signatures) t.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            var g = new TableRowGroup();
            var row = new TableRow();
            foreach (var s in r.Signatures)
                row.Cells.Add(new TableCell(new Paragraph(new Run(s)) { TextAlignment = TextAlignment.Center, Margin = new Thickness(0) })
                { BorderBrush = Muted, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(4) });
            g.Rows.Add(row);
            t.RowGroups.Add(g);
            doc.Blocks.Add(t);
        }

        doc.Blocks.Add(new Paragraph(new Run($"طُبع بواسطة {r.PrintedBy} — {r.PrintedAt:yyyy/MM/dd HH:mm}"))
        { FontSize = 10, Foreground = Muted, Margin = new Thickness(0, 24, 0, 0) });
        return doc;
    }

    private static TableCell Cell(string text, bool bold = false, bool muted = false) =>
        new(new Paragraph(new Run(text)) { Margin = new Thickness(0), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = muted ? Muted : Brushes.Black })
        { Padding = new Thickness(2, 3, 2, 3) };

    private static TableCell GridCell(string text, bool bold = false) =>
        new(new Paragraph(new Run(text)) { Margin = new Thickness(0), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal })
        { BorderBrush = Line, BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(6, 4, 6, 4) };
}
