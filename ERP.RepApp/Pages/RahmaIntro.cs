using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Layouts;
using ShapePath = Microsoft.Maui.Controls.Shapes.Path;   // يتعارض اسم Path مع System.IO.Path في الاستخدامات الضمنية

namespace ERP.RepApp.Pages;

/// <summary>
/// مسرح حركة فتح التطبيق (من هوية برنامج ويندوز نفسها): خلفية بحرية بفقاعات صاعدة، وحروف «الرحمة» بخط الملصق الأصلي تتطاير من أطراف الشاشة
/// وتتجمع في الوسط فتكتمل الكلمة، فتصغر وتدخل قطرة ماء تسقط فتتموّج وتتطاير رذاذاتها، ثم تتحول إلى شعار المعمل الذي يصعد إلى رأس الشاشة
/// وتظهر تحته عبارات المعمل. الحركة كلها دالة في الزمن <see cref="Render"/> (ثوانٍ) يقودها رسم متحرك واحد، فلا حالات متفرقة.
/// الإحداثيات في فضاء تصميمي عرضه 390 يُكبَّر بعرض الشاشة الفعلي.
/// </summary>
public sealed class RahmaIntro : AbsoluteLayout
{
    /// <summary>الزمن (ثوانٍ) الذي تستقر عنده الشاشة على رأس الدخول: الشعار في الأعلى والعبارات ظاهرة.</summary>
    public const double HeaderReady = 5.8;
    /// <summary>بداية صعود بطاقة الدخول واكتمالها.</summary>
    public const double SheetFrom = 4.7, SheetTo = 5.6;
    /// <summary>نهاية الحركة وحدها (الشعار في الوسط بعد التموّج) حين لا تتبعها شاشة دخول.</summary>
    public const double IntroOnly = 4.6;

    private const double VW = 390;
    // شرائح الكلمة من اليمين: (بداية الشريحة، عرضها) في صورة عرضها 1500 وارتفاعها 660 + هامش توهج 40 من كل جهة
    private static readonly (double X0, double W)[] Slices = { (1230, 270), (1060, 170), (800, 260), (560, 240), (290, 270), (0, 290) };
    private const double Pad = 40, SrcH = 660, Sc = 330.0 / 1500;
    // انزياح البداية لكل شريحة (س، ص، دوران) قبل أن تتجمع
    private static readonly (double X, double Y, double R)[] Scatter =
        { (60, -380, 45), (-300, -330, -40), (330, -120, 35), (-340, 300, -30), (250, 420, 25), (-90, 520, -45) };

    private readonly Grid _bg;
    private readonly List<(Border View, double X, double Y, double Size, double Speed)> _bubbles = new();
    private readonly List<(Image View, double X0, double W)> _pieces = new();
    private readonly ShapePath _drop;
    private readonly Border[] _rings = new Border[3];
    private readonly Border[] _sparks = new Border[8];
    private readonly Border _glow;
    private readonly Image _logo;
    private readonly Label _h1, _h2, _h3;
    private double _s;          // نسبة تكبير الفضاء التصميمي
    private double _t;          // الزمن الحالي
    private Action? _whenReady;

    /// <summary>يُستدعى بعد كل إطار بالزمن الحالي (تستعمله شاشة الدخول لإطلاع بطاقتها).</summary>
    public event Action<double>? Rendered;

