namespace ERP.Presentation;

/// <summary>
/// تفقيط المبالغ بالعربية للإيصالات والسندات: 1,250,000 ← "فقط مليون ومئتان وخمسون ألف دينار عراقي لا غير".
/// </summary>
public static class ArabicNumberWords
{
    private static readonly string[] Ones =
        { "", "واحد", "اثنان", "ثلاثة", "أربعة", "خمسة", "ستة", "سبعة", "ثمانية", "تسعة",
          "عشرة", "أحد عشر", "اثنا عشر", "ثلاثة عشر", "أربعة عشر", "خمسة عشر", "ستة عشر", "سبعة عشر", "ثمانية عشر", "تسعة عشر" };
    private static readonly string[] Tens = { "", "", "عشرون", "ثلاثون", "أربعون", "خمسون", "ستون", "سبعون", "ثمانون", "تسعون" };
    private static readonly string[] Hundreds = { "", "مئة", "مئتان", "ثلاثمئة", "أربعمئة", "خمسمئة", "ستمئة", "سبعمئة", "ثمانمئة", "تسعمئة" };

    // (المفرد، المثنى، الجمع 3-10)
    private static readonly (long value, string one, string two, string plural)[] Scales =
    {
        (1_000_000_000_000, "ترليون", "ترليونان", "ترليونات"),
        (1_000_000_000, "مليار", "ملياران", "مليارات"),
        (1_000_000, "مليون", "مليونان", "ملايين"),
        (1_000, "ألف", "ألفان", "آلاف"),
    };

    public static string Amount(decimal amount, string currency = "دينار عراقي")
    {
        var whole = (long)Math.Floor(Math.Abs(amount));
        var text = whole == 0 ? "صفر" : Words(whole);
        return $"فقط {text} {currency} لا غير";
    }

    public static string Words(long n)
    {
        if (n == 0) return "صفر";
        var parts = new List<string>();
        foreach (var (value, one, two, plural) in Scales)
        {
            var count = n / value;
            if (count == 0) continue;
            n %= value;
            parts.Add(count switch
            {
                1 => one,
                2 => two,
                >= 3 and <= 10 => $"{BelowThousand((int)count)} {plural}",
                _ => $"{BelowThousand((int)count)} {one}"
            });
        }
        if (n > 0) parts.Add(BelowThousand((int)n));
        return string.Join(" و", parts);
    }

    private static string BelowThousand(int n)
    {
        var parts = new List<string>();
        if (n >= 100) { parts.Add(Hundreds[n / 100]); n %= 100; }
        if (n >= 20)
        {
            var o = n % 10;
            parts.Add(o == 0 ? Tens[n / 10] : $"{Ones[o]} و{Tens[n / 10]}");
        }
        else if (n > 0) parts.Add(Ones[n]);
        return string.Join(" و", parts);
    }
}
