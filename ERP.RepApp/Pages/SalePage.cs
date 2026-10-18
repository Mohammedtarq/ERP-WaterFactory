using ERP.Cloud.Contracts;
using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>
/// البيع: نقدي أو آجل أو مجاني ← الزبون ← اختيار صنف من المحمّل في السيارة (بعبوته، مثل «330 مل — كارتون 40») ← العدد ← إضافة
/// ← الإجمالي والتنبيهات ← حفظ. السعر من نسخة العمل (للعرض)، والمعمل يسعّر بنفسه عند الترحيل — لا خصم من الهاتف.
/// </summary>
public class SalePage : ContentPage
{
    private readonly RepWork _work = AppServices.Instance.Work;
    private readonly CustomerPicker _customer;
    private readonly Picker _item = new() { Title = "اختر المادة من السيارة", FontSize = 17 };
    private readonly Entry _qty = new() { Text = "1", Keyboard = Keyboard.Numeric, FontSize = 22, WidthRequest = 80, HorizontalTextAlignment = TextAlignment.Center };
    private readonly Label _itemPrice = Ui.Text("", 14, Ui.Muted);
    private readonly VerticalStackLayout _cart = new() { Spacing = 4 };
    private readonly List<SaleLine> _lines = new();
    private List<LoadedOption> _options = new();
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
        _item.SelectedIndexChanged += (_, _) => ShowItemPrice();
        _qty.TextChanged += (_, _) => ShowItemPrice();           // الباقي في السيارة وسعر السطر يتحدّثان مع كل + و−
        var snap = _work.Snapshot;
        var minus = Ui.Small("−", Ui.Muted, () => _qty.Text = Math.Max(1, (Ui.Number(_qty.Text) ?? 1) - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var plus = Ui.Small("+", Ui.Primary, () => _qty.Text = ((Ui.Number(_qty.Text) ?? 0) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var picking = Ui.Card(new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                _item, _itemPrice,
                new HorizontalStackLayout { Spacing = 6, Children = { Ui.Text("العدد", 17), minus, _qty, plus } },
                Ui.Small("إضافة للفاتورة", Ui.Primary, Add)
            }
        });
        FillOptions();

        Ui.Setup(this, "بيع", new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                new HorizontalStackLayout { Spacing = 8, Children = { _kinds[0], _kinds[1], _kinds[2] } },
                Ui.Card(_customer),
                _freeReason,
                Ui.Text("المواد", 18),
                snap is null ? Ui.Text("لم تصل نسخة العمل بعد — زامن أولًا", 16, Ui.Red)
                    : _options.Count == 0 ? Ui.Text("لا توجد مواد محمّلة في السيارة", 16, Ui.Red) : picking,
                _cart,
                Ui.Card(new VerticalStackLayout { Children = { _total, _warning } }, Ui.Primary),
                Ui.Big("حفظ البيع", Ui.Primary, SaveAsync)
            }
        });
        SetKind("CashSale");
    }

    /// <summary>المحمّل في السيارة الآن بعد ما أُضيف للفاتورة.</summary>
    private void FillOptions()
    {
        var keep = _item.SelectedItem as LoadedOption;
        _options = _work.LoadedOptions();
        _item.ItemsSource = _options;
        _item.SelectedIndex = keep is null ? (_options.Count > 0 ? 0 : -1)
            : _options.FindIndex(o => o.Product.ItemId == keep.Product.ItemId && o.Level.LevelId == keep.Level.LevelId && o.RecipeId == keep.RecipeId);
        ShowItemPrice();
    }

    private void ShowItemPrice()
    {
        var snap = _work.Snapshot;
        if (snap is null || _item.SelectedItem is not LoadedOption o) { _itemPrice.Text = ""; return; }
        var inCart = _lines.Where(l => l.ItemId == o.Product.ItemId && l.RecipeId == o.RecipeId).Sum(l => l.Quantity * (o.Product.Levels.FirstOrDefault(x => x.LevelId == l.LevelId)?.Units ?? 0));
        // العدد المكتوب الآن يُطرح فورًا من الباقي (قبل «إضافة للفاتورة») حتى يرى المندوب ما سيبقى
        var typing = Math.Max(0, Ui.Number(_qty.Text) ?? 0);
        var leftPieces = o.VanPieces - inCart - typing * o.Level.Units;
        var left = Formats.Packs(Math.Max(0, leftPieces), o.Product.Levels);
        var price = RepWork.PackPrice(o.Product, o.Level, o.RecipeId);
        _itemPrice.Text = (_kind == "Free" ? "مجاني"
            : $"سعر ال{o.Level.Name}: {Formats.Money(price)}" + (typing > 0 ? $" × {Formats.Qty(typing)} = {Formats.Money(price * typing)}" : ""))
            + $"\nالباقي في السيارة بعد هذا: {left}" + (leftPieces < 0 ? " — العدد أكبر من الموجود" : "");
        _itemPrice.TextColor = leftPieces < 0 ? Ui.Red : Ui.Muted;
    }

    private void Add()
    {
        if (_item.SelectedItem is not LoadedOption o) return;
        var q = Ui.Number(_qty.Text) ?? 0;
        if (q <= 0) { _warning.Text = "اكتب العدد"; _warning.TextColor = Ui.Red; return; }
        var i = _lines.FindIndex(l => l.ItemId == o.Product.ItemId && l.LevelId == o.Level.LevelId && l.RecipeId == o.RecipeId);
        if (i >= 0) _lines[i] = _lines[i] with { Quantity = _lines[i].Quantity + q };
        else _lines.Add(new SaleLine(o.Product.ItemId, o.Level.LevelId, q, o.RecipeId));
        _qty.Text = "1";
        Recalc();
    }

    private void FillCart(DraftCheck? check)
    {
        _cart.Children.Clear();
        var snap = _work.Snapshot;
        if (snap is null) return;
        foreach (var line in _lines.ToList())
        {
            var p = snap.Products.First(x => x.ItemId == line.ItemId);
            var lvl = p.Levels.First(x => x.LevelId == line.LevelId);
            var priced = check?.Lines.FirstOrDefault(x => x.Product.ItemId == line.ItemId && x.Level.LevelId == line.LevelId && x.RecipeId == line.RecipeId);
            var text = $"{Formats.Qty(line.Quantity)} × {p.Label(lvl, line.RecipeId)}" + (priced is null ? "" : $" = {Formats.Money(priced.Total)}");
            var remove = Ui.Small("حذف", Ui.Red, () => { _lines.Remove(line); Recalc(); });
            _cart.Children.Add(Ui.Card(new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
                Children = { Ui.Text(text, 17), WithColumn(remove, 1) }
            }));
        }
    }

    private static View WithColumn(View v, int column) { Grid.SetColumn(v, column); return v; }

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

    private void Recalc()
    {
        ShowItemPrice();
        if (_work.Snapshot is null) return;
        if (_customer.Selected is null)
        {
            FillCart(null);
            _total.Text = "الإجمالي: —";
            (_warning.Text, _warning.TextColor) = (_lines.Count > 0 ? "اختر الزبون" : "", Ui.Amber);
            return;
        }
        var check = _work.CheckSale(_kind, _customer.Selected.Id, _lines, _freeReason.Text);
        FillCart(check);
        _total.Text = $"الإجمالي: {Formats.Money(check.Total)}";
        _warning.Text = _lines.Count == 0 ? "" : check.Error ?? check.Warning ?? "";
        _warning.TextColor = check.Error is null ? Ui.Amber : Ui.Red;
    }

    private async Task SaveAsync()
    {
        if (_customer.Selected is not { } customer) { await DisplayAlert("بيع", "اختر الزبون", "حسنًا"); return; }
        var lines = _lines.ToList();
        var check = _work.CheckSale(_kind, customer.Id, lines, _freeReason.Text);
        if (!check.Ok) { await DisplayAlert("لا يمكن الحفظ", check.Error, "حسنًا"); return; }
        var label = _kind switch { "CashSale" => "بيع نقدي", "CreditSale" => "بيع آجل", _ => "مجاني" };
        var details = string.Join("\n", check.Lines.Select(l => l.Text));
        var message = $"{label} — {customer.Name}\n{details}\nالإجمالي: {Formats.Money(check.Total)}" + (check.Warning is null ? "" : $"\n\n⚠ {check.Warning}");
        if (!await DisplayAlert("تأكيد البيع", message, "حفظ", "رجوع")) return;
        var saved = _work.SaveSale(_kind, customer.Id, lines, _freeReason.Text);
        AppServices.Instance.SyncSoon();
        await ReceiptActions.OfferAsync(this, saved);           // يُرسل للمعمل تلقائيًا، والوصل يُطبع الآن أو لاحقًا من «حركاتي»
        await Navigation.PopAsync();
    }
}
