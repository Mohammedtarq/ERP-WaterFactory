using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>صرف جديد من صندوق المدير المالي: الصندوق ثابت (صندوقه) ← المبلغ ← الباب ← بيان ورقم وصل ← حفظ. يُرحَّل فورًا ويخصم من الصندوق.</summary>
public class NewBoxExpensePage : ContentPage
{
    private readonly RepWork _work = AppServices.Instance.Work;
    private readonly Entry _amount = new() { Placeholder = "المبلغ بالدينار", Keyboard = Keyboard.Numeric, FontSize = 24 };
    private readonly FlexLayout _categories = new() { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, Direction = Microsoft.Maui.Layouts.FlexDirection.Row };
    private readonly Entry _description = new() { Placeholder = "المستفيد / البيان (اختياري)", FontSize = 17 };
    private readonly Entry _receipt = new() { Placeholder = "رقم الوصل (اختياري)", FontSize = 17 };
    private readonly Label _note = Ui.Text("", 15, Ui.Muted);
    private int? _categoryId;
    private readonly Dictionary<int, Button> _buttons = new();

    public NewBoxExpensePage()
    {
        var box = _work.OwnBox();
        _amount.TextChanged += (_, _) => Check();
        foreach (var c in _work.FinanceCategories())
        {
            var b = new Button { Text = c.Name, FontSize = 15, FontAttributes = FontAttributes.Bold, CornerRadius = 14, HeightRequest = 44, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(14, 0) };
            var id = c.Id;
            b.Clicked += (_, _) => { _categoryId = id; Style(); Check(); };
            _buttons[id] = b;
            _categories.Children.Add(b);
        }
        Style();
        Ui.Setup(this, "صرف من الصندوق", new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Ui.Text("يُخصم من صندوقك ويُسجَّل في المصروفات بقيده تلقائيًا", 14, Ui.Muted),
                Ui.Card(new VerticalStackLayout
                {
                    Spacing = 2,
                    Children =
                    {
                        Ui.Text("الصندوق", 12.5, Ui.Muted),
                        new Label { Text = box?.Name ?? "لم يُضبط صندوقك في المعمل", FontSize = 18, FontAttributes = FontAttributes.Bold, TextColor = box is null ? Ui.Red : Ui.Ink },
                        Ui.Text(box is null ? "" : $"الرصيد الآن: {Formats.Money(box.Balance)}", 14, Ui.Muted)
                    }
                }),
                Ui.Text("المبلغ", 18), _amount,
                Ui.Text("الباب", 18), _categories,
                _description, _receipt, _note,
                Ui.Big("حفظ الصرف", Ui.Green, SaveAsync)
            }
        });
    }

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
        if (Ui.Number(_amount.Text) is not decimal amount || _work.OwnBox() is not { } box) { _note.Text = ""; return; }
        var check = _work.CheckBoxExpense(_categoryId ?? -1, amount);
        _note.Text = check.Error ?? check.Warning ?? $"رصيد الصندوق بعد الصرف: {Formats.Money(box.Balance - amount)}";
        _note.TextColor = check.Error is not null ? Ui.Red : check.Warning is not null ? Ui.Amber : Ui.Green;
    }

    private async Task SaveAsync()
    {
        var amount = Ui.Number(_amount.Text) ?? 0;
        var check = _work.CheckBoxExpense(_categoryId, amount);
        if (!check.Ok) { await DisplayAlert("لا يمكن الحفظ", check.Error, "حسنًا"); return; }
        var category = _work.FinanceCategories().First(c => c.Id == _categoryId).Name;
        var message = $"{category}: {Formats.Money(amount)}\nمن صندوق {_work.OwnBox()!.Name}" + (check.Warning is null ? "" : $"\n\n⚠ {check.Warning}");
        if (!await DisplayAlert("تأكيد الصرف", message, "حفظ", "رجوع")) return;
        _work.SaveBoxExpense(_categoryId!.Value, amount, _description.Text, _receipt.Text);
        AppServices.Instance.SyncSoon();
        await DisplayAlert("تم الحفظ", "حُفظ الصرف في الهاتف ويُرسل للمعمل فيُرحَّل إلى بابه ويخصم من الصندوق", "حسنًا");
        await Navigation.PopAsync();
    }
}
