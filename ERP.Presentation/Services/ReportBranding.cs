using ERP.Data.Services;

namespace ERP.Presentation.Services;

/// <summary>
/// هوية الطباعة المركزية كما تُستخدم في الرسم (رأس وتذييل كل مستند). تُحمَّل عند فتح المشروع
/// وبعد حفظ شاشة الهوية؛ قبل ضبطها يُستخدم اسم المشروع ولون الواجهة.
/// </summary>
public sealed class ReportBranding
{
    public const string DefaultColor = "#0F766E";    // تركواز الواجهة
    public const string InkColor = "#0F172A";        // كحلي النصوص

    public string CompanyName { get; init; } = "";
    public string? CompanyNameEn { get; init; }
    public IReadOnlyList<string> Phones { get; init; } = Array.Empty<string>();
    public string? Address { get; init; }
    public string? TaxNumber { get; init; }
    public string? CommercialRegister { get; init; }
    public string BrandColor { get; init; } = DefaultColor;
    public string? FooterText { get; init; }
    public byte[]? Logo { get; init; }

    /// <summary>سطر الاتصال المختصر: الهواتف · العنوان.</summary>
    public string ContactLine => string.Join("  ·  ", new[] { string.Join(" / ", Phones), Address ?? "" }.Where(s => s.Length > 0));
    public string RegistrationLine => string.Join("  ·  ", new[]
    {
        TaxNumber is null ? null : $"الرقم الضريبي: {TaxNumber}",
        CommercialRegister is null ? null : $"السجل التجاري: {CommercialRegister}"
    }.Where(s => s is not null));

    private static ReportBranding? _current;
    public static ReportBranding Current
    {
        get => _current ?? new ReportBranding { CompanyName = "" };
        set => _current = value;
    }

    public static ReportBranding Fallback(string projectName) => new() { CompanyName = projectName };

    public static async Task<ReportBranding> LoadAsync(AppSession session)
    {
        await using var db = session.NewDb();
        var p = await new CompanyProfileService(db).GetAsync();
        return p is null ? Fallback(session.ProjectName) : new ReportBranding
        {
            CompanyName = p.NameAr, CompanyNameEn = p.NameEn,
            Phones = (p.Phones ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Address = p.Address, TaxNumber = p.TaxNumber, CommercialRegister = p.CommercialRegister,
            BrandColor = p.BrandColor, FooterText = p.FooterText, Logo = p.Logo
        };
    }

    public static async Task RefreshAsync(AppSession session) => Current = await LoadAsync(session);
}
