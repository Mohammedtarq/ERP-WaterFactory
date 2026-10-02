using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ERP.Presentation.Services;

namespace ERP.Desktop.Printing;

/// <summary>
/// محرك الطباعة الموحّد. كل مستند في النظام (فاتورة، سند، قيد، تقرير...) يمر من هنا:
/// - A4: ترويسة الشركة (شعار، اسم، اتصال، عنوان المستند) وتذييل (صفحة س من ص، وقت الطباعة، نص التذييل، المستخدم)
///   يتكرران في كل صفحة؛ الألوان فقط في الخط الفاصل ورأس الجدول والإجماليات.
/// - كاشير 80mm: إيصال ضيق بطول المحتوى تمامًا.
/// الترويسة تُبنى بشبكة WPF (Grid) لا بجدول مستند: جدول المستند لا يدعم الأعمدة التلقائية، وكان ذلك سبب ظهور
/// العنوان حرفًا تحت حرف.
/// </summary>
public static class ReportRenderer
{
    public const double PageWidth = 793.7;    // A4 عند 96 نقطة/بوصة
    public const double PageHeight = 1122.5;
    public static readonly Thickness PageMargin = new(42, 34, 42, 30);
    public const double ReceiptWidth = 302;  // 80mm
    private const string Font = "Segoe UI, Tahoma, Arial";

    private static readonly Brush Ink = Frozen(Color.FromRgb(0x0F, 0x17, 0x2A));
    private static readonly Brush Muted = Frozen(Color.FromRgb(0x47, 0x55, 0x69));
    private static readonly Brush Rule = Frozen(Color.FromRgb(0xE2, 0xE8, 0xF0));
    private static readonly Brush Danger = Frozen(Color.FromRgb(0xB9, 0x1C, 0x1C));

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public static Color BrandColor(ReportBranding b)
    {
        try { return (Color)ColorConverter.ConvertFromString(b.BrandColor); }
        catch (FormatException) { return (Color)ColorConverter.ConvertFromString(ReportBranding.DefaultColor); }
    }

    public static ReportBranding BrandingOf(ReportDocument r)
    {
        var b = r.Branding ?? ReportBranding.Current;
        return string.IsNullOrWhiteSpace(b.CompanyName) ? new ReportBranding
        {
            CompanyName = r.CompanyName, CompanyNameEn = b.CompanyNameEn, Phones = b.Phones, Address = b.Address, TaxNumber = b.TaxNumber,
            CommercialRegister = b.CommercialRegister, BrandColor = b.BrandColor, FooterText = b.FooterText, Logo = b.Logo
        } : b;
    }

