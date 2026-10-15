using System.Globalization;

namespace ERP.RepApp;

/// <summary>ألوان وعناصر الواجهة الموحّدة: خط كبير وأزرار كبيرة تناسب العمل في الشارع.</summary>
public static class Ui
{
    /// <summary>أزرق التطبيق (الشريط والأزرار الرئيسية)، وأزرق سماوي للتحصيل.</summary>
    public static readonly Color Primary = Color.FromArgb("#1D4ED8");
    public static readonly Color Blue = Color.FromArgb("#0369A1");
    public static readonly Color Amber = Color.FromArgb("#B45309");
    public static readonly Color Red = Color.FromArgb("#B91C1C");
    public static readonly Color Green = Color.FromArgb("#15803D");
    public static readonly Color Muted = Color.FromArgb("#64748B");
    public static readonly Color Ink = Color.FromArgb("#0F172A");
    public static readonly Color PageBg = Color.FromArgb("#F1F5F9");

    // ألوان هوية النظام (الرئيسية): كحلي للنصوص، وأزرق للأزرار الرئيسية، وأزرق داكن لشريط العنوان
    public static readonly Color Navy = Color.FromArgb("#0F3556");
    public static readonly Color Brand = Color.FromArgb("#2B8FD6");
    public static readonly Color Deep = Color.FromArgb("#1B4F7A");
    public static readonly Color Line = Color.FromArgb("#E5ECF3");

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

    /// <summary>مربع في شبكة الرئيسية: أيقونة في دائرة خفيفة فوق الاسم والوصف، كلها في المنتصف.</summary>
    public static Border Tile(string icon, string title, string hint, Color tint, Func<Task> onTap)
    {
        var tile = new Border
        {
            BackgroundColor = Colors.White, Stroke = Line, StrokeThickness = 1.5, Padding = new Thickness(8, 14), MinimumHeightRequest = 118,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 20 },
            Shadow = new Shadow { Brush = new SolidColorBrush(Navy), Opacity = 0.08f, Radius = 8, Offset = new Point(0, 3) },
            Content = new VerticalStackLayout
            {
                Spacing = 4, HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    new Border
                    {
                        WidthRequest = 52, HeightRequest = 52, HorizontalOptions = LayoutOptions.Center, StrokeThickness = 0,
                        BackgroundColor = tint.WithAlpha(0.14f), StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 },
                        Content = new Label { Text = icon, FontSize = 26, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center }
                    },
                    new Label { Text = title, FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = Navy, HorizontalTextAlignment = TextAlignment.Center },
                    new Label { Text = hint, FontSize = 12, TextColor = Muted, HorizontalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.WordWrap }
                }
            }
        };
        var busy = false;
        tile.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                if (busy) return;
                busy = true;
                try { await onTap(); }
                finally { busy = false; }
            })
        });
        SemanticProperties.SetDescription(tile, title);
        return tile;
    }

    /// <summary>زر «بيع» الكبير: تدرج أزرق، الأيقونة والاسم في المنتصف.</summary>
    public static Border Hero(string icon, string title, string hint, Func<Task> onTap)
    {
        var hero = new Border
        {
            HeightRequest = 132, Margin = new Thickness(0, 4, 0, 8), StrokeThickness = 0, Padding = new Thickness(12),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 24 },
            Background = new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(Color.FromArgb("#1F78C1"), 0f), new GradientStop(Brand, 0.6f), new GradientStop(Color.FromArgb("#4DB5E8"), 1f)
            }, new Point(0, 0), new Point(1, 1)),
            Shadow = new Shadow { Brush = new SolidColorBrush(Brand), Opacity = 0.35f, Radius = 14, Offset = new Point(0, 6) },
            Content = new VerticalStackLayout
            {
                Spacing = 2, VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    new Border
                    {
                        WidthRequest = 46, HeightRequest = 46, HorizontalOptions = LayoutOptions.Center, StrokeThickness = 0,
                        BackgroundColor = Colors.White.WithAlpha(0.2f), StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
                        Content = new Label { Text = icon, FontSize = 24, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center }
                    },
                    new Label { Text = title, FontSize = 30, FontAttributes = FontAttributes.Bold, TextColor = Colors.White, HorizontalTextAlignment = TextAlignment.Center },
                    new Label { Text = hint, FontSize = 13, TextColor = Colors.White.WithAlpha(0.9f), HorizontalTextAlignment = TextAlignment.Center }
                }
            }
        };
        var busy = false;
        hero.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                if (busy) return;
                busy = true;
                try { await onTap(); }
                finally { busy = false; }
            })
        });
        SemanticProperties.SetDescription(hero, title);
        return hero;
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
