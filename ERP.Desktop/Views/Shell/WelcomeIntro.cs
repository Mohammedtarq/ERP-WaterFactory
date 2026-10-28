using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ERP.Desktop.Views.Shell;

/// <summary>
/// حركة الترحيب بعد الدخول (نحو ثلاث ثوانٍ ونصف): قطرة ماء تسقط فتتموّج المياه وتتطاير رذاذاتها، ثم تتحول القطرة إلى شعار المعمل الملوّن
/// ويمرّ عليه بريق، ويظهر الترحيب باسم المستخدم والمعمل وشعاره اللفظي، ثم تتلاشى الطبقة كلها عن الواجهة الرئيسية.
/// نقرة أو أي مفتاح تتخطاها، ولا تعمل إن كان Windows مضبوطًا على تقليل الحركة.
/// </summary>
public sealed class WelcomeIntro : Grid
{
    private static readonly Uri LogoUri = new("pack://application:,,,/Assets/logo-color.png");
    private const double LogoSize = 300, ExitAt = 3.0;
    private readonly Canvas _stage = new() { FlowDirection = FlowDirection.LeftToRight };
    private bool _leaving;

    public WelcomeIntro(string fullName, string company, string? slogan)
    {
        Background = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.42), GradientOrigin = new Point(0.5, 0.42), RadiusX = 0.85, RadiusY = 0.85,
            GradientStops = { new GradientStop(C("#1B6FB8"), 0), new GradientStop(C("#0D3F78"), 0.55), new GradientStop(C("#071F42"), 1) }
        };
        Children.Add(_stage);
        Children.Add(Greeting(fullName, company, slogan));
        Cursor = Cursors.Hand;
        ToolTip = "انقر للتخطي";
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = new ScaleTransform(1, 1);
        PreviewMouseDown += (_, _) => Leave();
        Loaded += (_, _) => Play();
    }

    /// <summary>يضع الحركة فوق محتوى النافذة؛ ولا شيء إن كانت الحركة مقلّلة في Windows.</summary>
    public static void Show(Window window, string fullName, string company, string? slogan)
    {
        if (!SystemParameters.ClientAreaAnimation || window.Content is not Grid root) return;
        var intro = new WelcomeIntro(fullName, company, slogan);
        Grid.SetColumnSpan(intro, Math.Max(1, root.ColumnDefinitions.Count));
        Grid.SetRowSpan(intro, Math.Max(1, root.RowDefinitions.Count));
        Panel.SetZIndex(intro, 1000);
        root.Children.Add(intro);
        void Key(object s, KeyEventArgs e) { intro.Leave(); window.PreviewKeyDown -= Key; }
        window.PreviewKeyDown += Key;
    }

    private void Play()
    {
        var w = Math.Max(ActualWidth, 800);
        var h = Math.Max(ActualHeight, 600);
        _stage.Width = w; _stage.Height = h;
        var cx = w / 2; var cy = h / 2 - 70;

        Bubbles(w, h);

        // التوهج خلف الشعار
        var glow = new Ellipse
        {
            Width = 520, Height = 520, Opacity = 0,
            Fill = new RadialGradientBrush(Color.FromArgb(0x70, 0x38, 0xBD, 0xF8), Color.FromArgb(0, 0x38, 0xBD, 0xF8))
        };
        Place(glow, cx, cy);
        Fade(glow, 0, 1, 0.62, 0.6);

        // التموّجات: حلقات مسطّحة كسطح الماء من نقطة الارتطام
        for (var i = 0; i < 3; i++)
        {
            var ring = new Ellipse { Width = 120, Height = 38, Stroke = new SolidColorBrush(C("#7DD3FC")), StrokeThickness = 3, Opacity = 0 };
            var st = Center(ring, cx, cy + 120);
            var at = 0.62 + i * 0.16;
            Anim(st, ScaleTransform.ScaleXProperty, 0.3, 4.2 - i * 0.6, at, 1.1, new QuadraticEase { EasingMode = EasingMode.EaseOut });
            Anim(st, ScaleTransform.ScaleYProperty, 0.3, 4.2 - i * 0.6, at, 1.1, new QuadraticEase { EasingMode = EasingMode.EaseOut });
            ring.BeginAnimation(OpacityProperty, Keys((at, 0.9), (at + 1.1, 0)));
        }

        // القطرة: تسقط وتتمدد، ثم تنضغط عند الارتطام وتذوب في الشعار
        var drop = Drop();
        _stage.Children.Add(drop);
        Canvas.SetLeft(drop, cx - 45); Canvas.SetTop(drop, cy + 120 - 133);
        var dScale = new ScaleTransform(1, 1);
        var dMove = new TranslateTransform(0, -(h / 2 + 200));
        drop.RenderTransformOrigin = new Point(0.5, 1);
        drop.RenderTransform = new TransformGroup { Children = { dScale, dMove } };
        Anim(dMove, TranslateTransform.YProperty, -(h / 2 + 200), 0, 0, 0.62, new QuadraticEase { EasingMode = EasingMode.EaseIn });
        dScale.BeginAnimation(ScaleTransform.ScaleYProperty, Keys((0, 1), (0.5, 1.25), (0.62, 0.62), (0.75, 1.1), (0.9, 0.2)));
        dScale.BeginAnimation(ScaleTransform.ScaleXProperty, Keys((0, 1), (0.5, 0.85), (0.62, 1.35), (0.75, 0.9), (0.9, 0.2)));
        drop.BeginAnimation(OpacityProperty, Keys((0, 1), (0.72, 1), (0.9, 0)));

        // الرذاذ: قطيرات تتطاير بقوس ثم تسقط
        var rnd = new Random(7);
        for (var i = 0; i < 10; i++)
        {
            var size = 7 + rnd.NextDouble() * 9;
            var dot = new Ellipse { Width = size, Height = size, Fill = new SolidColorBrush(C(i % 3 == 0 ? "#B8F1FF" : "#3EC5F5")), Opacity = 0 };
            var t = new TranslateTransform();
            dot.RenderTransform = t;
            _stage.Children.Add(dot);
            Canvas.SetLeft(dot, cx - size / 2); Canvas.SetTop(dot, cy + 110);
            var dir = (i % 2 == 0 ? 1 : -1) * (40 + rnd.NextDouble() * 170);
            var up = 70 + rnd.NextDouble() * 120;
            Anim(t, TranslateTransform.XProperty, 0, dir, 0.62, 0.7, null);
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimationUsingKeyFrames
            {
                BeginTime = TimeSpan.FromSeconds(0.62),
                KeyFrames =
                {
                    new EasingDoubleKeyFrame(-up, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.32)), new QuadraticEase { EasingMode = EasingMode.EaseOut }),
                    new EasingDoubleKeyFrame(40, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.7)), new QuadraticEase { EasingMode = EasingMode.EaseIn })
                }
            });
            dot.BeginAnimation(OpacityProperty, Keys((0.62, 1), (1.1, 0.9), (1.32, 0)));
        }

        // الشعار الملوّن يولد من القطرة
        var logo = new Image { Source = new BitmapImage(LogoUri), Width = LogoSize, Height = LogoSize, Opacity = 0 };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        var ls = Center(logo, cx, cy);
        var lr = new RotateTransform(-10);
        ((TransformGroup)logo.RenderTransform).Children.Add(lr);
        Anim(ls, ScaleTransform.ScaleXProperty, 0.25, 1, 0.74, 0.75, new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut });
        Anim(ls, ScaleTransform.ScaleYProperty, 0.25, 1, 0.74, 0.75, new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut });
        Anim(lr, RotateTransform.AngleProperty, -10, 0, 0.74, 0.75, new CubicEase { EasingMode = EasingMode.EaseOut });
        Fade(logo, 0, 1, 0.74, 0.35);

        // بريق يمر على الشعار
        var sweep = new TranslateTransform(-1.2, 0);
        var shine = new Rectangle
        {
            Width = LogoSize, Height = LogoSize, IsHitTestVisible = false,
            OpacityMask = new ImageBrush(new BitmapImage(LogoUri)),
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(1, 0.35), RelativeTransform = sweep,
                GradientStops = { new GradientStop(Colors.Transparent, 0.35), new GradientStop(Color.FromArgb(0xC8, 255, 255, 255), 0.5), new GradientStop(Colors.Transparent, 0.65) }
            }
        };
        Place(shine, cx, cy);
        Anim(sweep, TranslateTransform.XProperty, -1.2, 1.2, 1.55, 0.8, new SineEase { EasingMode = EasingMode.EaseInOut });

        // التلاشي عن الواجهة الرئيسية
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(ExitAt) };
        timer.Tick += (_, _) => { timer.Stop(); Leave(); };
        timer.Start();
    }

    private void Leave()
    {
        if (_leaving) return;
        _leaving = true;
        IsHitTestVisible = false;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.55)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => (Parent as Panel)?.Children.Remove(this);
        BeginAnimation(OpacityProperty, fade);
        var grow = new DoubleAnimation(1, 1.08, TimeSpan.FromSeconds(0.55));
        ((ScaleTransform)RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        ((ScaleTransform)RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private static FrameworkElement Greeting(string fullName, string company, string? slogan)
    {
        var hello = DateTime.Now.Hour < 12 ? "صباح الخير" : "مساء الخير";
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, LogoSize + 120, 0, 0), Opacity = 0, IsHitTestVisible = false,
            RenderTransform = new TranslateTransform(0, 24)
        };
        panel.Children.Add(new TextBlock { Text = $"{hello}، {fullName}", Foreground = Brushes.White, FontSize = 30, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = company, Foreground = new SolidColorBrush(C("#BFE6FA")), FontSize = 19, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
        if (!string.IsNullOrWhiteSpace(slogan))
            panel.Children.Add(new TextBlock { Text = slogan, Foreground = new SolidColorBrush(C("#7DD3FC")), FontSize = 17, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
        Fade(panel, 0, 1, 1.3, 0.6);
        Anim((TranslateTransform)panel.RenderTransform, TranslateTransform.YProperty, 24, 0, 1.3, 0.6, new CubicEase { EasingMode = EasingMode.EaseOut });
        return panel;
    }

    /// <summary>فقاعات خافتة تصعد في الخلفية.</summary>
    private void Bubbles(double w, double h)
    {
        var rnd = new Random(3);
        for (var i = 0; i < 16; i++)
        {
            var size = 6 + rnd.NextDouble() * 22;
            var b = new Ellipse { Width = size, Height = size, Stroke = new SolidColorBrush(Color.FromArgb(0x55, 0xB8, 0xF1, 0xFF)), StrokeThickness = 1.5,
                                  Fill = new SolidColorBrush(Color.FromArgb(0x18, 0xB8, 0xF1, 0xFF)) };
            var t = new TranslateTransform();
            b.RenderTransform = t;
            _stage.Children.Add(b);
            Canvas.SetLeft(b, rnd.NextDouble() * w); Canvas.SetTop(b, h * (0.55 + rnd.NextDouble() * 0.5));
            Anim(t, TranslateTransform.YProperty, 0, -(h * (0.4 + rnd.NextDouble() * 0.4)), rnd.NextDouble() * 0.6, 3.2, null);
        }
    }

    private static Path Drop()
    {
        var shape = Geometry.Parse("M45,0 C60,30 90,62 90,88 A45,45 0 1 1 0,88 C0,62 30,30 45,0 Z");
        return new Path
        {
            Data = shape, Width = 90, Height = 133, Stretch = Stretch.None,
            Fill = new LinearGradientBrush(C("#B8F1FF"), C("#1279D6"), new Point(0.2, 0), new Point(0.8, 1)),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = C("#38BDF8"), BlurRadius = 30, ShadowDepth = 0, Opacity = 0.8 }
        };
    }

    private void Place(FrameworkElement e, double cx, double cy)
    {
        _stage.Children.Add(e);
        Canvas.SetLeft(e, cx - e.Width / 2);
        Canvas.SetTop(e, cy - e.Height / 2);
    }

    private ScaleTransform Center(FrameworkElement e, double cx, double cy)
    {
        Place(e, cx, cy);
        var s = new ScaleTransform(1, 1);
        e.RenderTransformOrigin = new Point(0.5, 0.5);
        e.RenderTransform = new TransformGroup { Children = { s } };
        return s;
    }

    private static void Anim(Animatable target, DependencyProperty p, double from, double to, double at, double dur, IEasingFunction? ease) =>
        target.BeginAnimation(p, new DoubleAnimation(from, to, TimeSpan.FromSeconds(dur)) { BeginTime = TimeSpan.FromSeconds(at), EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });

    private static void Fade(UIElement e, double from, double to, double at, double dur) =>
        e.BeginAnimation(OpacityProperty, new DoubleAnimation(from, to, TimeSpan.FromSeconds(dur)) { BeginTime = TimeSpan.FromSeconds(at) });

    /// <summary>قيم عند أزمنة مطلقة (بالثواني) بانتقال خطي بينها.</summary>
    private static DoubleAnimationUsingKeyFrames Keys(params (double at, double value)[] keys)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        foreach (var (at, value) in keys) a.KeyFrames.Add(new LinearDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(at))));
        return a;
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
