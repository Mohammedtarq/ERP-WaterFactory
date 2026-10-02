namespace ERP.Presentation.Services;

/// <summary>
/// مستند قابل للطباعة مستقل عن WPF: الـ ViewModel يبنيه، وتنفيذ IDialogService
/// يعرضه (معاينة + طباعة في Desktop، تسجيل في الاختبارات).
/// </summary>
public class ReportDocument
{
    public required string CompanyName { get; init; }
    public required string Title { get; init; }
    /// <summary>ختم يظهر بجانب العنوان (مثل "مسودة" أو "بيع مجاني").</summary>
    public string? Stamp { get; init; }
    public List<ReportField> HeaderFields { get; } = new();
    public List<string> Columns { get; } = new();
    public List<IReadOnlyList<string>> Rows { get; } = new();
    public List<ReportField> Totals { get; } = new();
    public string? Notes { get; init; }
    /// <summary>أسماء خانات التوقيع أسفل المستند.</summary>
    public List<string> Signatures { get; } = new();
    public string PrintedBy { get; init; } = "";
    public DateTime PrintedAt { get; init; } = DateTime.Now;

    private string? _key;
    /// <summary>
    /// نوع المستند لحفظ تفضيلات الطباعة (نوع الطابعة، الأعمدة المخفية). افتراضيًا العنوان قبل أي " — ".
    /// </summary>
    public string Key { get => _key ?? Title.Split(" — ")[0].Trim(); init => _key = value; }

    /// <summary>يصلح لطابعة الكاشير الحرارية 80mm (فواتير، إيصالات الصندوق، السندات).</summary>
    public bool ReceiptCapable { get; init; }

    /// <summary>أعمدة الجدول المطبوعة على الكاشير (فهارس في Columns) — مثل: الصنف، الكمية، السعر، الإجمالي.</summary>
    public int[]? ReceiptColumns { get; init; }

    /// <summary>هوية الطباعة؛ null = الهوية المركزية الحالية (ReportBranding.Current).</summary>
    public ReportBranding? Branding { get; init; }

    /// <summary>
    /// نسخة للطباعة بعد إخفاء أعمدة واختياريًا إخفاء الصفوف الصفرية/الفارغة (كل خلاياها الرقمية صفر أو فارغة).
    /// </summary>
    public ReportDocument WithVisibility(ISet<int> hiddenColumns, bool hideZeroRows)
    {
        var copy = new ReportDocument
        {
            CompanyName = CompanyName, Title = Title, Stamp = Stamp, Notes = Notes, PrintedBy = PrintedBy, PrintedAt = PrintedAt,
            Key = Key, ReceiptCapable = ReceiptCapable, Branding = Branding,
            ReceiptColumns = ReceiptColumns?.Where(i => !hiddenColumns.Contains(i)).Select(i => i - hiddenColumns.Count(h => h < i)).ToArray()
        };
        copy.HeaderFields.AddRange(HeaderFields);
        copy.Totals.AddRange(Totals);
        copy.Signatures.AddRange(Signatures);
        var keep = Enumerable.Range(0, Columns.Count).Where(i => !hiddenColumns.Contains(i)).ToList();
        copy.Columns.AddRange(keep.Select(i => Columns[i]));
        foreach (var row in Rows)
        {
            if (hideZeroRows && IsZeroRow(row)) continue;
            copy.Rows.Add(keep.Select(i => i < row.Count ? row[i] : "").ToList());
        }
        return copy;
    }

    /// <summary>صف بلا أي رقم غير الصفر (كل كمياته/مبالغه صفر أو فارغة) — يتجاهل عمود الترقيم "#".</summary>
    public bool IsZeroRow(IReadOnlyList<string> row)
    {
        var values = Enumerable.Range(0, Math.Min(row.Count, Columns.Count)).Where(i => Columns[i] != "#").Select(i => row[i]).ToList();
        var hasNonZero = values.Any(v => ParseNumber(v) is decimal d && d != 0);
        var hasValueCell = values.Any(v => ParseNumber(v) is not null || string.IsNullOrWhiteSpace(v));
        return !hasNonZero && hasValueCell;
    }

    public static bool IsNumber(string s) => ParseNumber(s) is not null;

    public static decimal? ParseNumber(string s)
    {
        var t = (s ?? "").Replace(",", "").Replace("د.ع", "").Replace("قطعة", "").Trim();
        return decimal.TryParse(t, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    public ReportDocument Field(string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) HeaderFields.Add(new ReportField(label, value));
        return this;
    }

    public ReportDocument Total(string label, string value, bool emphasis = false)
    {
        Totals.Add(new ReportField(label, value, emphasis));
        return this;
    }
}

public record ReportField(string Label, string Value, bool Emphasis = false);
