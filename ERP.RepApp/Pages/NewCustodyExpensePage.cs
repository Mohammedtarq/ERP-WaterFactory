using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>مصروف جديد من العهدة: العهدة (إن تعدّدت) ← المبلغ ← الباب (أزرار) ← وصف ورقم وصل ← حفظ. يُرحَّل تلقائيًا ويخصم من رصيد العهدة.</summary>
public class NewCustodyExpensePage : ContentPage
{
    private readonly RepWork _work = AppServices.Instance.Work;
    private readonly Picker _custody = new() { Title = "العهدة", FontSize = 18 };
    private readonly Entry _amount = new() { Placeholder = "المبلغ بالدينار", Keyboard = Keyboard.Numeric, FontSize = 24 };
    private readonly FlexLayout _categories = new() { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, Direction = Microsoft.Maui.Layouts.FlexDirection.Row };
    private readonly Entry _description = new() { Placeholder = "وصف المصروف (اختياري)", FontSize = 17 };
    private readonly Entry _receipt = new() { Placeholder = "رقم الوصل (اختياري)", FontSize = 17 };
    private readonly Label _note = Ui.Text("", 15, Ui.Muted);
    private List<RepWork.LiveCustody> _open = new();
    private int? _categoryId;
    private readonly Dictionary<int, Button> _buttons = new();

    public NewCustodyExpensePage()
    {
        _open = _work.OpenCustodies();
        _custody.ItemsSource = _open.Select(c => $"{c.Title} — {Formats.Money(c.Balance)}").ToList();
        if (_open.Count > 0) _custody.SelectedIndex = 0;
        _custody.SelectedIndexChanged += (_, _) => Check();
        _amount.TextChanged += (_, _) => Check();
        foreach (var c in _work.CustodyCategories())
        {
            var b = new Button { Text = c.Name, FontSize = 15, FontAttributes = FontAttributes.Bold, CornerRadius = 14, HeightRequest = 44, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(14, 0) };
            var id = c.Id;
            b.Clicked += (_, _) => { _categoryId = id; Style(); Check(); };
            _buttons[id] = b;
            _categories.Children.Add(b);
        }
        Style();
        Ui.Setup(this, "مصروف جديد", new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Ui.Text("يُخصم من رصيد عهدتك ويُسجَّل في المعمل بابه تلقائيًا", 14, Ui.Muted),
                _open.Count > 1 ? Ui.Card(_custody) : Ui.Text(_open.Count == 0 ? "لا عهدة فعّالة عندك — اطلبها من المالية" : _open[0].Title, 16, _open.Count == 0 ? Ui.Red : Ui.Muted),
                Ui.Text("المبلغ", 18), _amount,
                Ui.Text("الباب", 18), _categories,
                _description, _receipt, _note,
                Ui.Big("حفظ المصروف", Ui.Green, SaveAsync)
            }
        });
    }

    private RepWork.LiveCustody? Selected => _custody.SelectedIndex >= 0 && _custody.SelectedIndex < _open.Count ? _open[_custody.SelectedIndex] : null;

    private void Style()
    {
        foreach (var (id, b) in _buttons)
        {
            var on = id == _categoryId;
            b.BackgroundColor = on ? Ui.Brand : Colors.White;
            b.TextColor = on ? Colors.White : Ui.Ink;
            b.BorderColor = Ui.Line;
            b.BorderWidth = on ? 0 : Ui.BorderW;
        }
    }

    private void Check()
    {
        if (Ui.Number(_amount.Text) is not decimal amount || Selected is null) { _note.Text = ""; return; }
        var check = _work.CheckCustodyExpense(Selected.Id, _categoryId ?? -1, amount);
        _note.Text = check.Error ?? check.Warning ?? $"رصيد العهدة بعد المصروف: {Formats.Money(Selected.Balance - amount)}";
        _note.TextColor = check.Error is not null ? Ui.Red : check.Warning is not null ? Ui.Amber : Ui.Green;
    }

    private async Task SaveAsync()
    {
        var amount = Ui.Number(_amount.Text) ?? 0;
        var check = _work.CheckCustodyExpense(Selected?.Id, _categoryId, amount);
        if (!check.Ok) { await DisplayAlert("لا يمكن الحفظ", check.Error, "حسنًا"); return; }
        var category = _work.CustodyCategories().First(c => c.Id == _categoryId).Name;
        var message = $"{category}: {Formats.Money(amount)}\nمن عهدة {Selected!.Title}" + (check.Warning is null ? "" : $"\n\n⚠ {check.Warning}");
        if (!await DisplayAlert("تأكيد المصروف", message, "حفظ", "رجوع")) return;
        _work.SaveCustodyExpense(Selected.Id, _categoryId!.Value, amount, _description.Text, _receipt.Text);
        AppServices.Instance.SyncSoon();
        await DisplayAlert("تم الحفظ", "حُفظ المصروف في الهاتف ويُرسل للمعمل فيُرحَّل إلى بابه", "حسنًا");
        await Navigation.PopAsync();
    }
}
