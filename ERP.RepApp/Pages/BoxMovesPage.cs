using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>
/// حركات الصناديق للمدير المالي: يبدأ بصندوقه ويستطيع تبديل العرض إلى أي صندوق آخر للاطلاع؛ كل حركة بنوعها ومبلغها (أخضر داخل، أحمر خارج)
/// وما صرفه من هاتفه ولم يصل المعمل بعد يظهر «بانتظار المعمل». يُفتح جذرًا (من الشريط السفلي) أو صفحة فرعية.
/// </summary>
public class BoxMovesPage : ContentPage
{
    private readonly RepWork _work = AppServices.Instance.Work;
    private readonly bool _asRoot;
    private readonly Picker _picker = new() { Title = "الصندوق", FontSize = 17 };
    private readonly VerticalStackLayout _list = new() { Spacing = 8 };
    private readonly Label _balance = new() { FontSize = 30, FontAttributes = FontAttributes.Bold, TextColor = Ui.Ink };
    private List<RepWork.LiveBox> _boxes = new();

    public BoxMovesPage(bool asRoot = true, int? boxId = null)
    {
        _asRoot = asRoot;
        _boxes = _work.Boxes();
        _picker.ItemsSource = _boxes.Select(b => b.Own ? $"{b.Name} (صندوقي)" : b.Name).ToList();
        var start = boxId is int id ? _boxes.FindIndex(b => b.Id == id) : _boxes.FindIndex(b => b.Own);
        _picker.SelectedIndex = start >= 0 ? start : (_boxes.Count > 0 ? 0 : -1);
        _picker.SelectedIndexChanged += (_, _) => Fill();
        var body = new VerticalStackLayout
        {
            Spacing = 8,
            Children = { Ui.Card(new VerticalStackLayout { Spacing = 4, Children = { _picker, Ui.Text("الرصيد", 12.5, Ui.Muted), _balance }}), _list }
        };
        if (asRoot) Ui.SetupMain(this, Ui.Header("حركات الصناديق", null), body, "moves", _work.UnreadCount());
        else Ui.Setup(this, "حركات الصناديق", body);
        Fill();
    }

    private void Fill()
    {
        _list.Children.Clear();
        if (_picker.SelectedIndex < 0 || _picker.SelectedIndex >= _boxes.Count) { _balance.Text = "—"; return; }
        var box = _boxes[_picker.SelectedIndex];
        _balance.Text = Formats.Money(box.Balance);
        _balance.TextColor = box.Balance < 0 ? Ui.Red : Ui.Ink;
        var rows = _work.BoxMoves(box.Id);
        if (rows.Count == 0) _list.Children.Add(Ui.Text("لا حركات في آخر 30 يومًا", 16, Ui.Muted));
        foreach (var r in rows)
        {
            var lines = new VerticalStackLayout
            {
                Spacing = 2,
                Children =
                {
                    new Label { Text = r.Title, FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = Ui.Ink },
                    Ui.Text((string.IsNullOrWhiteSpace(r.Detail) ? "" : r.Detail + " · ") + r.Date.ToString("yyyy/MM/dd"), 12.5, Ui.Muted)
                }
            };
            if (r.Pending) lines.Children.Add(Ui.Text(r.State, 13, Ui.Amber));
            var grid = new Grid
            {
                ColumnSpacing = 10,
                ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
                Children =
                {
                    lines,
                    Put(new Label
                    {
                        Text = (r.Amount > 0 ? "+ " : "− ") + Formats.Money(Math.Abs(r.Amount)), FontSize = 17, FontAttributes = FontAttributes.Bold,
                        TextColor = r.Amount > 0 ? Ui.Green : Ui.Red, VerticalOptions = LayoutOptions.Center
                    }, 1)
                }
            };
            _list.Children.Add(Ui.Card(grid));
        }
    }

    private static View Put(View v, int col) { Grid.SetColumn(v, col); return v; }
}
