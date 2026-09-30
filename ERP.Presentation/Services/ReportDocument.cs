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
