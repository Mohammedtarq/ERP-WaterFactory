using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>
/// رئيسية صاحب عهدة ليس مندوبًا (مسؤول المشتريات): شريط باسم المعمل والموظف، وحالة الاتصال، ورصيد عهدته وما صُرف له،
/// وزر «مصروف جديد»، ثم مصاريفي والمزامنة وحركاتي. المتبقي يعيده للخزينة أو يبقى في عهدته. المزامنة كل دقيقة.
/// </summary>
public class CustodyHomePage : ContentPage
{
    private readonly AppServices _app = AppServices.Instance;
    private readonly Label _connection = new() { FontSize = 13, FontAttributes = FontAttributes.Bold };
    private readonly Border _connectionBox = new() { StrokeThickness = 0, Padding = new Thickness(12, 8), StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 } };
    private readonly Label _balance = new() { FontSize = 38, FontAttributes = FontAttributes.Bold };
    private readonly Label _issued = new() { FontSize = 19, FontAttributes = FontAttributes.Bold };
    private readonly Label _waiting = new() { FontSize = 19, FontAttributes = FontAttributes.Bold };
    private readonly VerticalStackLayout _custodies = new() { Spacing = 2 };
    private bool _visible;
    private int _timerGeneration;

    public CustodyHomePage()
    {
        var logout = _app.Store.Snapshot?.Login is not null ? (Func<Task>)LogoutAsync : null;
        var header = Ui.Header(_app.Work.CompanyName, _app.Store.Snapshot is { } s ? $"الموظف: {s.RepName}" : "بانتظار نسخة العمل من المعمل", logout,
                               () => Navigation.PushAsync(new SettingsPage()));
        _connectionBox.Content = _connection;
        _balance.TextColor = Ui.Ink;
        var chips = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) },
            Children = { Chip("صُرف لي", _issued, 0), Chip("ينتظر المعمل", _waiting, 1) }
        };
        var card = Ui.Card(new VerticalStackLayout { Spacing = 6, Children = { Ui.Text("رصيد عهدتي", 13, Ui.Muted), _balance, chips, _custodies } });
        var grid = new Grid
        {
            ColumnSpacing = 10, RowSpacing = 10,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) },
            RowDefinitions = { new RowDefinition(GridLength.Auto) }
        };
        void Place(View v, int col) { Grid.SetColumn(v, col); grid.Children.Add(v); }
        Place(Ui.ColorTile("list", "مصاريفي", Ui.Expense, () => Navigation.PushAsync(new CustodyExpensesPage(asRoot: false))), 0);
        Place(Ui.ColorTile("sync", "مزامنة الآن", Ui.SyncColor, SyncAsync), 1);
        Place(Ui.ColorTile("box", "حركاتي", Ui.Moves, () => Navigation.PushAsync(new OutboxPage())), 2);
        var body = new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                _connectionBox, card,
                Ui.Hero("coin", "مصروف جديد", "سجّل مبلغًا وبابه من عهدتك", () => Navigation.PushAsync(new NewCustodyExpensePage())),
                grid,
                Ui.Text("المتبقي من العهدة يبقى معك للمصاريف القادمة، أو تسلّمه للمالية فتُسوّى العهدة من المكتب.", 12.5, Ui.Muted)
            }
        };
        Ui.SetupMain(this, header, body, "home", _app.Work.UnreadCount());
    }

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

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _visible = true;
        _app.Changed += Refresh;
        Refresh();
        _app.SyncSoon();
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
        // وصلت نسخة عمل لمندوب (ليس موظف عهدة): يُستبدل بالرئيسية الأخرى
        if (_app.Store.Snapshot is not null && !_app.Work.CustodyOnly) { App.ShowRoot(MainPages.Create("home")); return; }
        var open = _app.Work.OpenCustodies();
        _balance.Text = Formats.Money(open.Sum(c => c.Balance));
        _balance.TextColor = open.Count > 0 && open.All(c => c.IsLow) ? Ui.Red : Ui.Ink;
        _issued.Text = Formats.Money(open.Sum(c => c.Custody.Issued));
        var waiting = _app.Work.WaitingCount();
        _waiting.Text = waiting == 0 ? "لا شيء ✔" : $"{waiting} حركة";
        _waiting.TextColor = waiting == 0 ? Ui.Green : Ui.Amber;
        _custodies.Children.Clear();
        if (open.Count > 1)
            foreach (var c in open)
                _custodies.Children.Add(Ui.Text($"{c.Title}: {Formats.Money(c.Balance)}", 13.5, c.IsLow ? Ui.Red : Ui.Muted));
        else if (open.Count == 0)
            _custodies.Children.Add(Ui.Text("لا عهدة فعّالة عندك الآن — اطلبها من المالية", 13.5, Ui.Amber));
        var last = _app.LastSync;
        var (text, color) = last switch
        {
            null => ("…", Ui.Muted),
            { Error: { } e } => (e, Ui.Red),
            { Online: false } => ("لا اتصال — الحركات محفوظة في الهاتف وتُرسل تلقائيًا", Ui.Amber),
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

    private async Task LogoutAsync()
    {
        if (_app.Store.Snapshot?.Login is not { } login) return;
        if (!await DisplayAlert("خروج", "الخروج من الحساب؟ حركاتك محفوظة وتُرسل للمعمل.", "خروج", "إلغاء")) return;
        App.ShowRoot(new LoginPage(login));
    }
}