    public static ImageSource? LoadLogo(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException) { return null; }
    }

    // ===================================================================
    //                                A4
    // ===================================================================

    /// <summary>مستند A4 مقسّم صفحات، بترويسة وتذييل في كل صفحة. pageSize افتراضيًا A4 (أو مساحة الطابعة).</summary>
    public static BrandedPaginator A4(ReportDocument r, Size? pageSize = null)
    {
        var b = BrandingOf(r);
        var size = pageSize ?? new Size(PageWidth, PageHeight);
        var contentWidth = size.Width - PageMargin.Left - PageMargin.Right;
        var header = BuildHeader(r, b, contentWidth);
        var footerSample = BuildFooter(r, b, contentWidth, 1, 1);
        var bodyHeight = size.Height - PageMargin.Top - PageMargin.Bottom - header.DesiredSize.Height - footerSample.DesiredSize.Height - 16;

        var body = BuildBody(r, b, contentWidth, bodyHeight);
        var inner = ((IDocumentPaginatorSource)body).DocumentPaginator;
        inner.PageSize = new Size(contentWidth, bodyHeight);
        return new BrandedPaginator(inner, size, PageMargin, header.DesiredSize.Height + 10, footerSample.DesiredSize.Height,
            () => BuildHeader(r, b, contentWidth), (page, total) => BuildFooter(r, b, contentWidth, page, total));
    }

    /// <summary>الترويسة: الشعار والشركة في البداية (يمين)، عنوان المستند في النهاية (يسار) بخط أكبر، ثم خط بلون العلامة.</summary>
    public static FrameworkElement BuildHeader(ReportDocument r, ReportBranding b, double width)
    {
        var brand = new SolidColorBrush(BrandColor(b));
        var grid = new Grid { Width = width, FlowDirection = FlowDirection.RightToLeft };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var company = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (LoadLogo(b.Logo) is { } logo)
            company.Children.Add(new Image { Source = logo, MaxHeight = 64, MaxWidth = 96, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 12, 0),
                                             VerticalAlignment = VerticalAlignment.Center });
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(Text(b.CompanyName, 17, FontWeights.Bold, Ink));
        if (!string.IsNullOrWhiteSpace(b.CompanyNameEn)) names.Children.Add(Text(b.CompanyNameEn!, 10, FontWeights.Normal, Muted, FlowDirection.LeftToRight));
        if (b.ContactLine.Length > 0) names.Children.Add(Text(b.ContactLine, 9.5, FontWeights.Normal, Muted));
        if (b.RegistrationLine.Length > 0) names.Children.Add(Text(b.RegistrationLine, 9, FontWeights.Normal, Muted));
        company.Children.Add(names);
        grid.Children.Add(company);

        // في اتجاه RTL: Right = نهاية السطر = يسار الصفحة
        var titleBlock = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var title = Text(r.Title, 18, FontWeights.Bold, Ink);
        title.TextAlignment = TextAlignment.Right;
        title.Name = "DocumentTitle";
        titleBlock.Children.Add(title);
        var number = r.HeaderFields.FirstOrDefault(f => f.Label.StartsWith("رقم"));
        if (number is not null) titleBlock.Children.Add(new TextBlock
        {
            Text = number.Value, FontSize = 11, FontFamily = new FontFamily(Font), Foreground = brand, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right, FlowDirection = FlowDirection.LeftToRight
        });
        if (r.Stamp is not null)
        {
            var stamp = Text(r.Stamp, 10.5, FontWeights.Bold, Danger);
            stamp.TextAlignment = TextAlignment.Right;
            titleBlock.Children.Add(stamp);
        }
        Grid.SetColumn(titleBlock, 1);
        grid.Children.Add(titleBlock);

        var line = new Rectangle { Height = 2, Fill = brand, Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(line, 1);
        Grid.SetColumnSpan(line, 2);
        grid.Children.Add(line);
        Layout(grid, width);
        return grid;
    }

    /// <summary>التذييل: من طبع ومتى، نص التذييل، ورقم الصفحة "س من ص".</summary>
    public static FrameworkElement BuildFooter(ReportDocument r, ReportBranding b, double width, int page, int total)
    {
        var grid = new Grid { Width = width, FlowDirection = FlowDirection.RightToLeft };
        for (var i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(i == 1 ? 2 : 1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var line = new Rectangle { Height = 0.75, Fill = Rule, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumnSpan(line, 3);
        grid.Children.Add(line);

        var who = Text($"طُبع بواسطة {r.PrintedBy}\n{r.PrintedAt:yyyy/MM/dd HH:mm}", 8.5, FontWeights.Normal, Muted);
        Grid.SetRow(who, 1);
        grid.Children.Add(who);
        if (!string.IsNullOrWhiteSpace(b.FooterText))
        {
            var foot = Text(b.FooterText!, 9, FontWeights.Normal, Ink);
            foot.TextAlignment = TextAlignment.Center;
            Grid.SetRow(foot, 1);
            Grid.SetColumn(foot, 1);
            grid.Children.Add(foot);
        }
        var pageText = Text($"صفحة {page} من {total}", 9, FontWeights.SemiBold, Ink);
        pageText.TextAlignment = TextAlignment.Right;
        pageText.Name = "PageNumber";
        Grid.SetRow(pageText, 1);
        Grid.SetColumn(pageText, 2);
        grid.Children.Add(pageText);
        Layout(grid, width);
        return grid;
    }

    private static TextBlock Text(string text, double size, FontWeight weight, Brush brush, FlowDirection dir = FlowDirection.RightToLeft) => new()
    {
        Text = text, FontSize = size, FontWeight = weight, Foreground = brush, FontFamily = new FontFamily(Font),
        TextWrapping = TextWrapping.Wrap, FlowDirection = dir
    };

    private static void Layout(FrameworkElement e, double width)
    {
        e.Measure(new Size(width, double.PositiveInfinity));
        e.Arrange(new Rect(0, 0, width, e.DesiredSize.Height));
        e.UpdateLayout();
    }

    /// <summary>محتوى المستند (يُقسَّم صفحات): حقول الرأس، الجدول، الإجماليات، الملاحظات، التواقيع.</summary>
    public static FlowDocument BuildBody(ReportDocument r, ReportBranding b, double width, double pageHeight)
    {
        var brandColor = BrandColor(b);
        var brand = new SolidColorBrush(brandColor);
        var tint = new SolidColorBrush(Color.FromArgb(0x1F, brandColor.R, brandColor.G, brandColor.B));
        var doc = new FlowDocument
        {
            FlowDirection = FlowDirection.RightToLeft, FontFamily = new FontFamily(Font), FontSize = 11, Foreground = Ink,
            PageWidth = width, PageHeight = pageHeight, ColumnWidth = width, PagePadding = new Thickness(0),
            Background = Brushes.White, LineHeight = 17, IsOptimalParagraphEnabled = false
        };

        // حقول الرأس: أربعة أعمدة بعروض ثابتة نسبيًا (عنوان: قيمة × 2) — رقم المستند في الترويسة
        var fields = r.HeaderFields.Where(f => !f.Label.StartsWith("رقم")).ToList();
        if (fields.Count > 0)
        {
            var t = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 12) };
            foreach (var w in new[] { 1.0, 2.0, 1.0, 2.0 }) t.Columns.Add(new TableColumn { Width = new GridLength(w, GridUnitType.Star) });
            var g = new TableRowGroup();
            for (var i = 0; i < fields.Count; i += 2)
            {
                var row = new TableRow();
                foreach (var f in fields.Skip(i).Take(2))
                {
                    row.Cells.Add(Cell(f.Label, Muted, FontWeights.Normal, 10.5));
                    row.Cells.Add(Cell(f.Value, Ink, FontWeights.SemiBold, 11));
                }
                g.Rows.Add(row);
            }
            t.RowGroups.Add(g);
            doc.Blocks.Add(t);
        }

        // الجدول: رأس بلون العلامة الخفيف، فواصل رفيعة بين الصفوف، رأس الجدول يتكرر في كل صفحة
        if (r.Columns.Count > 0)
        {
            var t = new Table { CellSpacing = 0 };
            foreach (var (c, i) in r.Columns.Select((c, i) => (c, i)))
                t.Columns.Add(new TableColumn { Width = c == "#" ? new GridLength(30) : new GridLength(ColumnWeight(r, i), GridUnitType.Star) });
            var head = new TableRowGroup();
            var hr = new TableRow { Background = tint };
            foreach (var c in r.Columns)
                hr.Cells.Add(new TableCell(new Paragraph(new Run(c)) { Margin = new Thickness(0), FontWeight = FontWeights.Bold, FontSize = 10.5 })
                { Padding = new Thickness(6, 5, 6, 5), BorderBrush = brand, BorderThickness = new Thickness(0, 0, 0, 1.2) });
            head.Rows.Add(hr);
            t.RowGroups.Add(head);
            var g = new TableRowGroup();
            foreach (var rowValues in r.Rows)
            {
                var row = new TableRow();
                foreach (var v in rowValues)
                    row.Cells.Add(new TableCell(new Paragraph(new Run(v)) { Margin = new Thickness(0), FontSize = 10.5 })
                    { Padding = new Thickness(6, 4, 6, 4), BorderBrush = Rule, BorderThickness = new Thickness(0, 0, 0, 0.75) });
                g.Rows.Add(row);
            }
            if (r.Rows.Count == 0)
            {
                var empty = new TableRow();
                empty.Cells.Add(new TableCell(new Paragraph(new Run("لا توجد بيانات")) { Foreground = Muted, TextAlignment = TextAlignment.Center })
                { ColumnSpan = r.Columns.Count, Padding = new Thickness(6) });
                g.Rows.Add(empty);
            }
            t.RowGroups.Add(g);
            doc.Blocks.Add(t);
        }

        // الإجماليات في النهاية (يسار): الإجمالي النهائي بلون العلامة
        if (r.Totals.Count > 0)
        {
            var t = new Table { CellSpacing = 0, Margin = new Thickness(0, 12, 0, 0) };
            t.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            t.Columns.Add(new TableColumn { Width = new GridLength(1.2, GridUnitType.Star) });
            t.Columns.Add(new TableColumn { Width = new GridLength(1.4, GridUnitType.Star) });
            var g = new TableRowGroup();
            foreach (var f in r.Totals)
            {
                var row = new TableRow();
                row.Cells.Add(new TableCell(new Paragraph()));
                var words = f.Value.StartsWith("فقط ");
                var label = Cell(f.Label, f.Emphasis ? brand : Muted, f.Emphasis ? FontWeights.Bold : FontWeights.Normal, 11);
                var value = Cell(f.Value, f.Emphasis ? brand : Ink, FontWeights.Bold, f.Emphasis ? 13 : words ? 10 : 11);
                if (f.Emphasis)
                {
                    label.BorderBrush = value.BorderBrush = brand;
                    label.BorderThickness = value.BorderThickness = new Thickness(0, 1.2, 0, 1.2);
                }
                row.Cells.Add(label);
                row.Cells.Add(value);
                g.Rows.Add(row);
            }
            t.RowGroups.Add(g);
            doc.Blocks.Add(t);
        }

        if (!string.IsNullOrWhiteSpace(r.Notes))
            doc.Blocks.Add(new Paragraph(new Run("ملاحظات: " + r.Notes)) { Margin = new Thickness(0, 14, 0, 0), FontSize = 10.5 });

        if (r.Signatures.Count > 0)
        {
            var t = new Table { CellSpacing = 18, Margin = new Thickness(0, 40, 0, 0) };
            foreach (var _ in r.Signatures) t.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            var g = new TableRowGroup();
            var row = new TableRow();
            foreach (var s in r.Signatures)
                row.Cells.Add(new TableCell(new Paragraph(new Run(s)) { TextAlignment = TextAlignment.Center, Margin = new Thickness(0), FontSize = 10.5, Foreground = Muted })
                { BorderBrush = Muted, BorderThickness = new Thickness(0, 0.75, 0, 0), Padding = new Thickness(4, 6, 4, 0) });
            g.Rows.Add(row);
            t.RowGroups.Add(g);
            doc.Blocks.Add(t);
        }
        return doc;
    }

    /// <summary>عرض نسبي للعمود حسب طول محتواه (1 إلى 4) — بلا أعمدة تلقائية.</summary>
    private static double ColumnWeight(ReportDocument r, int i)
    {
        var lengths = r.Rows.Take(200).Select(row => i < row.Count ? row[i].Length : 0).Append(r.Columns[i].Length);
        var avg = lengths.Average();
        return Math.Clamp(avg / 7.0, 1, 4);
    }

    private static TableCell Cell(string text, Brush brush, FontWeight weight, double size) =>
        new(new Paragraph(new Run(text)) { Margin = new Thickness(0), FontWeight = weight, Foreground = brush, FontSize = size })
        { Padding = new Thickness(3, 3, 3, 3) };

    // ===================================================================
    //                         كاشير حراري 80mm
    // ===================================================================

    /// <summary>إيصال ضيق: شعار مصغّر، الاسم والهاتف، العنوان، الجدول بأعمدة الكاشير فقط، الإجماليات، والتذييل.</summary>
    public static FrameworkElement Receipt(ReportDocument r)
    {
        var b = BrandingOf(r);
        var width = ReceiptWidth - 16;
        var panel = new StackPanel { Width = width, Margin = new Thickness(8), FlowDirection = FlowDirection.RightToLeft };
        var root = new Border { Background = Brushes.White, Child = panel, Width = ReceiptWidth };

        if (LoadLogo(b.Logo) is { } logo)
            panel.Children.Add(new Image { Source = logo, MaxHeight = 46, MaxWidth = 120, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(Centered(b.CompanyName, 14, FontWeights.Bold));
        if (b.Phones.Count > 0) panel.Children.Add(Centered(string.Join(" / ", b.Phones), 9, FontWeights.Normal));
        if (!string.IsNullOrWhiteSpace(b.Address)) panel.Children.Add(Centered(b.Address!, 9, FontWeights.Normal));
        panel.Children.Add(Dashed());
        panel.Children.Add(Centered(r.Title, 12, FontWeights.Bold));
        if (r.Stamp is not null) panel.Children.Add(Centered(r.Stamp, 10, FontWeights.Bold, Danger));
        foreach (var f in r.HeaderFields) panel.Children.Add(Pair(f.Label, f.Value, 9.5, FontWeights.Normal, width));

        var cols = r.ReceiptColumns is { Length: > 0 } rc ? rc.Where(i => i < r.Columns.Count).ToArray()
                 : r.Columns.Count == 0 ? Array.Empty<int>() : Enumerable.Range(0, Math.Min(4, r.Columns.Count)).ToArray();
        if (cols.Length > 0 && r.Rows.Count > 0)
        {
            panel.Children.Add(Dashed());
            var grid = new Grid { Width = width };
            for (var c = 0; c < cols.Length; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(cols.Length >= 4 ? 52 : 64) });
            AddRow(grid, cols.Select(i => r.Columns[i]).ToList(), FontWeights.Bold);
            foreach (var row in r.Rows) AddRow(grid, cols.Select(i => i < row.Count ? row[i] : "").ToList(), FontWeights.Normal);
            panel.Children.Add(grid);
        }
        if (r.Totals.Count > 0)
        {
            panel.Children.Add(Dashed());
            foreach (var t in r.Totals)
            {
                if (t.Value.StartsWith("فقط ")) panel.Children.Add(Centered(t.Value, 8.5, FontWeights.Normal));
                else panel.Children.Add(Pair(t.Label, t.Value, t.Emphasis ? 12 : 10, t.Emphasis ? FontWeights.Bold : FontWeights.Normal, width));
            }
        }
        if (!string.IsNullOrWhiteSpace(r.Notes)) panel.Children.Add(Centered(r.Notes!, 9, FontWeights.Normal));
        panel.Children.Add(Dashed());
        if (!string.IsNullOrWhiteSpace(b.FooterText)) panel.Children.Add(Centered(b.FooterText!, 9.5, FontWeights.SemiBold));
        panel.Children.Add(Centered($"{r.PrintedBy} — {r.PrintedAt:yyyy/MM/dd HH:mm}", 8, FontWeights.Normal, Muted));
        Layout(root, ReceiptWidth);
        return root;
    }

    private static void AddRow(Grid grid, IReadOnlyList<string> values, FontWeight weight)
    {
        var r = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < values.Count; c++)
        {
            var tb = new TextBlock
            {
                Text = values[c], FontSize = 9.5, FontWeight = weight, FontFamily = new FontFamily(Font), Foreground = Ink,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(1, 1, 1, 2), TextAlignment = c == 0 ? TextAlignment.Left : TextAlignment.Right
            };
            Grid.SetRow(tb, r);
            Grid.SetColumn(tb, c);
            grid.Children.Add(tb);
        }
    }

    private static TextBlock Centered(string text, double size, FontWeight weight, Brush? brush = null) => new()
    {
        Text = text, FontSize = size, FontWeight = weight, Foreground = brush ?? Ink, FontFamily = new FontFamily(Font),
        TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1)
    };

    private static FrameworkElement Pair(string label, string value, double size, FontWeight weight, double width)
    {
        var g = new Grid { Width = width, Margin = new Thickness(0, 1, 0, 1) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
        var l = new TextBlock { Text = label, FontSize = size, FontWeight = weight, Foreground = Muted, FontFamily = new FontFamily(Font), TextWrapping = TextWrapping.Wrap };
        var v = new TextBlock { Text = value, FontSize = size, FontWeight = weight, Foreground = Ink, FontFamily = new FontFamily(Font), TextWrapping = TextWrapping.Wrap,
                                TextAlignment = TextAlignment.Right };
        Grid.SetColumn(v, 1);
        g.Children.Add(l);
        g.Children.Add(v);
        return g;
    }

    private static Line Dashed() => new()
    {
        X1 = 0, X2 = ReceiptWidth - 16, Stroke = Muted, StrokeThickness = 0.8, StrokeDashArray = new DoubleCollection { 3, 2 }, Margin = new Thickness(0, 5, 0, 5)
    };
}

