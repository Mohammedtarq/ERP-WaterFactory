using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>
/// الرئيسية: شريط باسم المعمل والمندوب (وخروج وإعدادات)، وحالة الاتصال، ومبيعات اليوم والنقد وما ينتظر الإرسال، وزر «بيع»،
/// وبطاقة ديون الزبائن، وستة أزرار بألوان معانيها؛ والمزامنة كل دقيقة.
/// </summary>
public class HomePage : ContentPage
{
    private readonly AppServices _app = AppServices.Instance;
    private readonly Label _connection = new() { FontSize = 13, FontAttributes = FontAttributes.Bold };
    private readonly Border _connectionBox = new() { StrokeThickness = 0, Padding = new Thickness(12, 8), StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 } };
    private readonly Label _today = new() { FontSize = 38, FontAttributes = FontAttributes.Bold };
    private readonly Label _wallet = new() { FontSize = 19, FontAttributes = FontAttributes.Bold };
    private readonly Label _waiting = new() { FontSize = 19, FontAttributes = FontAttributes.Bold };
    private readonly Label _debtTitle = new() { FontSize = 15, FontAttributes = FontAttributes.Bold };
    private readonly Label _debtSub = new() { FontSize = 12 };
    private readonly Label _debtAmount = new() { FontSize = 18, FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center };
    private readonly Label _custodyTitle = new() { FontSize = 15, FontAttributes = FontAttributes.Bold, TextColor = Ui.Ink };
    private readonly Label _custodySub = new() { FontSize = 12.5, TextColor = Ui.Muted };
    private Border _custodyBox = null!;
    private View _header = null!;
    private bool _visible;
    private int _timerGeneration;

    public HomePage()
    {
        var logout = _app.Store.Snapshot?.Login is not null ? (Func<Task>)LogoutAsync : null;
        _header = Ui.Header(_app.Work.CompanyName, HeaderSub(), logout, () => Navigation.PushAsync(new SettingsPage()));

        _connectionBox.Content = _connection;
        _today.TextColor = Ui.Ink;
        var statChips = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) },
            Children = { Chip("النقد معك", _wallet, 0), Chip("ينتظر المعمل", _waiting, 1) }
        };
        var sales = Ui.Card(new VerticalStackLayout
        {
            Spacing = 6,
            Children = { Ui.Text("مبيعات اليوم", 13, Ui.Muted), _today, statChips }
        });

        _debtTitle.TextColor = Ui.Ink;
        _debtSub.TextColor = Ui.Muted;
        _debtAmount.TextColor = Ui.Red;
        var debtRow = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            Children = { Put(Ui.Icon("users", Ui.Red, 28), 0), Put(new VerticalStackLayout { Spacing = 0, Children = { _debtTitle, _debtSub } }, 1), Put(_debtAmount, 2) }
        };
        var debts = new Border
        {
            Content = debtRow, Padding = new Thickness(14, 12), BackgroundColor = Colors.White, Stroke = Ui.Red, StrokeThickness = Math.Max(1.5, Ui.BorderW),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 }
        };
        debts.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => App.ShowRoot(MainPages.Create("debts"))) });

        var grid = new Grid
        {
            ColumnSpacing = 10, RowSpacing = 10,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) },
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) }
        };
        void Place(View v, int row, int col) { Grid.SetRow(v, row); Grid.SetColumn(v, col); grid.Children.Add(v); }
        // اتجاه RTL: العمود الأول يمين الشاشة
        Place(Ui.ColorTile("coin", "تحصيل دين", Ui.Collect, () => Navigation.PushAsync(new CollectionPage())), 0, 0);
        Place(Ui.ColorTile("user", "زبون جديد", Ui.NewCustomer, () => Navigation.PushAsync(new NewCustomerPage())), 0, 1);
        Place(Ui.ColorTile("box", "رصيد السيارة", Ui.Stock, () => Navigation.PushAsync(new VanStockPage())), 0, 2);
        Place(Ui.ColorTile("wrench", "مصروف السيارة", Ui.Expense, () => Navigation.PushAsync(new ExpensePage())), 1, 0);
        Place(Ui.ColorTile("sync", "مزامنة الآن", Ui.SyncColor, SyncAsync), 1, 1);
        Place(Ui.ColorTile("list", "حركاتي", Ui.Moves, () => Navigation.PushAsync(new OutboxPage())), 1, 2);
        var custodyCard = Ui.Card(new VerticalStackLayout { Spacing = 2, Children = { _custodyTitle, _custodySub } }, Ui.Brand);
        custodyCard.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => Navigation.PushAsync(new CustodyExpensesPage(asRoot: false))) });
        _custodyBox = custodyCard;
        _custodyBox.IsVisible = false;
        var mine = new Border
        {
            BackgroundColor = Ui.Brand, StrokeThickness = 0, Padding = new Thickness(12, 14),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 },
            Shadow = new Shadow { Brush = new SolidColorBrush(Ui.Brand), Opacity = 0.3f, Radius = 8, Offset = new Point(0, 4) },
            Content = new VerticalStackLayout
            {
                Spacing = 2,
                Children =
                {
                    new Label { Text = "زبائني من النظام", FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = Colors.White, HorizontalTextAlignment = TextAlignment.Center },
                    new Label { Text = "اختيار زبائنك بموافقة الإدارة", FontSize = 12, TextColor = Colors.White.WithAlpha(0.9f), HorizontalTextAlignment = TextAlignment.Center }
                }
            }
        };
        mine.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await Navigation.PushAsync(new MyCustomersPage())) });

        var body = new VerticalStackLayout
        {
            Spacing = 10,
            Children = { _connectionBox, sales, Ui.Hero("cash", "بيع", "فاتورة نقدية أو آجلة", () => Navigation.PushAsync(new SalePage())), debts, _custodyBox, grid, mine }
        };
        Ui.SetupMain(this, _header, body, "home", _app.Work.UnreadCount());
    }

    private static View Put(View v, int col) { Grid.SetColumn(v, col); return v; }

    private static View Chip(string caption, Label value, int col)
    {
        value.TextColor = Ui.Navy;
        var box = new Border
        {
            BackgroundColor = Ui.Chip, StrokeThickness = Ui.Sun ? 2 : 0, Stroke = Ui.Line, Padding = new Thickness(12, 8),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
            Content = new VerticalStackLayout { Spacing = 0, Children = { Ui.Text(caption, 12, Ui.Muted), value } }
        };
        Grid.SetColumn(box, col);
        return box;
    }

    private string HeaderSub() => _app.Store.Snapshot is { } s ? $"المندوب: {s.RepName}" : "بانتظار نسخة العمل من المعمل";

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _visible = true;
        _app.Changed += Refresh;
        Refresh();
        _app.SyncSoon();
        // مؤقّت واحد لكل ظهور للصفحة (الأقدم يتوقف)
        var generation = ++_timerGeneration;
        Dispatcher.StartTimer(TimeSpan.FromSeconds(60), () =>
        {
            var alive = _visible && generation == _timerGeneration;
            if (alive) _app.SyncSoon();
            return alive;
        });
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _visible = false;
        _app.Changed -= Refresh;
    }

    private void Refresh()
    {
        // نسخة العمل لصاحب عهدة ليس مندوبًا (وصلت بعد فتح الرئيسية): تُستبدل بواجهة العهدة
        if (_app.Work.CustodyOnly) { App.ShowRoot(MainPages.Create("home")); return; }
        var custodies = _app.Work.OpenCustodies();
        _custodyBox.IsVisible = custodies.Count > 0;
        if (custodies.Count > 0)
        {
            _custodyTitle.Text = $"عهدتي: {Formats.Money(custodies.Sum(c => c.Balance))}";
            _custodySub.Text = "اضغط لتسجيل مصروف طارئ (وقود أو تصليح) أو مراجعة مصاريف العهدة";
        }
        _wallet.Text = Formats.Money(_app.Work.Wallet());
        _today.Text = Formats.Money(_app.Work.TodaySales());
        var waiting = _app.Work.WaitingCount();
        _waiting.Text = waiting == 0 ? "لا شيء ✔" : $"{waiting} حركة";
        _waiting.TextColor = waiting == 0 ? Ui.Green : Ui.Amber;
        var debtors = _app.Work.Debtors();
        var late = debtors.Count(d => d.Severity == 2);
        _debtTitle.Text = "ديون زبائني";
        _debtSub.Text = debtors.Count == 0 ? "لا ديون" : $"{debtors.Count} زبائن" + (late > 0 ? $" · منهم {late} متأخر أكثر من 30 يومًا" : "");
        _debtAmount.Text = Formats.Money(debtors.Sum(d => d.Balance));
        var last = _app.LastSync;
        var (text, color) = last switch
        {
            null => ("…", Ui.Muted),
            { Error: { } e } => (e, Ui.Red),
            { Online: false } => (NetworkHint.Mismatch(_app.Store.Link?.LocalUrl, NetworkHint.PhoneAddresses()) is { } hint
                                      ? $"لا اتصال: {hint}" : "لا اتصال — الحركات محفوظة في الهاتف وتُرسل تلقائيًا", Ui.Amber),
            { FactoryLastSeenUtc: { } seen } => ($"متصل {last.Channel} — آخر اتصال للمعمل قبل {Ago(DateTime.UtcNow - seen)}", Ui.Green),
            _ => ($"متصل بالخادم {last.Channel} — المعمل لم يتصل بعد", Ui.Amber)
        };
        _connection.Text = text;
        _connection.TextColor = color;
        _connectionBox.BackgroundColor = color.WithAlpha(0.14f);
    }

    private static string Ago(TimeSpan t) => t.TotalMinutes < 1 ? "أقل من دقيقة" : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} دقيقة" : $"{(int)t.TotalHours} ساعة";

    private async Task SyncAsync()
    {
        var r = await _app.SyncNowAsync();
        await DisplayAlert("المزامنة", r.Text, "حسنًا");
    }

    /// <summary>خروج بلا تعقيد: يعود لشاشة الدخول (اسم وكلمة مرور المندوب)، وتبقى حركاته وبياناته في الهاتف.</summary>
    private async Task LogoutAsync()
    {
        if (_app.Store.Snapshot?.Login is not { } login) return;
        if (!await DisplayAlert("خروج", "الخروج من حساب المندوب؟ حركاتك محفوظة وتُرسل للمعمل.", "خروج", "إلغاء")) return;
        App.ShowRoot(new LoginPage(login));
    }
}
