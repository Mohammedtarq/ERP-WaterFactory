using ERP.Cloud.Contracts;
using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>
/// «زبائني من النظام»: يبحث المندوب في زبائن المعمل ويؤشّر على زبائنه ثم يرسل طلب ربط للإدارة.
/// لا يتغير شيء في قائمته حتى توافق الإدارة من «طلبات المندوبين»؛ المطلوب سابقًا يظهر «بانتظار الموافقة».
/// </summary>
public class MyCustomersPage : ContentPage
{
    private readonly RepWork _work = AppServices.Instance.Work;
    private readonly SearchBar _search = new() { Placeholder = "ابحث باسم الزبون أو هاتفه", FontSize = 16 };
    private readonly VerticalStackLayout _list = new() { Spacing = 4 };
    private readonly Label _count = Ui.Text("", 15, Ui.Muted);
    private readonly HashSet<int> _picked = new();

    public MyCustomersPage()
    {
        _search.TextChanged += (_, _) => Fill();
        Ui.Setup(this, "زبائني من النظام", new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Ui.Text("أشّر على زبائنك ثم أرسل الطلب. تظهر في قائمتك بعد موافقة الإدارة.", 15, Ui.Muted),
                Ui.Card(new VerticalStackLayout { Spacing = 6, Children = { _search, _count, Ui.Small("مزامنة الآن", Ui.Blue, async () => { await AppServices.Instance.SyncNowAsync(); Fill(); }), _list } }),
                Ui.Big("إرسال للإدارة للموافقة", Ui.Primary, SendAsync)
            }
        });
        Fill();
    }

    private void Fill()
    {
        _list.Children.Clear();
        var all = _work.OtherCustomers();
        var q = (_search.Text ?? "").Trim();
        if (!_work.OtherCustomersSupported)
        {
            // نسخة العمل وصلت بلا قائمة الزبائن: برنامج المعمل (أو وكيل المزامنة) قديم، فيجب تحديثه ثم المزامنة
            _count.Text = _work.Snapshot is null ? "لم تصل نسخة العمل من المعمل بعد — اضغط «مزامنة الآن»"
                : "قائمة زبائن النظام لم تصل من المعمل: برنامج المعمل (ووكيل المزامنة) يحتاج التحديث للنسخة الجديدة، ثم اضغط «مزامنة الآن».";
            _count.TextColor = Ui.Red;
            return;
        }
        _count.TextColor = Ui.Muted;
        var rows = all.Where(x => q.Length == 0 || x.Customer.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || (x.Customer.Phone ?? "").Contains(q))
                      .OrderBy(x => x.Pending).ThenBy(x => x.Customer.Name).Take(60).ToList();
        _count.Text = all.Count == 0 ? "لا يوجد زبائن آخرون في النظام غير المربوطين بك — زامن لتحديث القائمة"
            : rows.Count == 0 ? $"لا نتائج لـ «{q}» بين {all.Count} زبون — جرّب جزءًا آخر من الاسم"
            : $"المختار: {_picked.Count}   ·   المعروض {rows.Count} من {all.Count}";
        foreach (var (c, pending) in rows)
        {
            var id = c.Id;
            var box = new CheckBox { IsChecked = _picked.Contains(id), IsEnabled = !pending, Color = Ui.Primary };
            box.CheckedChanged += (_, e) => { if (e.Value) _picked.Add(id); else _picked.Remove(id); _count.Text = $"المختار: {_picked.Count}"; };
            var name = Ui.Text(c.Name + (string.IsNullOrWhiteSpace(c.Phone) ? "" : $"  ·  {c.Phone}"), 16, pending ? Ui.Muted : Ui.Ink);
            name.VerticalTextAlignment = TextAlignment.Center;
            var row = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
                Children = { box, WithColumn(name, 1), WithColumn(Ui.Text(pending ? "بانتظار الموافقة" : "", 13, Ui.Amber), 2) }
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => { if (!pending) box.IsChecked = !box.IsChecked; };
            row.GestureRecognizers.Add(tap);
            _list.Children.Add(Ui.Card(row));
        }
    }

    private static View WithColumn(View v, int column) { Grid.SetColumn(v, column); return v; }

    private async Task SendAsync()
    {
        if (_picked.Count == 0) { await DisplayAlert("زبائني", "أشّر على زبون واحد على الأقل", "حسنًا"); return; }
        if (!await DisplayAlert("إرسال للإدارة", $"طلب ربط {_picked.Count} زبون بك. لا تتغير قائمتك حتى توافق الإدارة.", "إرسال", "رجوع")) return;
        try
        {
            _work.SaveCustomerLink(_picked.ToList());
        }
        catch (InvalidOperationException ex)
        {
            await DisplayAlert("زبائني", ex.Message, "حسنًا");
            return;
        }
        AppServices.Instance.SyncSoon();
        await DisplayAlert("زبائني", "أُرسل الطلب — تجد حالته في «حركاتي»", "حسنًا");
        await Navigation.PopAsync();
    }
}
