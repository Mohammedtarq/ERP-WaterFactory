using ERP.Cloud.Contracts;
using ERP.RepApp.Core;
using Microsoft.Maui.Controls.Shapes;

namespace ERP.RepApp.Pages;

/// <summary>حقل إدخال بلا الخط السفلي الافتراضي لأندرويد: الإطار والخلفية من الحاوية.</summary>
public class BorderlessEntry : Entry
{
    static BorderlessEntry()
    {
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("BorderlessEntry", (handler, view) =>
        {
            if (view is BorderlessEntry) handler.PlatformView.Background = null;
        });
    }
}

/// <summary>
/// دخول المندوب: اسم وكلمة مرور قصيران (حتى 4 حروف أو أرقام) يضعهما المعمل. يُتحقق منهما محليًا من نسخة العمل (بلا إنترنت)،
/// مع 5 محاولات ثم إيقاف مؤقت يتضاعف. لا يظهر إلا إن ضُبط للمندوب دخول في المعمل.
/// <para>التصميم: رأس بحري بشعار المعمل (من حركة الفتح نفسها) وبطاقة بيضاء فيها الاسم وكلمة المرور (زر لإظهارها مؤقتًا)، ونقاط تعدّ الأحرف،
/// وزر «دخول» لا يعمل قبل الإدخال، وعدّاد المحاولات الخمس، وإيقاف بعدّاد تنازلي.</para>
/// <para>عند فتح التطبيق من جديد (intro) تسبق الشاشةَ حركةُ الفتح (تُتخطّى بلمسة)؛ وعند الخروج من الحساب تظهر الشاشة مباشرة.</para>
/// </summary>
public class LoginPage : ContentPage
{
    private readonly SnapshotLogin _login;
    private readonly LoginGuard _guard = new(AppServices.Instance.Store);
    private readonly RahmaIntro _stage = new();
    private readonly Border _sheet = new();
    private readonly BorderlessEntry _user, _pass;
    private readonly Border _userBox, _passBox;
    private readonly Label _eye = new() { Text = "إظهار", FontSize = 12, TextColor = Ui.Blue, FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center };
    private readonly List<BoxView> _passDots = new();
    private readonly List<BoxView> _attemptDots = new();
    private readonly Label _attemptsText = Ui.Text("", 12.5, Color.FromArgb("#6B7A99"));
    private readonly Border _messageBox = new() { IsVisible = false, StrokeThickness = 0, Padding = new Thickness(12, 10), StrokeShape = new RoundRectangle { CornerRadius = 12 } };
    private readonly Label _message = new() { FontSize = 14, FontAttributes = FontAttributes.Bold };
    private readonly Button _enter = new() { Text = "دخول", FontSize = 20, FontAttributes = FontAttributes.Bold, HeightRequest = 60, CornerRadius = 18, TextColor = Colors.White };
    private readonly Grid _success = new() { IsVisible = false, Opacity = 0, BackgroundColor = Ui.PageBg };
    private bool _busy, _leaving;
    private double _sheetTop = 300;
    private readonly bool _intro;

    private static readonly Color Soft = Color.FromArgb("#F3F7FF");

    public LoginPage(SnapshotLogin login, bool intro = false)
    {
        _login = login;
        _intro = intro;
        NavigationPage.SetHasNavigationBar(this, false);
        BackgroundColor = Color.FromArgb("#071F42");

        (_userBox, _user) = Field("person", "اسم المستخدم (حتى 4 حروف أو أرقام)", password: false);
        (_passBox, _pass) = Field("lock", "كلمة المرور", password: true);
        _user.Completed += (_, _) => _pass.Focus();
        _pass.Completed += async (_, _) => await TryAsync();
        _user.TextChanged += (_, _) => Refresh();
        _pass.TextChanged += (_, _) => Refresh();

        _enter.Clicked += async (_, _) => await TryAsync();
        _messageBox.Content = _message;

        var passDots = new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.Center, Margin = new Thickness(0, 10, 0, 0) };
        for (var i = 0; i < RepLogin.MaxLength; i++)
        {
            var dot = new BoxView { WidthRequest = 10, HeightRequest = 10, CornerRadius = 5, Color = Ui.Line };
            _passDots.Add(dot);
            passDots.Children.Add(dot);
        }

        var attempts = new HorizontalStackLayout { Spacing = 6, HorizontalOptions = LayoutOptions.Center, Margin = new Thickness(0, 16, 0, 0) };
        attempts.Children.Add(_attemptsText);
        for (var i = 0; i < LoginGuard.FreeAttempts; i++)
        {
            var dot = new BoxView { WidthRequest = 12, HeightRequest = 12, CornerRadius = 6, Color = Ui.Blue, VerticalOptions = LayoutOptions.Center };
            _attemptDots.Add(dot);
            attempts.Children.Add(dot);
        }

