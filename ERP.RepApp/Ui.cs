using System.Globalization;

namespace ERP.RepApp;

/// <summary>ألوان وعناصر الواجهة الموحّدة: خط كبير وأزرار كبيرة تناسب العمل في الشارع.</summary>
public static class Ui
{
    /// <summary>
    /// هوية التطبيق: أزرق بحري. الفاتح هو الأصل، و«شمس» خيار من الإعدادات (حدود وألوان أقوى لوضوح أفضل تحت الضوء المباشر).
    /// الألوان حقول تُبدَّل بـ ApplyTheme قبل بناء الصفحات.
    /// </summary>
    public static Color Primary = Color.FromArgb("#2563EB");
    public static Color Blue = Color.FromArgb("#2563EB");
    public static Color Amber = Color.FromArgb("#B45309");
    public static Color Red = Color.FromArgb("#C23B3B");
    public static Color Green = Color.FromArgb("#1E8E5A");
    public static Color Muted = Color.FromArgb("#587380");
    public static Color Ink = Color.FromArgb("#0E2A36");
    public static Color PageBg = Color.FromArgb("#EFF4FF");
    public static Color Navy = Color.FromArgb("#0E2A36");
    public static Color Brand = Color.FromArgb("#2563EB");
    public static Color Deep = Color.FromArgb("#2563EB");
    public static Color Line = Color.FromArgb("#C9D8F5");
    public static Color Chip = Color.FromArgb("#DBE7FE");
    /// <summary>ألوان الأزرار الستة بمعانيها: تحصيل، زبون جديد، رصيد السيارة، مصروف، مزامنة، حركاتي.</summary>
    public static Color Collect = Color.FromArgb("#17874F"), NewCustomer = Color.FromArgb("#0A8AA6"), Stock = Color.FromArgb("#5B3FD0"),
                        Expense = Color.FromArgb("#E06E00"), SyncColor = Color.FromArgb("#2563EB"), Moves = Color.FromArgb("#A8692E");
    /// <summary>سماكة الحدود: 1 عادي، 2.5 في وضع الشمس.</summary>
    public static double BorderW = 1;
    public static bool Sun { get; private set; }

    public const string ThemeKey = "Theme";

    public static void ApplyTheme(bool sun)
    {
        Sun = sun;
        if (sun)
        {
            (Primary, Blue, Amber, Red, Green, Muted, Ink, PageBg) = (C("#00546F"), C("#00546F"), C("#7A4B00"), C("#B00020"), C("#07703A"), C("#1B1B1B"), C("#000000"), C("#FFFFFF"));
            (Navy, Brand, Deep, Line, Chip) = (C("#000000"), C("#00546F"), C("#00546F"), C("#000000"), C("#CDEEF8"));
            (Collect, NewCustomer, Stock, Expense, SyncColor, Moves) = (C("#07703A"), C("#00697F"), C("#4B2FB0"), C("#A04A00"), C("#0B4DA0"), C("#7A4A1A"));
            BorderW = 2.5;
        }
        else
        {
            (Primary, Blue, Amber, Red, Green, Muted, Ink, PageBg) = (C("#2563EB"), C("#2563EB"), C("#B45309"), C("#C23B3B"), C("#1E8E5A"), C("#587380"), C("#0E2A36"), C("#EFF4FF"));
            (Navy, Brand, Deep, Line, Chip) = (C("#0E2A36"), C("#2563EB"), C("#2563EB"), C("#C9D8F5"), C("#DBE7FE"));
            (Collect, NewCustomer, Stock, Expense, SyncColor, Moves) = (C("#17874F"), C("#0A8AA6"), C("#5B3FD0"), C("#E06E00"), C("#2563EB"), C("#A8692E"));
            BorderW = 1;
        }
        static Color C(string hex) => Color.FromArgb(hex);
    }

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

