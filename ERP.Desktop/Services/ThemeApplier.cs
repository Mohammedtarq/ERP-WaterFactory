using System.Windows;
using System.Windows.Media;
using ERP.Presentation.Services;

namespace ERP.Desktop.Services;

/// <summary>
/// يطبّق مظهر المستخدم على الواجهة فورًا: يستبدل فُرَش الألوان العامة (الأساسي، القائمة الجانبية وأشرطتها، الخلفية، التمييز)
/// فتتحدث كل الشاشات المفتوحة (تستعملها بـ DynamicResource). المظهر الزجاجي: تدرج للقائمة الجانبية.
/// </summary>
public class ThemeApplier : IThemeHost
{
    public void Apply(ThemeInfo theme)
    {
        var app = Application.Current;
        if (app is null) return;
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.Invoke(() => Apply(theme)); return; }
        var r = app.Resources;
        r["PrimaryBrush"] = Brush(theme.Primary);
        r["SidebarBrush"] = theme.SidebarGradientTo is { } to
            ? new LinearGradientBrush(Color(theme.Sidebar), Color(to), new Point(0, 0), new Point(0.4, 1))
            : Brush(theme.Sidebar);
        r["SidebarDeepBrush"] = Brush(theme.SidebarDeep);
        r["SidebarHoverBrush"] = Brush(theme.SidebarHover);
        r["SidebarSelectedBrush"] = Brush(theme.SidebarSelected);
        r["SidebarChipBrush"] = Brush(theme.SidebarHover);
        r["BackgroundBrush"] = Brush(theme.Background);
        r["AccentBrush"] = Brush(theme.Accent);
    }

    private static Color Color(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush(Color(hex));
        b.Freeze();
        return b;
    }
}
