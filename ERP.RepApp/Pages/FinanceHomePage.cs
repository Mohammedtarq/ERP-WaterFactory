using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>
/// رئيسية المدير المالي: رصيد صندوقه الظاهر في النظام ووارد اليوم ومصروفه، وزر «صرف من الصندوق» (من صندوقه وحده)،
/// وقائمة الصناديق الأخرى وأرصدتها للاطلاع فقط (تفتح حركاتها)، وإجمالي الصناديق. المزامنة كل دقيقة.
/// </summary>
public class FinanceHomePage : ContentPage
{
    private readonly AppServices _app = AppServices.Instance;
    private readonly Label _connection = new() { FontSize = 13, FontAttributes = FontAttributes.Bold };
    private readonly Border _connectionBox = new() { StrokeThickness = 0, Padding = new Thickness(12, 8), StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 } };
    private readonly Label _boxName = Ui.Text("", 13, Ui.Muted);
    private readonly Label _balance = new() { FontSize = 38, FontAttributes = FontAttributes.Bold };
    private readonly Label _in = new() { FontSize = 19, FontAttributes = FontAttributes.Bold };
    private readonly Label _out = new() { FontSize = 19, FontAttributes = FontAttributes.Bold };
    private readonly Label _total = new() { FontSize = 20, FontAttributes = FontAttributes.Bold };
    private readonly VerticalStackLayout _others = new() { Spacing = 6 };
    private bool _visible;
    private int _timerGeneration;

    public FinanceHomePage()
    {
        var logout = _app.Store.Snapshot?.Login is not null ? (Func<Task>)LogoutAsync : null;
        var header = Ui.Header(_app.Work.CompanyName, _app.Store.Snapshot is { } s ? $"المدير المالي: {s.RepName}" : "بانتظار نسخة العمل من المعمل", logout,
                               () => Navigation.PushAsync(new SettingsPage()));
        _connectionBox.Content = _connection;
        _balance.TextColor = Ui.Ink;
        var chips = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) },
            Children = { Chip("وارد اليوم", _in, 0, Ui.Green), Chip("مصروف اليوم", _out, 1, Ui.Red) }
        };
        var card = Ui.Card(new VerticalStackLayout { Spacing = 6, Children = { _boxName, _balance, chips } });
        var grid = new Grid
        {
            ColumnSpacing = 10, RowSpacing = 10,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) },
            RowDefinitions = { new RowDefinition(GridLength.Auto) }
        };
        void Place(View v, int col) { Grid.SetColumn(v, col); grid.Children.Add(v); }
        Place(Ui.ColorTile("list", "حركات الصندوق", Ui.NewCustomer, () => Navigation.PushAsync(new BoxMovesPage(asRoot: false))), 0);
        Place(Ui.ColorTile("sync", "مزامنة الآن", Ui.SyncColor, SyncAsync), 1);
        Place(Ui.ColorTile("box", "حركاتي", Ui.Moves, () => Navigation.PushAsync(new OutboxPage())), 2);
        var othersCard = Ui.Card(new VerticalStackLayout
        {
            Spacing = 6,
            Children = { Ui.Text("الصناديق الأخرى (للاطلاع)", 14, Ui.Muted), _others, new BoxView { HeightRequest = Ui.BorderW, Color = Ui.Line },
                         new HorizontalStackLayout { Spacing = 8, Children = { Ui.Text("إجمالي الصناديق:", 14, Ui.Muted), _total } } }
        });
        var body = new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                _connectionBox, card,
                Ui.Hero("coin", "صرف من الصندوق", "مصروف بأي باب، يخرج من صندوقك", () => Navigation.PushAsync(new NewBoxExpensePage())),
                othersCard, grid,
                Ui.Text("الصرف من صندوقك وحده؛ الصناديق الأخرى تُعرض للاطلاع، وحركاتها تظهر بالضغط عليها.", 12.5, Ui.Muted)
            }
        };
        Ui.SetupMain(this, header, body, "home", _app.Work.UnreadCount());
    }

    private static View Chip(string caption, Label value, int col, Color color)
    {
        value.TextColor = color;
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
        // وصلت نسخة عمل لا تخص المدير المالي (أُلغيت الواجهة): يُستبدل بالرئيسية المناسبة
        if (_app.Store.Snapshot is not null && !_app.Work.FinanceMode) { App.ShowRoot(MainPages.Create("home")); return; }
        var boxes = _app.Work.Boxes();
        var own = boxes.FirstOrDefault(b => b.Own);
        _boxName.Text = own is null ? "صندوقي" : $"صندوقي: {own.Name}";
        _balance.Text = Formats.Money(own?.Balance ?? 0);
        _balance.TextColor = own is not null && own.Balance <= 0 ? Ui.Red : Ui.Ink;
        var (inToday, outToday) = own is null ? (0m, 0m) : _app.Work.BoxToday(own.Id);
        _in.Text = inToday == 0 ? "—" : "+ " + Formats.Money(inToday);
        _out.Text = outToday == 0 ? "—" : "− " + Formats.Money(outToday);
        _total.Text = Formats.Money(_app.Work.BoxesTotal());
        _total.TextColor = Ui.Ink;
        _others.Children.Clear();
        foreach (var b in boxes.Where(x => !x.Own))
        {
            var row = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
                Children =
                {
                    Ui.Text(b.Name, 15, Ui.Ink),
                    Put(new Label { Text = Formats.Money(b.Balance), FontSize = 15, FontAttributes = FontAttributes.Bold, TextColor = b.Balance < 0 ? Ui.Red : Ui.Ink }, 1)
                }
            };
            var id = b.Id;
            row.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => Navigation.PushAsync(new BoxMovesPage(asRoot: false, boxId: id))) });
            _others.Children.Add(row);
        }
        if (_others.Children.Count == 0) _others.Children.Add(Ui.Text("لا صناديق أخرى", 14, Ui.Muted));
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

    private static View Put(View v, int col) { Grid.SetColumn(v, col); return v; }
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
