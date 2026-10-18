using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>
/// اختيار الزبون كقائمة منسدلة بالبحث: بلا كتابة تظهر آخر الزبائن تعاملًا (الأحدث) فقط، وبالكتابة يُبحث بالاسم أو الهاتف
/// في كل زبائن المندوب — بدل قائمة طويلة بكل الأسماء.
/// </summary>
public class CustomerPicker : ContentView
{
    private readonly RepWork _work;
    private readonly SearchBar _search = new() { Placeholder = "ابحث باسم الزبون أو هاتفه", FontSize = 16 };
    private readonly VerticalStackLayout _list = new() { Spacing = 4 };
    private readonly Label _selected = Ui.Text("لم يُختر زبون", 17, Ui.Muted);
    private readonly Label _hint = Ui.Text("", 13, Ui.Muted);
    public LiveCustomer? Selected { get; private set; }
    public event Action? SelectionChanged;

    public CustomerPicker(RepWork work)
    {
        _work = work;
        _search.TextChanged += (_, _) => Fill();
        var add = Ui.Small("+ زبون جديد", Ui.Blue, async () =>
        {
            if (Application.Current?.Windows.FirstOrDefault()?.Page is not NavigationPage nav) return;
            var page = new NewCustomerPage();
            await nav.PushAsync(page);
            if (await page.Result is { } c) Select(c.Id);
        });
        Content = new VerticalStackLayout { Spacing = 6, Children = { _selected, _search, _hint, _list, add } };
        Fill();
    }

    public void Select(int customerId)
    {
        Selected = _work.Customer(customerId);
        _selected.Text = Selected is null ? "لم يُختر زبون" : $"الزبون: {Selected.Name} — الرصيد {Formats.Money(Selected.Balance)}";
        _selected.TextColor = Selected is null ? Ui.Muted : (Selected.OverLimit ? Ui.Red : Ui.Primary);
        _search.Text = "";
        _list.Children.Clear();
        SelectionChanged?.Invoke();
    }

    private void Fill()
    {
        _list.Children.Clear();
        var q = (_search.Text ?? "").Trim();
        _hint.Text = "";
        if (q.Length == 0 && Selected is not null) return;
        var all = _work.Customers();
        IEnumerable<LiveCustomer> rows;
        if (q.Length == 0)
        {
            // بلا بحث: الأحدث تعاملًا (والزبائن الجدد)، وإلا أول الأسماء
            var recent = _work.RecentCustomerIds();
            var top = recent.Select(id => all.FirstOrDefault(c => c.Id == id)).OfType<LiveCustomer>()
                            .Concat(all.Where(c => c.IsNew)).DistinctBy(c => c.Id).Take(8).ToList();
            rows = top.Count > 0 ? top : all.OrderBy(c => c.Name).Take(8);
            _hint.Text = $"{(top.Count > 0 ? "الأحدث" : "أول الأسماء")} — اكتب للبحث في {all.Count} زبون";
        }
        else
            rows = all.Where(c => c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || (c.Customer.Phone ?? "").Contains(q))
                      .OrderBy(c => c.Name).Take(30);
        foreach (var c in rows)
        {
            var id = c.Id;
            var b = new Button
            {
                Text = $"{c.Name}{(c.IsNew ? " (جديد)" : "")}   ({Formats.Money(c.Balance)})", FontSize = 16, HeightRequest = 50, CornerRadius = 10,
                BackgroundColor = Colors.White, TextColor = c.OverLimit ? Ui.Red : Ui.Ink, BorderColor = Color.FromArgb("#CBD5E1"), BorderWidth = 1
            };
            b.Clicked += (_, _) => Select(id);
            _list.Children.Add(b);
        }
    }
}
