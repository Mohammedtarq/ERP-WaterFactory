using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>الرئيسية: النقد مع المندوب، ومبيعات اليوم، وما ينتظر الإرسال، وحالة الاتصال بالمعمل؛ والمزامنة كل دقيقة.</summary>
public class HomePage : ContentPage
{
    private readonly AppServices _app = AppServices.Instance;
    private readonly Label _rep = Ui.Text("", 17);
    private readonly Label _company = Ui.Title("");
    private readonly Image _logo = new() { HeightRequest = 56, WidthRequest = 56, Aspect = Aspect.AspectFit, IsVisible = false };
    private byte[]? _shownLogo;
    private readonly Label _connection = Ui.Text("", 15, Ui.Muted);
    private readonly Label _wallet = Ui.Text("", 24);
    private readonly Label _today = Ui.Text("", 18);
    private readonly Label _waiting = Ui.Text("", 18);
    private bool _visible;
    private int _timerGeneration;

    public HomePage()
    {
        _wallet.FontAttributes = FontAttributes.Bold;
        _wallet.TextColor = Ui.Navy;
        _company.TextColor = Ui.Navy;
        _company.HorizontalTextAlignment = TextAlignment.Center;
        _rep.HorizontalTextAlignment = TextAlignment.Center;
        _connection.HorizontalTextAlignment = TextAlignment.Center;
        _wallet.HorizontalTextAlignment = TextAlignment.Center;
        _today.HorizontalTextAlignment = TextAlignment.Center;
        _waiting.HorizontalTextAlignment = TextAlignment.Center;
        _logo.HorizontalOptions = LayoutOptions.Center;
        var grid = new Grid
        {
            ColumnSpacing = 12, RowSpacing = 12,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) },
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) }
        };
        void Place(View v, int row, int col) { Grid.SetRow(v, row); Grid.SetColumn(v, col); grid.Children.Add(v); }
        // اتجاه RTL: العمود الأول يمين الشاشة
        Place(Ui.Tile("coin", "تحصيل دين", "استلام دفعة من زبون", Ui.Brand, () => Navigation.PushAsync(new CollectionPage())), 0, 0);
        Place(Ui.Tile("user", "زبون جديد", "إضافة زبون وموقعه", Color.FromArgb("#0D9488"), () => Navigation.PushAsync(new NewCustomerPage())), 0, 1);
        Place(Ui.Tile("wrench", "مصروف السيارة", "وقود وصيانة وغسيل", Color.FromArgb("#D97706"), () => Navigation.PushAsync(new ExpensePage())), 1, 0);
        Place(Ui.Tile("box", "رصيد السيارة", "ما تبقى من البضاعة", Color.FromArgb("#6366F1"), () => Navigation.PushAsync(new VanStockPage())), 1, 1);
        Place(Ui.Tile("list", "حركاتي", "سجل اليوم وحالة كل حركة", Color.FromArgb("#475569"), () => Navigation.PushAsync(new OutboxPage())), 2, 0);
        Place(Ui.Tile("sync", "مزامنة الآن", "إرسال واستلام من المعمل", Ui.Brand, SyncAsync), 2, 1);
        Place(Ui.Tile("user", "زبائني من النظام", "اختيار زبائنك بموافقة الإدارة", Color.FromArgb("#7C3AED"), () => Navigation.PushAsync(new MyCustomersPage())), 3, 0);

        Ui.Setup(this, "الرئيسية", new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                // بطاقة الحالة: شعار المعمل واسمه والمندوب، ثم مبيعات اليوم وحالة الإرسال
                Ui.Card(new VerticalStackLayout
                {
                    Spacing = 4,
                    Children = { _logo, _company, _rep, _today, _waiting, _connection }
                }),
                Ui.Card(new VerticalStackLayout { Children = { Ui.Text("النقد معك", 14, Ui.Muted), _wallet } }, Ui.Brand),
                Ui.Hero("cash", "بيع", "فاتورة نقدية أو آجلة", () => Navigation.PushAsync(new SalePage())),
                grid,
            }
        });
        BackgroundColor = Colors.White;
        ToolbarItems.Add(new ToolbarItem("الطابعة", null, async () =>
        {
            if (await ReceiptActions.ChoosePrinterAsync(this)) await DisplayAlert("الطابعة", ReceiptActions.PrinterText, "حسنًا");
        }, ToolbarItemOrder.Secondary));
        ToolbarItems.Add(new ToolbarItem("تغيير عنوان الخادم", null, async () => await Navigation.PushAsync(new ServerPage()), ToolbarItemOrder.Secondary));
        ToolbarItems.Add(new ToolbarItem("فك الربط", null, async () => await UnlinkAsync(), ToolbarItemOrder.Secondary));
    }

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
        var snap = _app.Work.Snapshot;
        _company.Text = _app.Work.CompanyName;
        _rep.Text = snap is null ? "بانتظار نسخة العمل من المعمل" : $"المندوب: {snap.RepName}";
        var logo = snap?.CompanyLogo;
        if (!ReferenceEquals(logo, _shownLogo) && (logo is null || _shownLogo is null || !logo.AsSpan().SequenceEqual(_shownLogo)))
        {
            _shownLogo = logo;
            _logo.Source = logo is null ? null : ImageSource.FromStream(() => new MemoryStream(logo));
            _logo.IsVisible = logo is not null;
        }
        _wallet.Text = Formats.Money(_app.Work.Wallet());
        _today.Text = $"مبيعات اليوم: {Formats.Money(_app.Work.TodaySales())}";
        var waiting = _app.Work.WaitingCount();
        _waiting.Text = waiting == 0 ? "كل الحركات وصلت المعمل ✔" : $"حركات بانتظار المعمل: {waiting}";
        _waiting.TextColor = waiting == 0 ? Ui.Green : Ui.Amber;
        var last = _app.LastSync;
        (_connection.Text, _connection.TextColor) = last switch
        {
            null => ("…", Ui.Muted),
            { Error: { } e } => (e, Ui.Red),
            { Online: false } => ("لا اتصال — الحركات محفوظة في الهاتف وتُرسل تلقائيًا", Ui.Amber),
            { FactoryLastSeenUtc: { } seen } => ($"متصل {last.Channel} — آخر اتصال للمعمل قبل {Ago(DateTime.UtcNow - seen)}", Ui.Green),
            _ => ($"متصل بالخادم {last.Channel} — المعمل لم يتصل بعد", Ui.Amber)
        };
    }

    private static string Ago(TimeSpan t) => t.TotalMinutes < 1 ? "أقل من دقيقة" : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} دقيقة" : $"{(int)t.TotalHours} ساعة";

    private async Task SyncAsync()
    {
        var r = await _app.SyncNowAsync();
        await DisplayAlert("المزامنة", r.Text, "حسنًا");
    }

    private async Task UnlinkAsync()
    {
        if (!await DisplayAlert("فك الربط", "فك ربط هذا الهاتف بالمندوب؟ (لنقل الهاتف لمندوب آخر)", "فك الربط", "إلغاء")) return;
        if (_app.Store.Unlink() is string error) { await DisplayAlert("لا يمكن الآن", error, "حسنًا"); return; }
        _app.ResetClient();
        App.ShowRoot(new LinkPage());
    }
}
