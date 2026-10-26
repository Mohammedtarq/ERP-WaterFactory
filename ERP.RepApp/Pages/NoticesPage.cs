using ERP.Cloud.Contracts;
using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>التبليغات: تجهيز الحمولة، رفض الحركات واعتمادها، تأكيد التسوية، وتنبيه النقد المتأخر. غير المقروء معلَّم، ويُعلَّم الكل مقروءًا عند الخروج من الصفحة.</summary>
public class NoticesPage : ContentPage
{
    private readonly RepWork _work = AppServices.Instance.Work;

    public NoticesPage()
    {
        var items = _work.Notices();
        var list = new VerticalStackLayout { Spacing = 10 };
        foreach (var (n, unread) in items) list.Children.Add(Row(n, unread));
        if (items.Count == 0) list.Children.Add(Ui.Text(_work.Snapshot is null ? "لم تصل نسخة العمل بعد — زامن أولًا" : "لا تبليغات الآن", 16, Ui.Muted));
        var unreadCount = items.Count(i => i.Unread);
        var body = new VerticalStackLayout { Spacing = 10, Children = { Ui.Text(unreadCount == 0 ? "كل التبليغات مقروءة" : $"{unreadCount} تبليغ غير مقروء", 14, Ui.Muted), list } };
        Ui.SetupMain(this, Ui.Header("التبليغات", "آخر 14 يومًا", null, () => Navigation.PushAsync(new SettingsPage())), body, "notices", unreadCount);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _work.MarkNoticesRead();
    }

    private static View Row(SnapshotNotice n, bool unread)
    {
        var (icon, color) = n.Kind switch
        {
            "load" => ("box", Ui.Collect), "reject" => ("power", Ui.Red), "approve" => ("coin", Ui.Collect),
            "settle" => ("list", Ui.SyncColor), "cash" => ("cash", Ui.Expense), _ => ("bell", Ui.Brand)
        };
        var tile = new Border
        {
            WidthRequest = 42, HeightRequest = 42, BackgroundColor = color, StrokeThickness = 0, VerticalOptions = LayoutOptions.Start,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 13 }, Content = Ui.Icon(icon, Colors.White, 22)
        };
        var text = new VerticalStackLayout
        {
            Spacing = 2,
            Children =
            {
                new Label { Text = n.Title, FontSize = 15, FontAttributes = FontAttributes.Bold, TextColor = Ui.Ink, LineBreakMode = LineBreakMode.WordWrap },
                Ui.Text(n.Body ?? "", 13, Ui.Muted),
                Ui.Text(When(n.At.ToLocalTime()), 12, Ui.Muted)
            }
        };
        var grid = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
            Children = { tile, Put(text, 1) }
        };
        return Ui.Card(grid, unread ? Ui.Brand : null);
    }

    private static View Put(View v, int col) { Grid.SetColumn(v, col); return v; }

    private static string When(DateTime t) =>
        t.Date == DateTime.Today ? $"اليوم {t:HH:mm}" : t.Date == DateTime.Today.AddDays(-1) ? $"أمس {t:HH:mm}" : $"{t:yyyy/MM/dd HH:mm}";
}
