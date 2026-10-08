using System.Globalization;

namespace ERP.RepApp;

/// <summary>ألوان وعناصر الواجهة الموحّدة: خط كبير وأزرار كبيرة تناسب العمل في الشارع.</summary>
public static class Ui
{
    public static readonly Color Primary = Color.FromArgb("#0F766E");
    public static readonly Color Blue = Color.FromArgb("#1D4ED8");
    public static readonly Color Amber = Color.FromArgb("#B45309");
    public static readonly Color Red = Color.FromArgb("#B91C1C");
    public static readonly Color Green = Color.FromArgb("#15803D");
    public static readonly Color Muted = Color.FromArgb("#64748B");
    public static readonly Color Ink = Color.FromArgb("#0F172A");
    public static readonly Color PageBg = Color.FromArgb("#F1F5F9");

    public static Label Title(string text) => new() { Text = text, FontSize = 22, FontAttributes = FontAttributes.Bold, TextColor = Ink };
    public static Label Text(string text = "", double size = 16, Color? color = null) =>
        new() { Text = text, FontSize = size, TextColor = color ?? Ink, LineBreakMode = LineBreakMode.WordWrap };

    public static Button Big(string text, Color color, Func<Task> onClick)
    {
        var b = new Button { Text = text, FontSize = 20, FontAttributes = FontAttributes.Bold, HeightRequest = 64, CornerRadius = 14,
                             BackgroundColor = color, TextColor = Colors.White, Margin = new Thickness(0, 4) };
        b.Clicked += async (_, _) =>
        {
            b.IsEnabled = false;
            try { await onClick(); }
            finally { b.IsEnabled = true; }
        };
        return b;
    }

    public static Button Small(string text, Color color, Action onClick)
    {
        var b = new Button { Text = text, FontSize = 16, HeightRequest = 48, CornerRadius = 10, BackgroundColor = color, TextColor = Colors.White, Padding = new Thickness(14, 0) };
        b.Clicked += (_, _) => onClick();
        return b;
    }

    public static Border Card(View content, Color? accent = null) => new()
    {
        Content = content, Padding = 14, Margin = new Thickness(0, 6), BackgroundColor = Colors.White,
        Stroke = accent ?? Color.FromArgb("#E2E8F0"), StrokeThickness = accent is null ? 1 : 2,
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 }
    };

    /// <summary>صفحة قابلة للتمرير باتجاه عربي.</summary>
    public static void Setup(ContentPage page, string title, View body)
    {
        page.Title = title;
        page.FlowDirection = FlowDirection.RightToLeft;
        page.BackgroundColor = PageBg;
        page.Content = new ScrollView { Content = new VerticalStackLayout { Padding = 16, Spacing = 4, Children = { body } } };
    }

    /// <summary>رقم من لوحة المفاتيح (تقبل الأرقام العربية ٠١٢ والفارسية ۰۱۲).</summary>
    public static decimal? Number(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = new string(text.Trim().Select(c => c switch
        {
            >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
            >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
            '٫' => '.',
            '٬' or ',' => '\0',
            _ => c
        }).Where(c => c != '\0').ToArray());
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