        var form = new VerticalStackLayout
        {
            Spacing = 4,
            Padding = new Thickness(22, 24, 22, 20),
            Children =
            {
                Ui.Title("أهلًا بك"),
                Ui.Text("سجّل دخولك لبدء يوم العمل", 13, Color.FromArgb("#6B7A99")),
                new Label { Text = "اسم المستخدم", FontSize = 12.5, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#5A6F9C"), Margin = new Thickness(2, 14, 0, 2) },
                _userBox,
                new Label { Text = "كلمة المرور", FontSize = 12.5, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#5A6F9C"), Margin = new Thickness(2, 8, 0, 2) },
                _passBox,
                passDots,
                new VerticalStackLayout { Margin = new Thickness(0, 14, 0, 0), Children = { _enter } },
                _messageBox,
                attempts,
                new Label
                {
                    Text = $"الدخول يعمل بدون إنترنت  ●  نسخة {AppInfo.Current.VersionString}", FontSize = 11.5, TextColor = Color.FromArgb("#9AA9CC"),
                    HorizontalTextAlignment = TextAlignment.Center, Margin = new Thickness(0, 14, 0, 0)
                }
            }
        };

        _sheet.Content = new ScrollView { Content = form };
        _sheet.BackgroundColor = Colors.White;
        _sheet.StrokeThickness = 0;
        _sheet.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(30, 30, 0, 0) };
        _sheet.Shadow = new Shadow { Brush = new SolidColorBrush(Colors.Black), Opacity = 0.25f, Radius = 24, Offset = new Point(0, -6) };
        _sheet.VerticalOptions = LayoutOptions.Fill;
        if (intro) { _sheet.TranslationY = 3000; _sheet.InputTransparent = true; }   // تظهر بعد الحركة

        var rep = AppServices.Instance.Store.Snapshot?.RepName;
        _success.Children.Add(new VerticalStackLayout
        {
            Spacing = 14, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.Center,
            Children =
            {
                new Border
                {
                    WidthRequest = 90, HeightRequest = 90, BackgroundColor = Ui.Green, StrokeThickness = 0, HorizontalOptions = LayoutOptions.Center,
                    StrokeShape = new RoundRectangle { CornerRadius = 45 },
                    Content = new Label { Text = "✓", FontSize = 50, TextColor = Colors.White, HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center }
                },
                new Label { Text = string.IsNullOrWhiteSpace(rep) ? "أهلًا بك" : $"أهلًا {rep}", FontSize = 22, FontAttributes = FontAttributes.Bold, TextColor = Ui.Ink, HorizontalTextAlignment = TextAlignment.Center }
            }
        });

        var root = new Grid();
        root.Children.Add(_stage);
        root.Children.Add(_sheet);
        root.Children.Add(_success);
        Content = root;

        // لمسة على الرأس تتخطى الحركة
        _stage.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(Skip) });
        _stage.Rendered += OnStageRendered;
        SizeChanged += (_, _) => PositionSheet();
        Refresh();
        ShowAttempts();
        if (_guard.LockedSeconds > 0) StartLockCountdown();
    }

    private (Border Box, BorderlessEntry Entry) Field(string icon, string placeholder, bool password)
    {
        var entry = new BorderlessEntry
        {
            Placeholder = placeholder, PlaceholderColor = Color.FromArgb("#9AA9CC"), MaxLength = RepLogin.MaxLength, IsPassword = password,
            FontSize = 22, FontAttributes = FontAttributes.Bold, TextColor = Ui.Ink, BackgroundColor = Colors.Transparent,
            HorizontalTextAlignment = TextAlignment.Center, FlowDirection = FlowDirection.LeftToRight, VerticalOptions = LayoutOptions.Center,
            ReturnType = password ? ReturnType.Go : ReturnType.Next, Keyboard = Keyboard.Default
        };
        var grid = new Grid { ColumnSpacing = 10, ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
        var iconBox = new Border
        {
            WidthRequest = 34, HeightRequest = 34, BackgroundColor = Ui.Chip, StrokeThickness = 0, VerticalOptions = LayoutOptions.Center,
            StrokeShape = new RoundRectangle { CornerRadius = 10 }, Content = Ui.Icon(icon, Ui.Blue, 18)
        };
        grid.Add(iconBox, 0);
        grid.Add(entry, 1);
        if (password)
        {
            _eye.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(ToggleEye) });
            grid.Add(_eye, 2);
        }
        else grid.Add(new BoxView { WidthRequest = 34, Color = Colors.Transparent }, 2);

        var box = new Border
        {
            Content = grid, HeightRequest = 58, Padding = new Thickness(12, 0), BackgroundColor = Soft,
            Stroke = Ui.Line, StrokeThickness = 2, StrokeShape = new RoundRectangle { CornerRadius = 16 }
        };
        entry.Focused += (_, _) => { box.Stroke = Ui.Blue; box.BackgroundColor = Colors.White; };
        entry.Unfocused += (_, _) => { box.Stroke = Ui.Line; box.BackgroundColor = Soft; };
        return (box, entry);
    }

    private void ToggleEye()
    {
        _pass.IsPassword = !_pass.IsPassword;
        _eye.Text = _pass.IsPassword ? "إظهار" : "إخفاء";
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_intro) _stage.PlayWhenReady(RahmaIntro.HeaderReady, () => _ = FocusUserAsync());
        else { _stage.Jump(RahmaIntro.HeaderReady); }
    }

    private async Task FocusUserAsync()
    {
        await Task.Delay(150);
        if (_guard.LockedSeconds == 0) _user.Focus();
    }

    private void Skip()
    {
        if (_t < RahmaIntro.HeaderReady) _stage.Jump(RahmaIntro.HeaderReady);
    }

    private double _t;

    /// <summary>الرأس يتحرك بالحركة، والبطاقة تصعد من الأسفل بين 4.7 و5.6 ثانية.</summary>
    private void OnStageRendered(double t)
    {
        _t = t;
        var k = RahmaIntro.SheetFrom >= RahmaIntro.SheetTo ? 1 : Math.Clamp((t - RahmaIntro.SheetFrom) / (RahmaIntro.SheetTo - RahmaIntro.SheetFrom), 0, 1);
        var eased = k < .5 ? 4 * k * k * k : 1 - Math.Pow(-2 * k + 2, 3) / 2;
        _sheet.TranslationY = (1 - eased) * Math.Max(Height, 600);
        _sheet.InputTransparent = k < 1;
    }

    private void PositionSheet()
    {
        if (Width <= 0) return;
        _sheetTop = 300 * (Width / 390);
        _sheet.Margin = new Thickness(0, _sheetTop, 0, 0);
        if (_t >= RahmaIntro.HeaderReady || !_intro) OnStageRendered(Math.Max(_t, _intro ? 0 : RahmaIntro.HeaderReady));
    }

    // ---------------------------------------------------------------- الحالة والرسائل

    private void Refresh()
    {
        var ready = _user.Text?.Length > 0 && _pass.Text?.Length > 0 && _guard.LockedSeconds == 0 && !_busy;
        _enter.IsEnabled = ready;
        _enter.Background = ready
            ? new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromArgb("#1D4FD0"), 0f), new GradientStop(Color.FromArgb("#2F6DF0"), 1f) }, new Point(0, 0), new Point(1, 1))
            : new SolidColorBrush(Color.FromArgb("#B9C8E8"));
        var n = _pass.Text?.Length ?? 0;
        for (var i = 0; i < _passDots.Count; i++) _passDots[i].Color = i < n ? Ui.Blue : Ui.Line;
    }

    private void ShowAttempts()
    {
        var left = _guard.LockedSeconds > 0 ? 0 : _guard.AttemptsLeft;
        for (var i = 0; i < _attemptDots.Count; i++) _attemptDots[i].Color = i < left ? Ui.Blue : Ui.Line;
        _attemptsText.Text = $"المحاولات ({left} من {LoginGuard.FreeAttempts}):";
    }

    private void ShowMessage(string text, bool error)
    {
        _message.Text = text;
        _message.TextColor = error ? Ui.Red : Color.FromArgb("#9A6A12");
        _messageBox.BackgroundColor = error ? Color.FromArgb("#FDECEC") : Color.FromArgb("#FFF3D6");
        _messageBox.IsVisible = text.Length > 0;
    }

    private void MarkWrong(bool wrong)
    {
        _passBox.Stroke = wrong ? Ui.Red : Ui.Line;
        _passBox.BackgroundColor = wrong ? Color.FromArgb("#FFF5F5") : Soft;
    }

    private void Shake(View v) =>
        new Animation(p => v.TranslationX = Math.Sin(p * Math.PI * 6) * 8 * (1 - p), 0, 1).Commit(v, "shake", 16, 380, Easing.Linear);

    // ---------------------------------------------------------------- المحاولة

    private async Task TryAsync()
    {
        if (_busy || _leaving || _guard.LockedSeconds > 0) return;
        _busy = true;
        Refresh();
        try
        {
            var result = _guard.TryLogin(_login, _user.Text, _pass.Text);
            if (result.Ok) { await EnterAsync(); return; }
            _pass.Text = "";
            MarkWrong(true);
            Shake(_passBox);
            ShowAttempts();
            if (result.LockedSeconds > 0) StartLockCountdown();
            else ShowMessage(result.Message, error: true);
            _pass.Focus();
        }
        finally { _busy = false; Refresh(); }
    }

    /// <summary>إيقاف مؤقت: عدّاد تنازلي يعطّل الزر، ثم يعود كل شيء وحده.</summary>
    private void StartLockCountdown()
    {
        void Tick()
        {
            var left = _guard.LockedSeconds;
            ShowAttempts();
            if (left > 0)
            {
                ShowMessage($"محاولات كثيرة — أعد المحاولة بعد {LoginResult.Clock(left)}", error: true);
                _enter.Text = "مقفل مؤقتًا";
            }
            else { ShowMessage("يمكنك المحاولة الآن", error: false); MarkWrong(false); _enter.Text = "دخول"; }
            Refresh();
        }
        Tick();
        Dispatcher.StartTimer(TimeSpan.FromSeconds(1), () => { Tick(); return _guard.LockedSeconds > 0; });
    }

    private async Task EnterAsync()
    {
        _leaving = true;
        _success.IsVisible = true;
        await _success.FadeTo(1, 220);
        await Task.Delay(650);
        App.ShowRoot(MainPages.Create("home"));
    }
}
