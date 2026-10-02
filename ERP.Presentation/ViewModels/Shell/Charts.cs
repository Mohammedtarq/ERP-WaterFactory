using System.Globalization;

namespace ERP.Presentation.ViewModels.Shell;

/// <summary>
/// لوحة الألوان المعتمدة للرسوم (مُتحقَّق منها لعمى الألوان): الترتيب ثابت ولا يُدوَّر،
/// السلسلة الأولى أزرق والثانية برتقالي. النصوص لا تأخذ لون السلسلة أبدًا.
/// </summary>
public static class ChartPalette
{
    public const string Series1 = "#2A78D6";   // أزرق
    public const string Series2 = "#EB6834";   // برتقالي
    public const string Series3 = "#1BAF7A";   // أخضر مائي
    public const double PlotHeight = 150;      // ارتفاع منطقة الرسم (بكسل)
}

/// <summary>عمود واحد داخل فئة (يوم): ارتفاعه محسوب مسبقًا، وتلميح يظهر عند المرور.</summary>
public record ChartBar(decimal Value, double Height, string Color, string Tooltip, double Width);

/// <summary>فئة على المحور الأفقي (يوم مثلًا) تضم عمودًا لكل سلسلة.</summary>
public record ChartCategory(string Label, IReadOnlyList<ChartBar> Bars, string? ValueLabel)
{
    /// <summary>تلميح الفئة كاملة (كل السلاسل) — منطقة المرور أكبر من العمود نفسه.</summary>
    public string Tooltip => Bars.Count == 0 ? Label : Bars[0].Tooltip.Split('\n')[0] + "\n" + string.Join("\n", Bars.Select(b => b.Tooltip.Split('\n')[1]));
}

public record ChartLegendItem(string Name, string Color);

/// <summary>شريط أفقي في رسم الترتيب (أعلى الأصناف، أرصدة الصناديق...).</summary>
public record RankBar(string Label, decimal Value, double Ratio, string ValueText, string Color);

/// <summary>رسم أعمدة لنشاط يومي — سلسلة واحدة أو سلسلتان متجاورتان (وارد/صادر) بمحور واحد.</summary>
public class ColumnChart
{
    public required string Title { get; init; }
    public string Unit { get; init; } = "";
    public IReadOnlyList<ChartCategory> Categories { get; init; } = Array.Empty<ChartCategory>();
    public IReadOnlyList<ChartLegendItem> Legend { get; init; } = Array.Empty<ChartLegendItem>();
    public bool HasLegend => Legend.Count >= 2;
    public string MaxLabel { get; init; } = "";
    public string Summary { get; init; } = "";
    public bool IsEmpty { get; init; }

    /// <summary>
    /// days: التواريخ بالترتيب؛ series: (الاسم، اللون، القيمة لكل يوم). الملصقات انتقائية:
    /// قيمة آخر يوم وأعلى يوم فقط، والتفاصيل في التلميح.
    /// </summary>
    public static ColumnChart Daily(string title, string unit, IReadOnlyList<DateTime> days,
                                    params (string name, string color, Func<DateTime, decimal> value)[] series)
    {
        var values = series.Select(s => days.Select(s.value).ToArray()).ToArray();
        var max = values.SelectMany(v => v).DefaultIfEmpty(0).Max();
        var maxIndex = series.Length == 1 && max > 0 ? Array.IndexOf(values[0], max) : -1;
        var cats = days.Select((d, i) =>
        {
            var bars = series.Select((s, k) =>
            {
                var v = values[k][i];
                var tip = $"{d:dddd yyyy/MM/dd}\n{s.name}: {Fmt(v)} {unit}";
                return new ChartBar(v, max <= 0 ? 0 : Math.Max(v > 0 ? 2 : 0, (double)(v / max) * ChartPalette.PlotHeight), s.color, tip,
                                    series.Length >= 2 ? 11 : 18);
            }).ToList();
            var showLabel = series.Length == 1 && (i == days.Count - 1 || i == maxIndex) && values[0][i] > 0;
            return new ChartCategory(d.ToString("dd/MM", CultureInfo.InvariantCulture), bars, showLabel ? Short(values[0][i]) : null);
        }).ToList();
        var totals = series.Select((s, k) => $"{s.name}: {Fmt(values[k].Sum())}").ToList();
        return new ColumnChart
        {
            Title = title, Unit = unit, Categories = cats,
            Legend = series.Select(s => new ChartLegendItem(s.name, s.color)).ToList(),
            MaxLabel = max > 0 ? $"الأعلى {Fmt(max)} {unit}" : "",
            Summary = $"المجموع خلال {days.Count} يومًا — " + string.Join(" · ", totals) + $" {unit}",
            IsEmpty = max <= 0
        };
    }

    public static string Fmt(decimal v) => v.ToString("#,0.##", CultureInfo.InvariantCulture);

    /// <summary>ملصق مختصر فوق العمود: 1.2م، 350ألف.</summary>
    public static string Short(decimal v) => Math.Abs(v) switch
    {
        >= 1_000_000 => (v / 1_000_000m).ToString("0.#", CultureInfo.InvariantCulture) + "م",
        >= 10_000 => (v / 1_000m).ToString("0", CultureInfo.InvariantCulture) + "ألف",
        _ => Fmt(v)
    };
}

/// <summary>رسم أشرطة أفقية للترتيب (سلسلة واحدة، لون واحد، القيمة عند طرف الشريط).</summary>
public class RankChart
{
    public required string Title { get; init; }
    public IReadOnlyList<RankBar> Bars { get; init; } = Array.Empty<RankBar>();
    public bool IsEmpty => Bars.Count == 0;
    public string EmptyText { get; init; } = "لا توجد بيانات بعد";

    public static RankChart Of(string title, IEnumerable<(string label, decimal value)> rows, string unit, int take = 7, string color = ChartPalette.Series1)
    {
        var list = rows.Where(r => r.value != 0).OrderByDescending(r => r.value).Take(take).ToList();
        var max = list.Select(r => r.value).DefaultIfEmpty(0).Max();
        return new RankChart
        {
            Title = title,
            Bars = list.Select(r => new RankBar(r.label, r.value, max <= 0 ? 0 : Math.Max(0.01, (double)(r.value / max)),
                                                $"{ColumnChart.Fmt(r.value)} {unit}".Trim(), color)).ToList()
        };
    }
}

/// <summary>بطاقة مؤشر رقمي في رأس لوحة القسم.</summary>
public record DashboardTile(string Title, string Value, string Glyph, string Color, string Hint);
