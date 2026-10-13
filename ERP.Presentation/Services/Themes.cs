namespace ERP.Presentation.Services;

/// <summary>
/// مظهر الواجهة (قرار المالك: ستة ألوان مستوحاة من شعار الرحمة). يغيّر اللون الأساسي والقائمة الجانبية والخلفية؛
/// ألوان أيقونات الأقسام ثابتة في كل المظاهر. Glass: القائمة بتدرج شفاف (زجاجي).
/// </summary>
public sealed record ThemeInfo(string Key, string Name, string Description, string Primary, string Sidebar, string SidebarDeep,
                               string SidebarHover, string SidebarSelected, string Background, string Accent, string? SidebarGradientTo = null)
{
    public bool Glass => SidebarGradientTo is not null;
}

public static class Themes
{
    public const string Default = "rahma";

    public static readonly IReadOnlyList<ThemeInfo> All = new[]
    {
        new ThemeInfo("rahma", "الرحمة", "ألوان الشعار: أزرق سماوي كالماء (الافتراضي)", "#2B8FD6", "#1B4F7A", "#153F62", "#24618F", "#2C70A3", "#F2F8FC", "#6FCCF5"),
        new ThemeInfo("water", "أزرق الماء", "أزرق صافٍ كالماء", "#2F6FA7", "#173A5E", "#122F4D", "#21496F", "#2A5A86", "#F1F6FB", "#4A90D0"),
        new ThemeInfo("teal", "فيروزي", "فيروزي مع تمييز أخضر مائي", "#1B7F6E", "#0F4A41", "#0B3B34", "#155A4F", "#1C6B5E", "#F0F7F5", "#33AF97"),
        new ThemeInfo("graphite", "رصاصي", "رمادي رصاصي محايد", "#4B5563", "#1F2937", "#111827", "#374151", "#374151", "#F3F4F6", "#6B7280"),
        new ThemeInfo("aurora", "زجاجي", "تدرج شفاف حديث (Aurora)", "#5B5BD6", "#2A2F6B", "#23285A", "#3A3F85", "#4B4FA0", "#F4F3FB", "#8B7CF6", "#4A2F6B"),
        new ThemeInfo("olive", "زيتوني", "زيتوني طبيعي دافئ", "#5A6B2B", "#2F3A16", "#252E11", "#3C4A1E", "#4A5A25", "#F5F6EF", "#8A9A4A"),
    };

    public static ThemeInfo Get(string? key) => All.FirstOrDefault(t => t.Key == key) ?? All[0];
}

/// <summary>من يطبّق المظهر على الواجهة (WPF في البرنامج؛ لا شيء في الاختبارات).</summary>
public interface IThemeHost
{
    void Apply(ThemeInfo theme);
}

public static class ThemeHost
{
    public static IThemeHost? Current { get; set; }
    public static ThemeInfo Active { get; private set; } = Themes.Get(null);

    public static void Apply(ThemeInfo theme)
    {
        Active = theme;
        Current?.Apply(theme);
    }
}
