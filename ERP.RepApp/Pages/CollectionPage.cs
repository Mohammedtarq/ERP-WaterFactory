using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>تحصيل دين أو جزء منه: الزبون ← رصيده ← المبلغ ← حفظ.</summary>
public class CollectionPage : ContentPage
{
    private readonly RepWork _work = AppServices.Instance.Work;
    private readonly CustomerPicker _customer;
    private readonly Entry _amount = new() { Placeholder = "المبلغ بالدينار", Keyboard = Keyboard.Numeric, FontSize = 22 };
    private readonly Label _note = Ui.Text("", 15, Ui.Amber);

    public CollectionPage()
    {
        _customer = new CustomerPicker(_work);
        _customer.SelectionChanged += Check;
        _amount.TextChanged += (_, _) => Check();
        Ui.Setup(this, "تحصيل دين", new VerticalStackLayout
        {
            Spacing = 8,
            Children = { Ui.Card(_customer), Ui.Text("المبلغ المستلم", 18), _amount, _note, Ui.Big("حفظ التحصيل", Ui.Blue, SaveAsync) }
        });
    }

    private void Check()
    {
        if (_customer.Selected is null || Ui.Number(_amount.Text) is not decimal amount) { _note.Text = ""; return; }
        var check = _work.CheckCollection(_customer.Selected.Id, amount);
        _note.Text = check.Error ?? check.Warning ?? $"الرصيد بعد التحصيل: {Formats.Money(_customer.Selected.Balance - amount)}";
        _note.TextColor = check.Error is not null ? Ui.Red : check.Warning is not null ? Ui.Amber : Ui.Green;
    }

    private async Task SaveAsync()
    {
        if (_customer.Selected is not { } customer) { await DisplayAlert("تحصيل", "اختر الزبون", "حسنًا"); return; }
        var amount = Ui.Number(_amount.Text) ?? 0;
        var check = _work.CheckCollection(customer.Id, amount);
        if (!check.Ok) { await DisplayAlert("لا يمكن الحفظ", check.Error, "حسنًا"); return; }
        var message = $"تحصيل {Formats.Money(amount)} من {customer.Name}" + (check.Warning is null ? "" : $"\n\n⚠ {check.Warning}");
        if (!await DisplayAlert("تأكيد التحصيل", message, "حفظ", "رجوع")) return;
        _work.SaveCollection(customer.Id, amount);
        AppServices.Instance.SyncSoon();
        await DisplayAlert("تم الحفظ", "حُفظ التحصيل في الهاتف ويُرسل للمعمل تلقائيًا", "حسنًا");
        await Navigation.PopAsync();
    }
}
