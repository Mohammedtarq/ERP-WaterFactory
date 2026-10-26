using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>
/// الإعدادات: المظهر (فاتح الافتراضي، و«شمس» لوضوح أفضل تحت الضوء المباشر)، والطابعة وعرض الورق وطباعة تجريبية،
/// وعنوان الخادم، وفك الربط (منطقة حمراء منفصلة).
/// </summary>
public class SettingsPage : ContentPage
{
    private readonly AppServices _app = AppServices.Instance;
    private readonly Label _printer = Ui.Text("", 14, Ui.Muted);

    public SettingsPage()
    {
        var snap = _app.Store.Snapshot;
        var who = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
            Children =
            {
                new Border
                {
                    WidthRequest = 46, HeightRequest = 46, BackgroundColor = Ui.Brand, StrokeThickness = 0, StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 23 },
                    Content = Ui.Icon("user", Colors.White, 24)
                },
                Put(new VerticalStackLayout
                {
                    VerticalOptions = LayoutOptions.Center,
                    Children = { Ui.Text(snap?.RepName ?? "المندوب", 17, Ui.Ink), Ui.Text($"مرتبط بـ {_app.Work.CompanyName}", 12.5, Ui.Muted) }
                }, 1)
            }
        };

        var light = ThemeButton(false);
        var sun = ThemeButton(true);
        Grid.SetColumn(sun, 1);
        var theme = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) },
            Children = { light, sun }
        };

        var printerRow = Row("list", "الطابعة", _printer, async () =>
        {
            if (await ReceiptActions.ChoosePrinterAsync(this)) Refresh();
        });
        var server = Row("sync", "عنوان الخادم", Ui.Text(_app.Store.Link?.ServerUrl ?? "—", 13, Ui.Muted), () => Navigation.PushAsync(new ServerPage()));
        var unlink = new Border
        {
            Padding = new Thickness(14, 12), BackgroundColor = Colors.White, Stroke = Ui.Red, StrokeThickness = Math.Max(1.5, Ui.BorderW),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 },
            Content = new VerticalStackLayout
            {
                Spacing = 2,
                Children = { new Label { Text = "فك الربط", FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = Ui.Red }, Ui.Text("يفصل هذا الهاتف عن المعمل ويحتاج رمز ربط جديدًا (لنقله لمندوب آخر).", 12.5, Ui.Muted) }
            }
        };
        unlink.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await UnlinkAsync()) });

        Ui.Setup(this, "الإعدادات", new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Ui.Card(who),
                Ui.Text("المظهر", 13, Ui.Muted),
                theme,
                Ui.Text("«شمس»: ألوان وحدود أقوى لوضوح أفضل تحت الضوء المباشر.", 12, Ui.Muted),
                Ui.Text("الطباعة", 13, Ui.Muted),
                printerRow,
                Ui.Small("طباعة وصل تجريبي", Ui.Brand, async () => await ReceiptActions.PrintSampleAsync(this)),
                Ui.Text("الاتصال", 13, Ui.Muted),
                server,
                unlink
            }
        });
        Refresh();
    }

    private static View Put(View v, int col) { Grid.SetColumn(v, col); return v; }

    private void Refresh() => _printer.Text = ReceiptActions.PrinterText;

    private Button ThemeButton(bool sun)
    {
        var on = Ui.Sun == sun;
        var b = new Button
        {
            Text = sun ? "شمس" : "فاتح", FontSize = 15, FontAttributes = FontAttributes.Bold, HeightRequest = 48, CornerRadius = 12,
            BackgroundColor = on ? Ui.Brand : Ui.Chip, TextColor = on ? Colors.White : Ui.Muted
        };
        b.Clicked += (_, _) =>
        {
            if (Ui.Sun == sun) return;
            _app.Store.Set(Ui.ThemeKey, sun ? "sun" : "light");
            Ui.ApplyTheme(sun);
            App.ShowRoot(new HomePage());          // الصفحات تُبنى بألوان المظهر الجديد
        };
        return b;
    }

    private static View Row(string icon, string title, View value, Func<Task> onTap)
    {
        var grid = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
            Children =
            {
                new Border
                {
                    WidthRequest = 38, HeightRequest = 38, BackgroundColor = Ui.Chip, StrokeThickness = 0, StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 11 },
                    Content = Ui.Icon(icon, Ui.Brand, 20)
                },
                Put(new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Children = { Ui.Text(title, 15, Ui.Ink), value } }, 1)
            }
        };
        var card = Ui.Card(grid);
        card.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await onTap()) });
        return card;
    }

    private async Task UnlinkAsync()
    {
        if (!await DisplayAlert("فك الربط", "فك ربط هذا الهاتف بالمندوب؟ (لنقل الهاتف لمندوب آخر)", "فك الربط", "إلغاء")) return;
        if (_app.Store.Unlink() is string error) { await DisplayAlert("لا يمكن الآن", error, "حسنًا"); return; }
        _app.ResetClient();
        App.ShowRoot(new LinkPage());
    }
}
