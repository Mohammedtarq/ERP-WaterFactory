using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>
/// بيع في شاشة واحدة: النوع (نقدي/آجل/مجاني)، الزبون ودينه وحده، ثم منتجات السيارة بطاقات بأزرار + و−، وشريط سفلي ثابت بالإجمالي وحفظ الوصل.
/// ما نفد من السيارة لا يظهر. الدين يظهر فور اختيار الزبون. تأكيد أخير قبل الحفظ.
/// </summary>
public class SalePage : ContentPage
{
    private readonly RepWork _work = AppServices.Instance.Work;
    private readonly CustomerPicker _customer;
    private readonly Button[] _kinds;
    private readonly SearchBar _search = new() { Placeholder = "ابحث عن منتج", FontSize = 16 };
    private readonly VerticalStackLayout _products = new() { Spacing = 8 };
    private readonly Entry _freeReason = new() { Placeholder = "سبب المجاني (مثل: عينة، هدية للمحل)", FontSize = 16, IsVisible = false };
    private readonly Label _summary = Ui.Text("", 13, Ui.Muted);
    private readonly Label _total = new() { FontSize = 26, FontAttributes = FontAttributes.Bold, TextColor = Ui.Navy };
    private readonly Label _warning = Ui.Text("", 13, Ui.Amber);
    private readonly Border _debtBox = new() { IsVisible = false, Padding = new Thickness(12, 10), StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 }, StrokeThickness = 1.5 };
    private readonly Label _debtTitle = new() { FontSize = 15, FontAttributes = FontAttributes.Bold };
    private readonly Label _debtSub = new() { FontSize = 12.5, TextColor = Ui.Muted };
    private readonly VerticalStackLayout _debtBar = new();
    private readonly Button _save;
    private readonly List<SaleLine> _lines = new();
    private List<LoadedOption> _options = new();
    private string _kind = "CashSale";

    /// <param name="customerId">زبون محدَّد مسبقًا.</param>
    public SalePage(int? customerId = null)
    {
        _customer = new CustomerPicker(_work);
        _customer.SelectionChanged += () => { ShowDebt(); Recalc(); };
        _kinds = new[] { KindButton("نقدي", "CashSale"), KindButton("آجل", "CreditSale"), KindButton("مجاني", "Free") };
        _search.TextChanged += (_, _) => ApplySearch();
        _freeReason.TextChanged += (_, _) => Recalc();
        _debtBox.Content = new VerticalStackLayout { Spacing = 3, Children = { _debtTitle, _debtSub, _debtBar } };
        _save = new Button
        {
            Text = "حفظ وطباعة الوصل", FontSize = 18, FontAttributes = FontAttributes.Bold, HeightRequest = 56, CornerRadius = 16,
            BackgroundColor = Ui.Green, TextColor = Colors.White
        };
        _save.Clicked += async (_, _) => { _save.IsEnabled = false; try { await SaveAsync(); } finally { _save.IsEnabled = true; } };

        var snap = _work.Snapshot;
        var kinds = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) } };
        for (var i = 0; i < 3; i++) { Grid.SetColumn(_kinds[i], i); kinds.Children.Add(_kinds[i]); }

        var content = new VerticalStackLayout
        {
            Spacing = 8, Padding = new Thickness(14, 12),
            Children =
            {
                kinds,
                Ui.Card(_customer),
                _debtBox,
                _freeReason,
                snap is null ? Ui.Text("لم تصل نسخة العمل بعد — زامن أولًا", 16, Ui.Red) : _search,
                _products
            }
        };
        var dock = new VerticalStackLayout
        {
            Spacing = 4, Padding = new Thickness(14, 10, 14, 14), BackgroundColor = Colors.White,
            Children =
            {
                new BoxView { HeightRequest = Ui.BorderW, Color = Ui.Line, Margin = new Thickness(-14, -10, -14, 4) },
                new Grid
                {
                    ColumnSpacing = 8,
                    ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
                    Children = { _summary, Put(_total, 1) }
                },
                _warning,
                _save
            }
        };
        var scroll = new ScrollView { Content = content };
        var root = new Grid { RowDefinitions = { new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) } };
        Grid.SetRow(scroll, 0); Grid.SetRow(dock, 1);
        root.Children.Add(scroll); root.Children.Add(dock);
        Title = "بيع";
        FlowDirection = FlowDirection.RightToLeft;
        BackgroundColor = Ui.PageBg;
        Content = root;
        if (customerId is int id) _customer.Select(id);
        SetKind("CashSale");
    }

    private static View Put(View v, int col) { Grid.SetColumn(v, col); return v; }

    private Button KindButton(string text, string kind)
    {
        var b = new Button { Text = text, FontSize = 16, FontAttributes = FontAttributes.Bold, HeightRequest = 48, CornerRadius = 14 };
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
            _kinds[i].BackgroundColor = on ? Ui.Brand : Colors.White;
            _kinds[i].TextColor = on ? Colors.White : Ui.Ink;
            _kinds[i].BorderColor = Ui.Line;
            _kinds[i].BorderWidth = on ? 0 : Ui.BorderW;
        }
        _freeReason.IsVisible = kind == "Free";
        Fill();
        Recalc();
    }

    /// <summary>دين الزبون وحدّه فور اختياره: الأحمر عند التجاوز، والأصفر عند القرب من الحد.</summary>
    private void ShowDebt()
    {
        var c = _customer.Selected;
        if (c is null || c.Balance <= 0) { _debtBox.IsVisible = false; return; }
        var limit = c.Customer.CreditLimit is > 0 ? c.Customer.CreditLimit : null;
        var used = limit is decimal cap ? (double)(c.Balance / cap) : (double?)null;
        var color = c.OverLimit ? Ui.Red : used is >= 0.8 ? Ui.Amber : Ui.Brand;
        _debtBox.IsVisible = true;
        _debtBox.Stroke = color;
        _debtBox.BackgroundColor = color.WithAlpha(0.1f);
        _debtTitle.Text = $"عليه دين {Formats.Money(c.Balance)}";
        _debtTitle.TextColor = color;
        _debtSub.Text = limit is decimal l ? $"الحد {Formats.Money(l)} — المتبقي للآجل {Formats.Money(Math.Max(0, l - c.Balance))}" : "بلا حد دين مُحدَّد";
        _debtBar.Children.Clear();
        if (used is double u) _debtBar.Children.Add(Ui.Bar(u, color, 7));
    }

    private decimal RemainingPieces(LoadedOption o)
    {
        var inCart = _lines.Where(l => l.ItemId == o.Product.ItemId && l.RecipeId == o.RecipeId)
                           .Sum(l => l.Quantity * (o.Product.Levels.FirstOrDefault(x => x.LevelId == l.LevelId)?.Units ?? 0));
        return o.VanPieces - inCart;
    }

    private SaleLine? LineOf(LoadedOption o) =>
        _lines.FirstOrDefault(l => l.ItemId == o.Product.ItemId && l.LevelId == o.Level.LevelId && l.RecipeId == o.RecipeId);

    private readonly Label _empty = Ui.Text("", 16, Ui.Muted);

    /// <summary>
    /// بطاقة لكل منتج في السيارة (الأساسي ثم المتغيرات) تُبنى مرة واحدة؛ البحث يخفيها ويُظهرها فقط، والضغط على + أو − أو كتابة الرقم
    /// يعيد بناء بطاقات المنتج نفسه وحدها (لا القائمة كلها) فيبقى التطبيق سريعًا.
    /// </summary>
    private void Fill()
    {
        _products.Children.Clear();
        if (_work.Snapshot is null) return;
        _options = _work.LoadedOptions();
        for (var i = 0; i < _options.Count; i++) _products.Children.Add(BuildCard(i));
        _products.Children.Add(_empty);
        UpdateEmpty();
    }

    private bool Matches(LoadedOption o)
    {
        var q = (_search.Text ?? "").Trim();
        return q.Length == 0 || o.Text.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplySearch()
    {
        for (var i = 0; i < _options.Count; i++)
            ((View)_products.Children[i]).IsVisible = Matches(_options[i]) && (LineOf(_options[i]) is not null || RemainingPieces(_options[i]) >= _options[i].Level.Units);
        UpdateEmpty();
    }

    private void UpdateEmpty()
    {
        var any = Enumerable.Range(0, _options.Count).Any(i => ((View)_products.Children[i]).IsVisible);
        _empty.IsVisible = !any;
        _empty.Text = _options.Count == 0 ? "لا توجد مواد محمّلة في السيارة" : "لا نتائج";
        _empty.TextColor = _options.Count == 0 ? Ui.Red : Ui.Muted;
    }

    private View BuildCard(int index)
    {
        var o = _options[index];
        var line = LineOf(o);
        var leftPieces = RemainingPieces(o);
        var card = Card(o, line, leftPieces);
        card.IsVisible = Matches(o) && (line is not null || leftPieces >= o.Level.Units);   // نفد: لا يظهر
        return card;
    }

    /// <summary>بعد تغيير كمية منتج: تُعاد بطاقات مستوياته (كرتون/شرنك) لأن رصيدها واحد.</summary>
    private void RefreshRelated(LoadedOption changed)
    {
        for (var i = 0; i < _options.Count; i++)
        {
            if (_options[i].Product.ItemId != changed.Product.ItemId || _options[i].RecipeId != changed.RecipeId) continue;
            _products.Children[i] = BuildCard(i);
        }
        UpdateEmpty();
    }

    private Border Card(LoadedOption o, SaleLine? line, decimal leftPieces)
    {
        var price = RepWork.PackPrice(o.Product, o.Level, o.RecipeId);
        var left = Formats.Packs(Math.Max(0, leftPieces), o.Product.Levels);
        var info = new VerticalStackLayout
        {
            Spacing = 2, VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = o.Text, FontSize = 15, FontAttributes = FontAttributes.Bold, TextColor = Ui.Ink, LineBreakMode = LineBreakMode.WordWrap },
                Ui.Text((_kind == "Free" ? "مجاني" : Formats.Money(price)) + $" · متبقي {left}", 12.5, Ui.Muted)
            }
        };
        View control;
        if (line is null)
        {
            var add = new Button { Text = "+", FontSize = 22, FontAttributes = FontAttributes.Bold, WidthRequest = 48, HeightRequest = 48, CornerRadius = 14, BackgroundColor = Ui.Brand, TextColor = Colors.White, Padding = 0 };
            add.Clicked += (_, _) => SetQty(o, 1);
            control = add;
        }
        else
        {
            var minus = new Button { Text = "−", FontSize = 22, WidthRequest = 42, HeightRequest = 42, CornerRadius = 12, BackgroundColor = Ui.Chip, TextColor = Ui.Ink, Padding = 0 };
            var plus = new Button { Text = "+", FontSize = 22, WidthRequest = 42, HeightRequest = 42, CornerRadius = 12, BackgroundColor = Ui.Brand, TextColor = Colors.White, Padding = 0, IsEnabled = leftPieces >= o.Level.Units };
            minus.Clicked += (_, _) => SetQty(o, line.Quantity - 1);
            plus.Clicked += (_, _) => SetQty(o, line.Quantity + 1);
            // الرقم يُكتب مباشرة: لبيع عدد كبير بلا ضغط متكرر
            var qty = new Entry
            {
                Text = Formats.Qty(line.Quantity), Keyboard = Keyboard.Numeric, FontSize = 19, FontAttributes = FontAttributes.Bold, TextColor = Ui.Ink,
                WidthRequest = 64, HorizontalTextAlignment = TextAlignment.Center, VerticalOptions = LayoutOptions.Center
            };
            var done = false;
            void Commit()
            {
                if (done) return;
                done = true;
                if (decimal.TryParse(qty.Text?.Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0 && v != line.Quantity)
                    SetQty(o, v);
                else qty.Text = Formats.Qty(line.Quantity);
            }
            qty.Focused += (_, _) => Dispatcher.Dispatch(() => { qty.CursorPosition = 0; qty.SelectionLength = qty.Text?.Length ?? 0; });
            qty.Completed += (_, _) => Commit();
            qty.Unfocused += (_, _) => Commit();
            control = new HorizontalStackLayout { Spacing = 6, VerticalOptions = LayoutOptions.Center, Children = { minus, qty, plus } };
        }
        var grid = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            Children = { info, Put(control, 1) }
        };
        return Ui.Card(grid, line is null ? null : Ui.Brand);
    }

    /// <summary>يضبط كمية سطر المنتج (٠ يحذفه) بما لا يتجاوز ما في السيارة، ثم يحدّث بطاقات المنتج والإجمالي فقط.</summary>
    private void SetQty(LoadedOption o, decimal qty)
    {
        qty = Math.Floor(qty);
        var i = _lines.FindIndex(l => l.ItemId == o.Product.ItemId && l.LevelId == o.Level.LevelId && l.RecipeId == o.RecipeId);
        var current = i >= 0 ? _lines[i].Quantity : 0;
        var max = current + Math.Floor(RemainingPieces(o) / o.Level.Units);
        qty = Math.Min(qty, max);
        if (qty <= 0) { if (i >= 0) _lines.RemoveAt(i); }
        else if (i >= 0) _lines[i] = _lines[i] with { Quantity = qty };
        else _lines.Add(new SaleLine(o.Product.ItemId, o.Level.LevelId, qty, o.RecipeId));
        RefreshRelated(o);
        Recalc();
    }

    private void Recalc()
    {
        var count = _lines.Sum(l => l.Quantity);
        if (_work.Snapshot is null) return;
        if (_customer.Selected is null)
        {
            _total.Text = Formats.Money(0);
            _summary.Text = count == 0 ? "لم تُضف منتجات" : $"{Formats.Qty(count)} عبوة";
            (_warning.Text, _warning.TextColor) = (_lines.Count > 0 ? "اختر الزبون" : "", Ui.Amber);
            return;
        }
        var check = _work.CheckSale(_kind, _customer.Selected.Id, _lines, _freeReason.Text);
        _total.Text = Formats.Money(check.Total);
        _summary.Text = _lines.Count == 0 ? "لم تُضف منتجات" : string.Join(" + ", check.Lines.Select(l => $"{Formats.Qty(l.Quantity)} {l.Level.Name}"));
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
        var debt = _kind == "CreditSale" ? $"\nالدين بعد الحفظ: {Formats.Money(customer.Balance + check.Total)}" : "";
        var message = $"{label} — {customer.Name}\n{details}\nالإجمالي: {Formats.Money(check.Total)}{debt}" + (check.Warning is null ? "" : $"\n\n⚠ {check.Warning}");
        if (!await DisplayAlert("تأكيد البيع", message, "حفظ", "رجوع")) return;
        var saved = _work.SaveSale(_kind, customer.Id, lines, _freeReason.Text);
        AppServices.Instance.SyncSoon();
        await ReceiptActions.OfferAsync(this, saved);           // يُرسل للمعمل تلقائيًا، والوصل يُطبع الآن أو لاحقًا من «حركاتي»
        await Navigation.PopAsync();
    }
}