    // أيقونات خطية موحدة (24×24): السماكة والنهايات المدوّرة نفسها في كل مكان، وتتلوّن بلون مربعها
    private static readonly Dictionary<string, string> Icons = new()
    {
        ["coin"] = "M 12 3 a 9 9 0 1 0 0 18 a 9 9 0 0 0 0 -18 z M 12 7 v 10 M 9.5 9.5 c 0 -1 1 -1.5 2.5 -1.5 s 2.5 0.6 2.5 1.7 c 0 2.3 -5 1.2 -5 3.6 c 0 1.1 1.1 1.7 2.5 1.7 s 2.5 -0.6 2.5 -1.5",
        ["user"] = "M 10 11 a 4 4 0 1 0 0 -8 a 4 4 0 0 0 0 8 z M 2 21 c 0 -4 3.5 -6 8 -6 M 19 8 v 6 M 16 11 h 6",
        ["wrench"] = "M 14.7 6.3 a 4 4 0 0 0 -5.4 5.4 L 3 18 l 3 3 l 6.3 -6.3 a 4 4 0 0 0 5.4 -5.4 l -2.6 2.6 l -2.4 -0.6 l -0.6 -2.4 z",
        ["box"] = "M 21 8 l -9 -5 l -9 5 l 9 5 z M 3 8 v 8 l 9 5 l 9 -5 V 8 M 12 13 v 8",
        ["list"] = "M 6 3 h 12 a 2 2 0 0 1 2 2 v 14 a 2 2 0 0 1 -2 2 H 6 a 2 2 0 0 1 -2 -2 V 5 a 2 2 0 0 1 2 -2 z M 8 8 h 8 M 8 12 h 8 M 8 16 h 5",
        ["sync"] = "M 21 12 a 9 9 0 0 0 -15 -6.7 L 3 8 M 3 3 v 5 h 5 M 3 12 a 9 9 0 0 0 15 6.7 L 21 16 M 21 21 v -5 h -5",
        ["home"] = "M 3 9 l 9 -7 l 9 7 v 11 a 2 2 0 0 1 -2 2 H 5 a 2 2 0 0 1 -2 -2 z M 9 22 V 12 h 6 v 10",
        ["users"] = "M 16 21 v -2 a 4 4 0 0 0 -4 -4 H 6 a 4 4 0 0 0 -4 4 v 2 M 9 3 a 4 4 0 1 0 0 8 a 4 4 0 0 0 0 -8 z M 22 21 v -2 a 4 4 0 0 0 -3 -3.87 M 16 3.13 a 4 4 0 0 1 0 7.75",
        ["cal"] = "M 5 4 h 14 a 2 2 0 0 1 2 2 v 14 a 2 2 0 0 1 -2 2 H 5 a 2 2 0 0 1 -2 -2 V 6 a 2 2 0 0 1 2 -2 z M 16 2 v 4 M 8 2 v 4 M 3 10 h 18",
        ["bell"] = "M 6 8 a 6 6 0 0 1 12 0 c 0 7 3 9 3 9 H 3 s 3 -2 3 -9 M 10.3 21 a 1.94 1.94 0 0 0 3.4 0",
        ["power"] = "M 12 2 v 10 M 18.4 6.6 a 9 9 0 1 1 -12.77 0.04",
        ["sliders"] = "M 21 4 h -7 M 10 4 H 3 M 21 12 h -9 M 8 12 H 3 M 21 20 h -5 M 12 20 H 3 M 14 2 v 4 M 8 10 v 4 M 16 18 v 4",
        ["cash"] = "M 4 6 h 16 a 2 2 0 0 1 2 2 v 8 a 2 2 0 0 1 -2 2 H 4 a 2 2 0 0 1 -2 -2 V 8 a 2 2 0 0 1 2 -2 z M 12 9 a 3 3 0 1 0 0 6 a 3 3 0 0 0 0 -6 z"
    };

    /// <summary>أيقونة خطية بالاسم: coin، user، wrench، box، list، sync، cash، home، users، cal، bell، power، sliders.</summary>
    public static View Icon(string key, Color color, double size)
    {
        if (!Icons.TryGetValue(key, out var data)) return new BoxView { WidthRequest = size, HeightRequest = size, Color = Colors.Transparent };
        return new Microsoft.Maui.Controls.Shapes.Path
        {
            Data = (Microsoft.Maui.Controls.Shapes.Geometry)new Microsoft.Maui.Controls.Shapes.PathGeometryConverter().ConvertFromInvariantString(data)!,
            Stroke = new SolidColorBrush(color), StrokeThickness = 1.8, Fill = null,
            StrokeLineCap = Microsoft.Maui.Controls.Shapes.PenLineCap.Round, StrokeLineJoin = Microsoft.Maui.Controls.Shapes.PenLineJoin.Round,
            Aspect = Stretch.Uniform, WidthRequest = size, HeightRequest = size, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center
        };
    }

