using ERP.Cloud.Contracts;
using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>
/// البيع: نقدي أو آجل أو مجاني ← الزبون ← الأصناف بالعبوة (+ / −) ← الإجمالي والتنبيهات ← حفظ.
/// السعر من نسخة العمل (للعرض)، والمعمل يسعّر بنفسه عند الترحيل — لا خصم من الهاتف.
/// </summary>
public class SalePage : ContentPage
{
    private readonly RepWork _work = AppServices.Instance.Work;
    private readonly CustomerPicker _customer;
    private readonly Dictionary<int, (Picker level, Entry qty, Label price)> _rows = new();
    private readonly Entry _freeReason = new() { Placeholder = "سبب المجاني (مثل: عينة، هدية للمحل)", FontSize = 16, IsVisible = false };
    private readonly Label _total = Ui.Text("", 22);
    private readonly Label _warning = Ui.Text("", 15, Ui.Amber);
    private readonly Button[] _kinds;
    private string _kind = "CashSale";

    public SalePage()
    {
        _customer = new CustomerPicker(_work);
        _customer.SelectionChanged += Recalc;
        _total.FontAttributes = FontAttributes.Bold;
        _kinds = new[] { KindButton("نقدي", "CashSale"), KindButton("آجل", "CreditSale"), KindButton("مجاني", "Free") };
        var products = new VerticalStackLayout { Spacing = 4 };
        var snap = _work.Snapshot;
        var van = _work.VanStock();
        foreach (var p in snap?.Products ?? new())
            products.Children.Add(ProductRow(p, van.GetValueOrDefault(p.ItemId)));

        Ui.Setup(this, "بيع", new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                new HorizontalStackLayout { Spacing = 8, Children = { _kinds[0], _kinds[1], _kinds[2] } },
                Ui.Card(_customer),
                _freeReason,
                Ui.Text("الأصناف", 18),
                snap is null ? Ui.Text("لم تصل نسخة العمل بعد — زامن أولًا", 16, Ui.Red) : products,
                Ui.Card(new VerticalStackLayout { Children = { _total, _warning } }, Ui.Primary),
                Ui.Big("حفظ البيع", Ui.Primary, SaveAsync)
            }
        });
        SetKind("CashSale");
    }

    private Button KindButton(string text, string kind)
    {
        var b = new Button { Text = text, FontSize = 17, HeightRequest = 50, WidthRequest = 100, CornerRadius = 10 };
        b.Clicked += (_, _) => SetKind(kind);
        return b;
    }

    private void SetKind(string kind)
    {
        _kind = kind;
        var keys = new[] { "CashSale", "CreditSale", "Free" };
        for (var i = 0; i < 3; i++)
        {
            var on = keys[i] == kind;
            _kinds[i].BackgroundColor = on ? Ui.Primary : Colors.White;
            _kinds[i].TextColor = on ? Colors.White : Ui.Ink;
        }
        _freeReason.IsVisible = kind == "Free";
        Recalc();
    }

    private View ProductRow(SnapshotProduct p, decimal vanPieces)
    {
        var level = new Picker { FontSize = 16, WidthRequest = 110, ItemsSource = p.Levels.Select(l => l.Name).ToList(), SelectedIndex = 0 };
        var qty = new Entry { Text = "0", Keyboard = Keyboard.Numeric, FontSize = 20, WidthRequest = 70, HorizontalTextAlignment = TextAlignment.Center };
        var price = Ui.Text("", 14, Ui.Muted);
        var minus = Ui.Small("−", Ui.Muted, () => qty.Text = Math.Max(0, (Ui.Number(qty.Text) ?? 0) - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var plus = Ui.Small("+", Ui.Primary, () => qty.Text = ((Ui.Number(qty.Text) ?? 0) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        qty.TextChanged += (_, _) => Recalc();
        level.SelectedIndexChanged += (_, _) => Recalc();
        _rows[p.ItemId] = (level, qty, price);
        return Ui.Card(new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                Ui.Text(p.Name, 18),
                Ui.Text($"في السيارة: {Formats.Packs(vanPieces, p.Levels)}", 14, Ui.Muted),
                new HorizontalStackLayout { Spacing = 6, Children = { level, minus, qty, plus } },
                price
            }
        });
    }

    private List<SaleLine> Lines()
    {
        var snap = _work.Snapshot;
        if (snap is null) return new();
        var lines = new List<SaleLine>();
        foreach (var p in snap.Products)
        {
            if (!_rows.TryGetValue(p.ItemId, out var r)) continue;
            var q = Ui.Number(r.qty.Text) ?? 0;
            if (q == 0 || r.level.SelectedIndex < 0) continue;
            lines.Add(new SaleLine(p.ItemId, p.Levels[r.level.SelectedIndex].LevelId, q));
        }
        return lines;
    }

    private void Recalc()
    {
        var snap = _work.Snapshot;
        if (snap is null) return;
        // سعر العبوة المختارة لهذا الزبون
        foreach (var p in snap.Products)
        {
            if (!_rows.TryGetValue(p.ItemId, out var r) || r.level.SelectedIndex < 0) continue;
            var lvl = p.Levels[r.level.SelectedIndex];
            r.price.Text = _kind == "Free" ? "مجاني"
                : _customer.Selected is { } c ? $"سعر ال{lvl.Name}: {Formats.Money(Math.Round(RepWork.PiecePrice(snap, c.Customer, p) * lvl.Units, 2))}"
                : "اختر الزبون لعرض السعر";
        }
        if (_customer.Selected is null) { _total.Text = "الإجمالي: —"; _warning.Text = ""; return; }
        var check = _work.CheckSale(_kind, _customer.Selected.Id, Lines(), _freeReason.Text);
        _total.Text = $"الإجمالي: {Formats.Money(check.Total)}";
        _warning.Text = check.Error ?? check.Warning ?? "";
        _warning.TextColor = check.Error is null ? Ui.Amber : Ui.Red;
    }

    private async Task SaveAsync()
    {
        if (_customer.Selected is not { } customer) { await DisplayAlert("بيع", "اختر الزبون", "حسنًا"); return; }
        var lines = Lines();
        var check = _work.CheckSale(_kind, customer.Id, lines, _freeReason.Text);
        if (!check.Ok) { await DisplayAlert("لا يمكن الحفظ", check.Error, "حسنًا"); return; }
        var label = _kind switch { "CashSale" => "بيع نقدي", "CreditSale" => "بيع آجل", _ => "مجاني" };
        var details = string.Join("\n", check.Lines.Select(l => l.Text));
        var message = $"{label} — {customer.Name}\n{details}\nالإجمالي: {Formats.Money(check.Total)}" + (check.Warning is null ? "" : $"\n\n⚠ {check.Warning}");
        if (!await DisplayAlert("تأكيد البيع", message, "حفظ", "رجوع")) return;
        _work.SaveSale(_kind, customer.Id, lines, _freeReason.Text);
        AppServices.Instance.SyncSoon();
        await DisplayAlert("تم الحفظ", "حُفظ البيع في الهاتف ويُرسل للمعمل تلقائيًا", "حسنًا");
        await Navigation.PopAsync();
    }
}
