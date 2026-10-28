using System.Globalization;
using System.Text.RegularExpressions;

namespace ERP.Cloud.Contracts;

/// <summary>
/// اسم المنتج الموحد (قرار المالك) في كل الشاشات والتقارير والوصل وتطبيق المندوب:
/// «{العبوة} {عدد القطع}×{الحجم} {المتغير}» — كارتون 40×330 مطعم داون تاون، كارتون 40×330 الرحمة، شرنك 20×330 زواج سعيد.
/// القطعة وحدها: «قطعة 330 الرحمة». الحجم مجهول: «كارتون 40 — {اسم الصنف} {المتغير}».
/// لا يُجمع منتجان مختلفان في رقم واحد: كل (منتج، عبوة، متغير) سطر مستقل.
/// </summary>
public static partial class ProductName
{
    public static string Of(string levelName, decimal piecesPerPack, decimal? sizeMl, string itemName, string? variant)
    {
        var v = string.IsNullOrWhiteSpace(variant) ? "" : " " + variant.Trim();
        var level = levelName.Trim();
        if (sizeMl is decimal ml && ml > 0)
            return piecesPerPack > 1 ? $"{level} {Num(piecesPerPack)}×{Size(ml)}{v}" : $"{level} {Size(ml)}{v}";
        return piecesPerPack > 1 ? $"{level} {Num(piecesPerPack)} — {itemName.Trim()}{v}" : $"{level} — {itemName.Trim()}{v}";
    }

    /// <summary>
    /// أجزاء الاسم الموحد للأعمدة المنفصلة (العبوة | المحتوى | المتغير) — تمنع تكسّر الاسم الطويل في الجداول.
    /// المحتوى «20×330» أو «330» للقطعة؛ والحجم المجهول يُعرض «20».
    /// </summary>
    public static (string Pack, string Content, string Variant) Parts(string levelName, decimal piecesPerPack, decimal? sizeMl, string? variant)
    {
        var size = sizeMl is decimal ml && ml > 0 ? Size(ml) : "";
        var content = piecesPerPack > 1 ? (size.Length > 0 ? $"{Num(piecesPerPack)}×{size}" : Num(piecesPerPack)) : size;
        return (levelName.Trim(), content, variant?.Trim() ?? "");
    }

    /// <summary>
    /// اسم المتغير: المختصر، ثم اسم الزبون (المحجوز له)، ثم اسم الطلب الخاص؛ والأساسي باسمه المضبوط في «هوية الطباعة»
    /// (مثل «الرحمة»)، أو بلا اسم إن لم يُضبط.
    /// </summary>
    public static string Variant(string? shortName, string? customerName, string? recipeName, bool isBase, string? baseVariantName) =>
        isBase ? baseVariantName?.Trim() ?? ""
        : FirstNonEmpty(shortName, customerName, recipeName) ?? "طلب خاص";

    /// <summary>330 ← 330، و1500 ← 1.5 لتر.</summary>
    public static string Size(decimal ml) => ml >= 1000 ? $"{Num(ml / 1000)} لتر" : Num(ml);

    /// <summary>الحجم من اسم الصنف إن لم يُضبط: «ماء 330 مل» ← 330، «قنينة 1.5 لتر» ← 1500، «ماء 600» ← 600.</summary>
    public static decimal? ParseSizeMl(string? itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return null;
        var text = itemName.Replace('٫', '.').Replace(',', '.');
        text = new string(text.Select(c => c is >= '٠' and <= '٩' ? (char)('0' + (c - '٠')) : c).ToArray());
        var m = WithUnit().Match(text);
        if (m.Success && decimal.TryParse(m.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) && n > 0)
            return m.Groups[2].Value.StartsWith("ل") || m.Groups[2].Value.Equals("l", StringComparison.OrdinalIgnoreCase) ? n * 1000 : n;
        foreach (Match bare in Bare().Matches(text))
            if (decimal.TryParse(bare.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var b) && b is >= 100 and <= 20000)
                return b;
        return null;
    }

    private static string Num(decimal n) => n.ToString("0.##", CultureInfo.InvariantCulture);

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(مل|ملي|ml|لتر|ليتر|l)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex WithUnit();

    [GeneratedRegex(@"(?<![\d.])\d{3,5}(?![\d.])")]
    private static partial Regex Bare();
}