    /// <summary>مربع في شبكة الرئيسية: أيقونة في دائرة خفيفة فوق الاسم والوصف، كلها في المنتصف.</summary>
    public static Border Tile(string icon, string title, string hint, Color tint, Func<Task> onTap)
    {
        var tile = new Border
        {
            BackgroundColor = Colors.White, Stroke = Line, StrokeThickness = Math.Max(1.5, BorderW), Padding = new Thickness(8, 14), MinimumHeightRequest = 118,
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
                        Content = Icon(icon, tint, 26)
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
                new GradientStop(Brand, 0f), new GradientStop(Sun ? Brand : Color.FromArgb("#1D4ED8"), 1f)
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
                        Content = Icon(icon, Colors.White, 24)
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
        Stroke = accent ?? Line, StrokeThickness = accent is null ? BorderW : Math.Max(2, BorderW),
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 }
    };


    /// <summary>مربع ملوّن في شبكة الرئيسية: الأيقونة والاسم بالأبيض على لون الخدمة.</summary>
    public static Border ColorTile(string icon, string title, Color color, Func<Task> onTap)
    {
        var tile = new Border
        {
            BackgroundColor = color, StrokeThickness = 0, Padding = new Thickness(6, 10), HeightRequest = 108,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 20 },
            Shadow = new Shadow { Brush = new SolidColorBrush(color), Opacity = 0.3f, Radius = 8, Offset = new Point(0, 4) },
            Content = new VerticalStackLayout
            {
                Spacing = 8, HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    new Border
                    {
                        WidthRequest = 46, HeightRequest = 46, HorizontalOptions = LayoutOptions.Center, StrokeThickness = 0,
                        BackgroundColor = Colors.White.WithAlpha(0.22f), StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
                        Content = Icon(icon, Colors.White, 24)
                    },
                    new Label { Text = title, FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = Colors.White, HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Start, HeightRequest = 38 }
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

    /// <summary>شريط أعلى الصفحات الرئيسية: العنوان (اسم المعمل) وسطر ثانٍ، وزر خروج وإعدادات اختياريان.</summary>
    public static View Header(string title, string? sub, Func<Task>? onLogout = null, Func<Task>? onSettings = null)
    {
        var grid = new Grid
        {
            BackgroundColor = Deep, Padding = new Thickness(16, 14, 16, 16), ColumnSpacing = 8,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) }
        };
        var text = new VerticalStackLayout
        {
            VerticalOptions = LayoutOptions.Center, Spacing = 1,
            Children = { new Label { Text = title, FontSize = 19, FontAttributes = FontAttributes.Bold, TextColor = Colors.White, LineBreakMode = LineBreakMode.TailTruncation } }
        };
        if (!string.IsNullOrWhiteSpace(sub)) text.Children.Add(new Label { Text = sub, FontSize = 13, TextColor = Colors.White.WithAlpha(0.9f) });
        grid.Children.Add(text);
        void Put(View v, int col) { Grid.SetColumn(v, col); grid.Children.Add(v); }
        if (onSettings is not null) Put(HeaderButton("sliders", null, onSettings, "الإعدادات"), 1);
        if (onLogout is not null) Put(HeaderButton("power", null, onLogout, "خروج"), 2);
        return grid;
    }