/// <summary>
/// يلف تقسيم صفحات المستند ويضيف لكل صفحة الترويسة والتذييل (ومنه "صفحة س من ص").
/// نفس الكائن يُستخدم للمعاينة (عبر XPS) وللطباعة الفعلية، فما يُرى هو ما يُطبع.
/// </summary>
public sealed class BrandedPaginator : DocumentPaginator
{
    private readonly DocumentPaginator _inner;
    private readonly Thickness _margin;
    private readonly double _headerHeight, _footerHeight;
    private readonly Func<FrameworkElement> _header;
    private readonly Func<int, int, FrameworkElement> _footer;
    private Size _pageSize;

    public BrandedPaginator(DocumentPaginator inner, Size pageSize, Thickness margin, double headerHeight, double footerHeight,
                            Func<FrameworkElement> header, Func<int, int, FrameworkElement> footer)
    {
        _inner = inner;
        _pageSize = pageSize;
        _margin = margin;
        _headerHeight = headerHeight;
        _footerHeight = footerHeight;
        _header = header;
        _footer = footer;
        _inner.ComputePageCount();
    }

    private readonly Dictionary<int, DocumentPage> _pages = new();

    /// <summary>الصفحة نفسها لكل طلب (مقسّم المستند يعيد نفس المرئيات، ولا يجوز إضافتها لأبوين).</summary>
    public override DocumentPage GetPage(int pageNumber)
    {
        if (_pages.TryGetValue(pageNumber, out var cached)) return cached;
        var page = _inner.GetPage(pageNumber);
        var root = new ContainerVisual();
        root.Children.Add(new DrawingVisual());   // خلفية بيضاء
        using (var dc = ((DrawingVisual)root.Children[0]).RenderOpen())
            dc.DrawRectangle(Brushes.White, null, new Rect(_pageSize));

        root.Children.Add(Place(_header(), _margin.Left, _margin.Top));
        var body = new ContainerVisual { Transform = new TranslateTransform(_margin.Left, _margin.Top + _headerHeight) };
        body.Children.Add(page.Visual);
        root.Children.Add(body);
        root.Children.Add(Place(_footer(pageNumber + 1, PageCount), _margin.Left, _pageSize.Height - _margin.Bottom - _footerHeight));
        return _pages[pageNumber] = new DocumentPage(root, _pageSize, new Rect(_pageSize),
            new Rect(_margin.Left, _margin.Top, _pageSize.Width - _margin.Left - _margin.Right, _pageSize.Height - _margin.Top - _margin.Bottom));
    }

