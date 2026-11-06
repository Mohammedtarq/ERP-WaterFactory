namespace ERP.Presentation.Services;

/// <summary>المتوفر بجانب الصنف في قوائم الاختيار: العدد فقط بعبوة الصنف الكبرى («120»)، و«قليل» عند حد التنبيه، و«نفد» عند الصفر.</summary>
public static class StockHint
{
    public static (string Hint, string State) Of(decimal pieces, decimal topPackPieces, decimal? alertLevel)
    {
        if (pieces <= 0) return ("نفد", "out");
        var count = Math.Floor(pieces / (topPackPieces > 0 ? topPackPieces : 1));
        var text = (count > 0 ? count : pieces).ToString("N0");
        var low = alertLevel is decimal min && min > 0 && pieces <= min;
        return (low ? $"{text} · قليل" : text, low ? "low" : "ok");
    }
}
