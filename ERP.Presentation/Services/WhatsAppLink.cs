using System.Globalization;
using System.Text;

namespace ERP.Presentation.Services;

/// <summary>
/// إرسال كشف الحساب على WhatsApp بلا اشتراك: رابط wa.me برقم الطرف ونص جاهز يفتح المحادثة،
/// والمستخدم يضغط «إرسال» (ويرفق PDF الكشف من الطباعة إن أراد).
/// </summary>
public static class WhatsAppLink
{
    /// <summary>
    /// رقم عراقي بالصيغة الدولية بلا +: ‎07701234567 أو 7701234567 أو ‎+964 770 123 4567 أو 00964… ← 9647701234567.
    /// NULL إن كان الحقل فارغًا أو غير صالح.
    /// </summary>
    public static string? NormalizeIraqPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        // أول رقم فقط إن كُتب أكثر من رقم، مع تحويل الأرقام العربية الهندية
        var first = phone.Split(new[] { ',', '،', '/', ';' }, StringSplitOptions.RemoveEmptyEntries)[0];
        var digits = new StringBuilder();
        foreach (var ch in first)
            if (char.IsDigit(ch)) digits.Append((int)char.GetNumericValue(ch));
        var d = digits.ToString();
        if (d.StartsWith("00")) d = d[2..];
        if (d.StartsWith("964")) d = d[3..];
        if (d.StartsWith('0')) d = d[1..];
        return d.Length == 10 && d[0] == '7' ? "964" + d : null;
    }

    public static string Build(string internationalPhone, string text) =>
        $"https://wa.me/{internationalPhone}?text={Uri.EscapeDataString(text)}";

    /// <summary>نص الكشف: التحية، الرصيد، وآخر الحركات (الأحدث أولًا).</summary>
    public static string StatementText(string company, string party, string balanceLine,
                                       IEnumerable<(DateTime date, string doc, decimal amount)> lastMovements, string? companyPhones)
    {
        var ar = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"السلام عليكم {party}");
        sb.AppendLine($"كشف حسابكم لدى {company} حتى {DateTime.Today:yyyy/MM/dd}:");
        sb.AppendLine(balanceLine);
        var moves = lastMovements.ToList();
        if (moves.Count > 0)
        {
            sb.AppendLine("آخر الحركات:");
            foreach (var (date, doc, amount) in moves)
                sb.AppendLine($"• {date.ToString("yyyy/MM/dd", ar)} {doc}: {amount.ToString("N0", ar)} د.ع");
        }
        if (!string.IsNullOrWhiteSpace(companyPhones)) sb.AppendLine($"للاستفسار: {companyPhones.Trim()}");
        sb.Append("شكرًا لتعاملكم معنا.");
        return sb.ToString();
    }
}