    /// <summary>
    /// معاينة متجهية من صفحات الطباعة نفسها (لا تحويل لصور ولا إعادة قراءة XPS): ما يُعرض هو ما يُرسل للطابعة.
    /// </summary>
    public FixedDocument ToFixedDocument()
    {
        var doc = new FixedDocument();
        doc.DocumentPaginator.PageSize = _pageSize;
        for (var i = 0; i < PageCount; i++)
        {
            var page = GetPage(i);
            var fixedPage = new FixedPage { Width = _pageSize.Width, Height = _pageSize.Height, Background = Brushes.White };
            fixedPage.Children.Add(new PageVisualHost(page.Visual, _pageSize));
            var content = new PageContent();
            ((System.Windows.Markup.IAddChild)content).AddChild(fixedPage);
            doc.Pages.Add(content);
        }
        return doc;
    }

    private static ContainerVisual Place(Visual v, double x, double y)
    {
        var c = new ContainerVisual { Transform = new TranslateTransform(x, y) };
        c.Children.Add(v);
        return c;
    }

    public override bool IsPageCountValid => _inner.IsPageCountValid;
    public override int PageCount => _inner.PageCount;
    public override Size PageSize { get => _pageSize; set => _pageSize = value; }
    public override IDocumentPaginatorSource? Source => null;
}

/// <summary>يستضيف مرئية صفحة جاهزة داخل صفحة ثابتة (للمعاينة).</summary>
public sealed class PageVisualHost : FrameworkElement
{
    private readonly Visual _visual;
    public PageVisualHost(Visual visual, Size size)
    {
        _visual = visual;
        Width = size.Width;
        Height = size.Height;
        AddVisualChild(visual);
    }
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;
}
