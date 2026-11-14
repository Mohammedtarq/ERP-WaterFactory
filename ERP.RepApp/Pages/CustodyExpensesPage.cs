using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>مصاريفي: رصيد العهدة وعهدها الفعّالة، وكل مصروف بحالته (رُحّل ✔ أو ينتظر المعمل). يُفتح جذرًا لصاحب العهدة أو صفحة فرعية للمندوب.</summary>
public class CustodyExpensesPage : ContentPage
{
    private readonly RepWork _work = AppServices.Instance.Work;

    public CustodyExpensesPage(bool asRoot = true)
    {
        var open = _work.OpenCustodies();
        var body = new VerticalStackLayout { Spacing = 8 };
        var summary = new VerticalStackLayout { Spacing = 3, Children = { Ui.Text("رصيد عهدتي", 13, Ui.Muted), new Label { Text = Formats.Money(open.Sum(c => c.Balance)), FontSize = 30, FontAttributes = FontAttributes.Bold, TextColor = Ui.Ink } } };
        foreach (var c in open) summary.Children.Add(Ui.Text($"{c.Title}: {Formats.Money(c.Balance)}", 14, c.IsLow ? Ui.Red : Ui.Muted));
        if (open.Count == 0) summary.Children.Add(Ui.Text("لا عهدة فعّالة عندك الآن", 14, Ui.Amber));
        body.Children.Add(Ui.Card(summary));
        if (open.Count > 0) body.Children.Add(Ui.Big("مصروف جديد", Ui.Brand, () => Navigation.PushAsync(new NewCustodyExpensePage())));
        var rows = _work.CustodyExpenses();
        if (rows.Count == 0) body.Children.Add(Ui.Text("لا مصاريف بعد", 16, Ui.Muted));
        foreach (var r in rows)
        {
            var text = new VerticalStackLayout
            {
                Spacing = 2,
                Children =
                {
                    new Label { Text = r.Category, FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = Ui.Ink },
                    Ui.Text((string.IsNullOrWhiteSpace(r.Description) ? "" : r.Description + " · ") + r.Date.ToString("yyyy/MM/dd"), 12.5, Ui.Muted),
                    Ui.Text(r.State, 13, r.Pending ? Ui.Amber : Ui.Green)
                }
            };
            var grid = new Grid
            {
                ColumnSpacing = 10,
                ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
                Children = { text, Put(new Label { Text = Formats.Money(r.Amount), FontSize = 17, FontAttributes = FontAttributes.Bold, TextColor = Ui.Red, VerticalOptions = LayoutOptions.Center }, 1) }
            };
            body.Children.Add(Ui.Card(grid));
        }
        body.Children.Add(Ui.Text("لإعادة المتبقي سلّمه للمالية، وتُسوّى العهدة من المكتب.", 12.5, Ui.Muted));
        if (asRoot) Ui.SetupMain(this, Ui.Header("مصاريفي", null), body, "expenses", _work.UnreadCount());
        else Ui.Setup(this, "مصاريفي", body);
    }

    private static View Put(View v, int col) { Grid.SetColumn(v, col); return v; }
}
