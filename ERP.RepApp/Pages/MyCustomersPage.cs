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
    private const int PageSize = 60;
    private int _shown = PageSize;
    private Button _more = null!;

    public MyCustomersPage()
    {
        _search.TextChanged += (_, _) => { _shown = PageSize; Fill(); };     // بحث جديد يبدأ من الصفحة الأولى
        // القائمة الطويلة تُعرض على دفعات حتى يبقى الهاتف سريعًا؛ «عرض المزيد» يضيف الدفعة التالية
        _more = Ui.Small("عرض المزيد", Ui.Blue, () => { _shown += PageSize; Fill(); });
        Ui.Setup(this, "زبائني من النظام", new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Ui.Text("أشّر على زبائنك ثم أرسل الطلب. تظهر في قائمتك بعد موافقة الإدارة.", 15, Ui.Muted),
                Ui.Card(new VerticalStackLayout { Spacing = 6, Children = { _search, _count, Ui.Small("مزامنة الآن", Ui.Blue, async () => { await AppServices.Instance.SyncNowAsync(); Fill(); }), _list, _more } }),
                Ui.Big("إرسال للإدارة للموافقة", Ui.Primary, SendAsync)
            }
        });
        Fill();
    }

    private void Fill()
    {
        _list.Children.Clear();
        _more.IsVisible = false;
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
        var matches = all.Where(x => q.Length == 0 || x.Customer.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || (x.Customer.Phone ?? "").Contains(q))
                         .OrderBy(x => x.Pending).ThenBy(x => x.Customer.Name).ToList();
        var rows = matches.Take(_shown).ToList();
        var left = matches.Count - rows.Count;
        _more.Text = $"عرض المزيد ({Math.Min(PageSize, left)} أخرى — الباقي {left})";
        _more.IsVisible = left > 0;
        string Counter() => $"المختار: {_picked.Count}   ·   المعروض {rows.Count} من {matches.Count}" + (q.Length > 0 ? $" (نتائج «{q}»)" : "");
        _count.Text = all.Count == 0 ? "لا يوجد زبائن آخرون في النظام غير المربوطين بك — زامن لتحديث القائمة"
            : rows.Count == 0 ? $"لا نتائج لـ «{q}» بين {all.Count} زبون — جرّب جزءًا آخر من الاسم"
            : Counter();
        foreach (var (c, pending) in rows)
        {
            var id = c.Id;
            var box = new CheckBox { IsChecked = _picked.Contains(id), IsEnabled = !pending, Color = Ui.Primary };
            box.CheckedChanged += (_, e) => { if (e.Value) _picked.Add(id); else _picked.Remove(id); _count.Text = Counter(); };
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