    public RahmaIntro()
    {
        // التطبيق كله من اليمين لليسار؛ والمسرح إحداثياته من اليسار (في RTL يبدأ AbsoluteLayout من اليمين فتخرج العناصر من الشاشة)
        FlowDirection = FlowDirection.LeftToRight;
        BackgroundColor = Color.FromArgb("#071F42");
        _bg = new Grid
        {
            Background = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.38), Radius = 0.95,
                GradientStops = { new GradientStop(Color.FromArgb("#1B6FB8"), 0f), new GradientStop(Color.FromArgb("#0D3F78"), 0.55f), new GradientStop(Color.FromArgb("#071F42"), 1f) }
            }
        };
        Children.Add(_bg);

        for (var i = 0; i < 14; i++)
        {
            var size = 6 + (i * 37) % 22;
            var b = new Border
            {
                BackgroundColor = Color.FromArgb("#33BFE9FF"), Stroke = Color.FromArgb("#44BFE9FF"), StrokeThickness = 1, InputTransparent = true,
                StrokeShape = new RoundRectangle { CornerRadius = size / 2.0 }, Opacity = 0
            };
            _bubbles.Add((b, (i * 53) % 370 + 10, (i * 97) % 800, size, 14 + (i * 7) % 22));
            Children.Add(b);
        }

        _glow = new Border
        {
            InputTransparent = true, Opacity = 0, StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 140 },
            Background = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5), Radius = 0.5,
                GradientStops = { new GradientStop(Color.FromArgb("#5538BDF8"), 0f), new GradientStop(Color.FromArgb("#0038BDF8"), 1f) }
            }
        };
        Children.Add(_glow);

        for (var i = 0; i < _rings.Length; i++)
        {
            _rings[i] = new Border
            {
                InputTransparent = true, Opacity = 0, BackgroundColor = Colors.Transparent, Stroke = Color.FromArgb("#8BE0FF"), StrokeThickness = 5,
                StrokeShape = new RoundRectangle { CornerRadius = 150 }
            };
            Children.Add(_rings[i]);
        }
        for (var i = 0; i < _sparks.Length; i++)
        {
            _sparks[i] = new Border
            {
                InputTransparent = true, Opacity = 0, BackgroundColor = Color.FromArgb("#BFEAFF"), StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 4 }
            };
            Children.Add(_sparks[i]);
        }

        _drop = new ShapePath
        {
            InputTransparent = true, Opacity = 0, StrokeThickness = 0, Aspect = Stretch.Fill,
            Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString("M 65 4 C 65 4 12 72 12 110 a 53 53 0 0 0 106 0 C 118 72 65 4 65 4 z")!,
            Fill = new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(Color.FromArgb("#A5ECFF"), 0f), new GradientStop(Color.FromArgb("#2B7FD8"), 1f)
            }, new Point(0, 0), new Point(0, 1))
        };
        Children.Add(_drop);

        for (var i = 0; i < Slices.Length; i++)
        {
            var piece = new Image { Source = $"rahma_p{i + 1}.png", Aspect = Aspect.Fill, InputTransparent = true, Opacity = 0 };
            _pieces.Add((piece, Slices[i].X0, Slices[i].W));
            Children.Add(piece);
        }

        _logo = new Image { Source = "rahma_logo.png", Aspect = Aspect.AspectFit, InputTransparent = true, Opacity = 0 };
        Children.Add(_logo);

        _h1 = Caption("معمل مياه باب الرحمة", 25, Colors.White, FontAttributes.Bold);
        _h2 = Caption("تطبيق المندوبين", 13.5, Color.FromArgb("#9FD2F5"), FontAttributes.None);
        _h3 = Caption("خليك مثل الماي", 13, Color.FromArgb("#7DD3FC"), FontAttributes.Italic);
        Children.Add(_h1); Children.Add(_h2); Children.Add(_h3);
    }

    private static Label Caption(string text, double size, Color color, FontAttributes attrs) => new()
    {
        Text = text, FontSize = size, TextColor = color, FontAttributes = attrs, HorizontalTextAlignment = TextAlignment.Center, InputTransparent = true, Opacity = 0, FlowDirection = FlowDirection.RightToLeft
    };

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width <= 0 || height <= 0) return;
        var s = width / VW;
        if (Math.Abs(s - _s) > 0.001)
        {
            _s = s;
            ApplySize();
        }
        if (_whenReady is { } go) { _whenReady = null; go(); }
    }

    private static void Place(View v, double w, double h)
    {
        SetLayoutBounds((BindableObject)v, new Rect(0, 0, w, h));
        SetLayoutFlags((BindableObject)v, AbsoluteLayoutFlags.None);
    }

    private void ApplySize()
    {
        SetLayoutBounds((BindableObject)_bg, new Rect(0, 0, 1, 1));
        SetLayoutFlags((BindableObject)_bg, AbsoluteLayoutFlags.All);
        foreach (var b in _bubbles) Place(b.View, b.Size * _s, b.Size * _s);
        Place(_glow, 280 * _s, 280 * _s);
        foreach (var r in _rings) Place(r, 300 * _s, 300 * _s);
        foreach (var sp in _sparks) Place(sp, 7 * _s, 7 * _s);
        Place(_drop, 130 * _s, 170 * _s);
        foreach (var piece in _pieces) Place(piece.View, (piece.W + 2 * Pad) * Sc * _s, (SrcH + 2 * Pad) * Sc * _s);
        Place(_logo, 200 * _s, 200 * _s);
        foreach (var l in new[] { _h1, _h2, _h3 }) Place(l, VW * _s, 40 * _s);
        _h1.FontSize = 25 * _s; _h2.FontSize = 13.5 * _s; _h3.FontSize = 13 * _s;
        Render(_t);
    }

    /// <summary>يبدأ التشغيل عند توفر الحجم (قد يكون فورًا).</summary>
    public void PlayWhenReady(double endAt, Action? done = null)
    {
        void Go()
        {
            this.AbortAnimation("intro");
            var anim = new Animation(SetTime, 0, endAt);
            anim.Commit(this, "intro", 16, (uint)(endAt * 1000), Easing.Linear, (_, cancelled) => { if (!cancelled) done?.Invoke(); });
        }
        if (_s > 0) Go(); else _whenReady = Go;
    }

    /// <summary>قفز إلى لحظة (تخطي الحركة) وإيقاف الرسم المتحرك.</summary>
    public void Jump(double t)
    {
        _whenReady = null;
        this.AbortAnimation("intro");
        SetTime(t);
    }

    private void SetTime(double t)
    {
        _t = t;
        if (_s <= 0) return;
        Render(t);
        Rendered?.Invoke(t);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double Cl(double x) => Math.Max(0, Math.Min(1, x));
    private static double Seg(double t, double a, double b) => Cl((t - a) / (b - a));
    private static double Ease(double t) => t < .5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    /// <summary>يضع العنصر بمركزه (بإحداثيات الفضاء التصميمي) مع تكبير ودوران وشفافية؛ العنصر حجمه الأصلي w×h.</summary>
    private void Put(VisualElement v, double centerX, double centerY, double w, double h, double scale = 1, double rotation = 0, double opacity = 1)
    {
        v.TranslationX = (centerX - w / 2) * _s;
        v.TranslationY = (centerY - h / 2) * _s;
        v.Scale = scale;
        v.Rotation = rotation;
        v.Opacity = Cl(opacity);
    }

    public void Render(double t)
    {
        if (_s <= 0) return;
        // ---- الفقاعات ----
        foreach (var bubble in _bubbles)
        {
            var y = ((bubble.Y - bubble.Speed * t) % 820 + 820) % 820 - 20;
            Put(bubble.View, bubble.X + Math.Sin(t * 1.3 + bubble.Size) * 6 + bubble.Size / 2, y + bubble.Size / 2, bubble.Size, bubble.Size, 1, 0, Seg(t, 0, .5));
        }

        // ---- الحروف تتجمع (0.2 → 1.45)، ثم الكلمة تصغر وتدخل القطرة (2.05 → 2.75) وتتلاشى ----
        var shrink = Ease(Seg(t, 2.05, 2.75));
        var gs = Lerp(1, .42, shrink);
        var gty = Lerp(0, 80, shrink);
        const double cx = 195, cy = 218 + SrcH * Sc / 2;          // مركز مربع الكلمة
        var wordFade = 1 - Seg(t, 2.55, 2.9);
        for (var i = 0; i < _pieces.Count; i++)
        {
            var piece = _pieces[i];
            var p = Ease(Seg(t, .2 + i * .09, 1.45 + i * .09));
            var pw = (piece.W + 2 * Pad) * Sc;
            var ph = (SrcH + 2 * Pad) * Sc;
            var homeX = 30 + (piece.X0 - Pad) * Sc + pw / 2;
            var homeY = 218 - Pad * Sc + ph / 2;
            var px = cx + (homeX - cx) * gs + Scatter[i].X * (1 - p) * gs;
            var py = cy + (homeY - cy) * gs + gty + Scatter[i].Y * (1 - p) * gs;
            Put(piece.View, px, py, pw, ph, gs, Scatter[i].R * (1 - p), Seg(t, .1, .4) * (.3 + .7 * p) * wordFade);
        }

        // ---- القطرة: تنمو ثم تنبض ثم تتلاشى عند ظهور الشعار ----
        var dp = Ease(Seg(t, 2.05, 2.8));
        var pulse = 1 + .05 * Math.Sin(Seg(t, 2.8, 3.4) * Math.PI);
        _drop.AnchorX = .5; _drop.AnchorY = .7;
        Put(_drop, 195, 325 + Lerp(60, 0, dp), 130, 170, Lerp(.15, 1, dp) * pulse, 0, Seg(t, 2.05, 2.4) * (1 - Seg(t, 3.45, 3.8)));

        // ---- التموّج والرذاذ ----
        var ringStart = new[] { 2.85, 3.1, 3.35 };
        for (var i = 0; i < _rings.Length; i++)
        {
            var p = Seg(t, ringStart[i], ringStart[i] + 1.1);
            var k = Lerp(.1, 1, Ease(p));
            _rings[i].Scale = k;
            _rings[i].ScaleY = k * .32;                           // قطع ناقص مسطّح كسطح الماء
            _rings[i].TranslationX = (195 - 150) * _s;
            _rings[i].TranslationY = (470 - 150) * _s;
            _rings[i].Opacity = p > 0 && p < 1 ? (1 - p) * .9 : 0;
        }
        for (var i = 0; i < _sparks.Length; i++)
        {
            var p = Seg(t, 2.85, 3.5);
            var a = i / (double)_sparks.Length * Math.PI * 2;
            var r = Lerp(0, 60 + i % 3 * 18, Ease(p));
            Put(_sparks[i], 195 + Math.Cos(a) * r, 470 - Math.Abs(Math.Sin(a)) * r * 1.1 - Math.Sin(p * Math.PI) * 22, 7, 7, 1, 0, p > 0 && p < 1 ? 1 - p : 0);
        }

        // ---- الشعار: يظهر في الوسط ثم يصعد ويصغر إلى رأس الشاشة (4.4 → 5.4) ----
        var li = Seg(t, 3.45, 3.8);
        var grow = Ease(Seg(t, 3.45, 4.0));
        var move = Ease(Seg(t, 4.4, 5.4));
        var size = Lerp(200, 120, move) * Lerp(.78, 1, grow);
        var logoY = Lerp(400, 114, move);
        Put(_logo, 195, logoY, 200, 200, size / 200, 0, li);
        Put(_glow, 195, logoY, 280, 280, size / 200, 0, li * (1 - move * .5));

        // ---- عبارات المعمل ----
        Put(_h1, 195, 182 + Lerp(14, 0, Ease(Seg(t, 5.0, 5.6))) + 20, VW, 40, 1, 0, Seg(t, 5.0, 5.6));
        Put(_h2, 195, 220 + Lerp(14, 0, Ease(Seg(t, 5.1, 5.7))) + 20, VW, 40, 1, 0, Seg(t, 5.1, 5.7));
        Put(_h3, 195, 243 + Lerp(14, 0, Ease(Seg(t, 5.2, 5.8))) + 20, VW, 40, 1, 0, Seg(t, 5.2, 5.8));
    }
}
