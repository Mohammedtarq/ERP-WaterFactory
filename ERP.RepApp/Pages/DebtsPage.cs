using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>
/// ديون زبائني: الإجمالي وعدد المتأخرين، وبحث وفلتر (الكل، متأخر، تجاوز الحد)؛ ولكل زبون دينه وعمره وآخر دفعة ونسبته من حده
/// وزر «تحصيل». الأرقام حيّة: دين نسخة العمل مع حركات الهاتف التي لم تصل المعمل بعد.
/// </summary>
public class DebtsPage : ContentPage
{
    private readonly RepWork _work = AppServices.Instance.Work;
    private readonly SearchBar _search = new() { Placeholder = "ابحث عن زبون", FontSize = 16 };
    private readonly VerticalStackLayout _list = new() { Spacing = 10 };
    private readonly Label _total = new() { FontSize = 32, FontAttributes = FontAttributes.Bold, TextColor = Colors.White };
    private readonly Label _sub = new() { FontSize = 13, TextColor = Colors.White };
    private readonly Label _late = new() { FontSize = 18, FontAttributes = FontAttributes.Bold, TextColor = Colors.White, HorizontalTextAlignment = TextAlignment.End };
    private string _filter = "all";
    private readonly Dictionary<string, Button> _chips = new();

    public DebtsPage()
    {
        _search.TextChanged += (_, _) => Fill();
        var summary = new Border
        {
            BackgroundColor = Ui.Brand, StrokeThickness = 0, Padding = new Thickness(16, 14),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 20 },
            Content = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
                Children =
                {
                    new VerticalStackLayout { Children = { new Label { Text = "إجمالي ديون زبائني", FontSize = 13, TextColor = Colors.White }, _total } },
                    Put(new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Children = { _sub, _late } }, 1)
                }
            }
        };
        var filters = new HorizontalStackLayout { Spacing = 8 };
        foreach (var (key, text) in new[] { ("all", "الكل"), ("late", "متأخر"), ("over", "تجاوز الحد") })
        {
            var b = new Button { Text = text, FontSize = 13, FontAttributes = FontAttributes.Bold, HeightRequest = 38, CornerRadius = 12, Padding = new Thickness(14, 0) };
            var k = key;
            b.Clicked += (_, _) => { _filter = k; Fill(); };
            _chips[key] = b;
            filters.Children.Add(b);
        }
        var body = new VerticalStackLayout { Spacing = 10, Children = { summary, _search, filters, _list } };
        Ui.SetupMain(this, Ui.Header("ديون زبائني", _work.Snapshot is null ? null : "الأكبر دينًا أولًا", null, () => Navigation.PushAsync(new SettingsPage())),
                     body, "debts", _work.UnreadCount());
        Fill();
    }

    private static View Put(View v, int col) { Grid.SetColumn(v, col); return v; }

    private void Fill()
    {
        var all = _work.Debtors();
        _total.Text = Formats.Money(all.Sum(d => d.Balance));
        var late = all.Count(d => d.Severity == 2);
        _sub.Text = $"{all.Count} زبائن";
        _late.Text = late == 0 ? "لا متأخر" : $"{late} متأخر";
        foreach (var (key, b) in _chips)
        {
            var on = key == _filter;
            b.BackgroundColor = on ? Ui.Brand : Ui.Chip;
            b.TextColor = on ? Colors.White : Ui.Muted;
        }
        var q = (_search.Text ?? "").Trim();
        IEnumerable<RepWork.Debtor> rows = all;
        if (_filter == "late") rows = rows.Where(d => d.Severity >= 1);
        if (_filter == "over") rows = rows.Where(d => d.Customer.OverLimit);
        if (q.Length > 0) rows = rows.Where(d => d.Customer.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || (d.Customer.Customer.Phone ?? "").Contains(q));
        _list.Children.Clear();
        var shown = rows.Take(60).ToList();
        foreach (var d in shown) _list.Children.Add(Row(d));
        if (shown.Count == 0)
            _list.Children.Add(Ui.Text(_work.Snapshot is null ? "لم تصل نسخة العمل بعد — زامن أولًا" : all.Count == 0 ? "لا ديون على زبائنك" : "لا نتائج", 16, Ui.Muted));
    }

    private View Row(RepWork.Debtor d)
    {
        var id = d.Customer.Id;
        var title = new HorizontalStackLayout { Spacing = 8, Children = { new Label { Text = d.Customer.Name, FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = Ui.Ink, VerticalOptions = LayoutOptions.Center } } };
        if (d.AgeDays is int age)
        {
            var (back, fore) = d.Severity switch { 2 => (Ui.Red, Colors.White), 1 => (Ui.Amber, Colors.White), _ => (Ui.Chip, Ui.Muted) };
            title.Children.Add(Ui.Pill($"{age} يومًا", back, fore));
        }
        var info = new VerticalStackLayout { Spacing = 2, Children = { title } };
        if (d.LastPaymentAt is DateTime p) info.Children.Add(Ui.Text($"آخر دفعة: {p:yyyy/MM/dd}", 12, Ui.Muted));
        else info.Children.Add(Ui.Text("لا دفعات مسجلة", 12, Ui.Muted));
        if (d.UsedFraction is double used)
            info.Children.Add(new VerticalStackLayout
            {
                Spacing = 3,
                Children = { Ui.Bar(used, used >= 0.9 ? Ui.Red : used >= 0.5 ? Ui.Amber : Ui.Brand, 7), Ui.Text($"الحد {Formats.Money(d.Limit!.Value)}", 11.5, d.Customer.OverLimit ? Ui.Red : Ui.Muted) }
            });
        var amount = new Label { Text = Formats.Money(d.Balance), FontSize = 17, FontAttributes = FontAttributes.Bold, TextColor = Ui.Red, VerticalOptions = LayoutOptions.Start };
        var collect = Ui.Small("تحصيل", Ui.Collect, async () => await Navigation.PushAsync(new CollectionPage(id)));
        collect.HeightRequest = 40;
        collect.FontSize = 14;
        var top = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            Children = { info, Put(amount, 1) }
        };
        return Ui.Card(new VerticalStackLayout { Spacing = 8, Children = { top, collect } }, d.Severity == 2 ? Ui.Red : null);
    }
}