    private static View HeaderButton(string icon, string? label, Func<Task> onTap, string description)
    {
        var row = new HorizontalStackLayout { Spacing = 6, VerticalOptions = LayoutOptions.Center, Children = { Icon(icon, Colors.White, 20) } };
        if (label is not null) row.Children.Add(new Label { Text = label, FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = Colors.White, VerticalOptions = LayoutOptions.Center });
        var b = new Border
        {
            Padding = new Thickness(11, 8), StrokeThickness = 1.5, Stroke = Colors.White.WithAlpha(0.6f), VerticalOptions = LayoutOptions.Center,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 }, Content = row
        };
        var busy = false;
        b.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () => { if (busy) return; busy = true; try { await onTap(); } finally { busy = false; } })
        });
        SemanticProperties.SetDescription(b, description);
        return b;
    }

    /// <summary>الشريط السفلي: الرئيسية، الديون، شهري، التبليغات (وعليها عدد غير المقروء). الضغط يبدّل الصفحة الجذرية.</summary>
    public static View NavBar(string active, int unread)
    {
        var items = AppServices.Instance.Work.CustodyOnly
            ? new (string Key, string Icon, string Text)[] { ("home", "home", "الرئيسية"), ("expenses", "list", "مصاريفي"), ("notices", "bell", "التبليغات") }
            : new (string Key, string Icon, string Text)[] { ("home", "home", "الرئيسية"), ("debts", "users", "الديون"), ("month", "cal", "شهري"), ("notices", "bell", "التبليغات") };
        var grid = new Grid { BackgroundColor = Colors.White, Padding = new Thickness(6, 8, 6, 10) };
        for (var i = 0; i < items.Length; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (var i = 0; i < items.Length; i++)
        {
            var (key, icon, text) = items[i];
            var on = key == active;
            var color = on ? Brand : Muted;
            var cell = new VerticalStackLayout
            {
                Spacing = 2, HorizontalOptions = LayoutOptions.Center,
                Children =
                {
                    Icon(icon, color, 24),
                    new Label { Text = text, FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = color, HorizontalTextAlignment = TextAlignment.Center }
                }
            };
            View view = cell;
            if (key == "notices" && unread > 0)
            {
                var badge = new Border
                {
                    BackgroundColor = Red, StrokeThickness = 0, Padding = new Thickness(5, 0), HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.Start,
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 9 },
                    Content = new Label { Text = unread > 99 ? "99+" : unread.ToString(), FontSize = 11, FontAttributes = FontAttributes.Bold, TextColor = Colors.White }
                };
                view = new Grid { Children = { cell, badge }, HorizontalOptions = LayoutOptions.Center, WidthRequest = 70 };
            }
            if (!on)
                view.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => App.ShowRoot(Pages.MainPages.Create(key))) });
            Grid.SetColumn(view, i);
            grid.Children.Add(view);
        }
        return new VerticalStackLayout { Spacing = 0, Children = { new BoxView { HeightRequest = BorderW, Color = Line }, grid } };
    }

    /// <summary>صفحة رئيسية بشريط علوي وشريط سفلي ثابت: الشريط العلوي والمحتوى القابل للتمرير والشريط السفلي.</summary>
    public static void SetupMain(ContentPage page, View header, View body, string active, int unread)
    {
        NavigationPage.SetHasNavigationBar(page, false);
        page.FlowDirection = FlowDirection.RightToLeft;
        page.BackgroundColor = PageBg;
        var scroll = new ScrollView { Content = new VerticalStackLayout { Padding = new Thickness(14, 12), Spacing = 10, Children = { body } } };
        var nav = NavBar(active, unread);
        var grid = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) } };
        Grid.SetRow(header, 0); Grid.SetRow(scroll, 1); Grid.SetRow(nav, 2);
        grid.Children.Add(header); grid.Children.Add(scroll); grid.Children.Add(nav);
        page.Content = grid;
    }

    /// <summary>شارة صغيرة ملوّنة (عمر الدين، خاص...).</summary>
    public static Border Pill(string text, Color back, Color fore) => new()
    {
        BackgroundColor = back, StrokeThickness = 0, Padding = new Thickness(8, 2), VerticalOptions = LayoutOptions.Center,
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
        Content = new Label { Text = text, FontSize = 11.5, FontAttributes = FontAttributes.Bold, TextColor = fore }
    };

    /// <summary>شريط نسبة (حد الدين، تقدم): الملء من اليمين في الاتجاه العربي.</summary>
    public static View Bar(double fraction, Color fill, double height = 8)
    {
        var f = Math.Clamp(fraction, 0, 1);
        var grid = new Grid { HeightRequest = height, ColumnSpacing = 0 };
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Max(0.0001, f), GridUnitType.Star)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Max(0.0001, 1 - f), GridUnitType.Star)));
        var track = new Border { BackgroundColor = Chip, StrokeThickness = 0, StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = height / 2 } };
        Grid.SetColumnSpan(track, 2);
        grid.Children.Add(track);
        grid.Children.Add(new Border { BackgroundColor = fill, StrokeThickness = 0, StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = height / 2 } });
        return grid;
    }

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
