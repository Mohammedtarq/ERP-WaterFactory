using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>اختيار الزبون بالبحث بالاسم أو الهاتف، مع رصيده الحالي.</summary>
public class CustomerPicker : ContentView
{
    private readonly RepWork _work;
    private readonly SearchBar _search = new() { Placeholder = "ابحث باسم الزبون أو هاتفه", FontSize = 16 };
    private readonly VerticalStackLayout _list = new() { Spacing = 4 };
    private readonly Label _selected = Ui.Text("لم يُختر زبون", 17, Ui.Muted);
    public LiveCustomer? Selected { get; private set; }
    public event Action? SelectionChanged;

    public CustomerPicker(RepWork work)
    {
        _work = work;
        _search.TextChanged += (_, _) => Fill();
        Content = new VerticalStackLayout { Spacing = 6, Children = { _selected, _search, _list } };
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
        if (q.Length == 0 && Selected is not null) return;
        var rows = _work.Customers()
            .Where(c => q.Length == 0 || c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || (c.Customer.Phone ?? "").Contains(q))
            .OrderBy(c => c.Name).Take(30);
        foreach (var c in rows)
        {
            var id = c.Id;
            var b = new Button
            {
                Text = $"{c.Name}   ({Formats.Money(c.Balance)})", FontSize = 16, HeightRequest = 50, CornerRadius = 10,
                BackgroundColor = Colors.White, TextColor = c.OverLimit ? Ui.Red : Ui.Ink, BorderColor = Color.FromArgb("#CBD5E1"), BorderWidth = 1
            };
            b.Clicked += (_, _) => Select(id);
            _list.Children.Add(b);
        }
    }
}
