using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>مصروف السيارة (وقود، صيانة…): النوع ← المبلغ ← ملاحظة وصورة وصل اختيارية ← حفظ. يدخل مصروفات المعمل تحت «سيارة المندوب» بعد الاعتماد.</summary>
public class ExpensePage : ContentPage
{
    private readonly RepWork _work = AppServices.Instance.Work;
    private readonly Picker _kind = new() { Title = "نوع المصروف", FontSize = 20, ItemsSource = RepWork.ExpenseKinds };
    private readonly Entry _amount = new() { Placeholder = "المبلغ بالدينار", Keyboard = Keyboard.Numeric, FontSize = 22 };
    private readonly Entry _notes = new() { Placeholder = "ملاحظة (اختياري)", FontSize = 18 };
    private readonly Label _note = Ui.Text("", 15, Ui.Amber);
    private readonly Label _photoText = Ui.Text("لا صورة للوصل", 15, Ui.Muted);
    private byte[]? _photo;

    public ExpensePage()
    {
        _kind.SelectedIndexChanged += (_, _) => Check();
        _amount.TextChanged += (_, _) => Check();
        Ui.Setup(this, "مصروف السيارة", new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Ui.Text("يُخصم من النقد الذي معك ويُسجَّل في المعمل باسم سيارتك بعد اعتماده", 14, Ui.Muted),
                Ui.Card(_kind), Ui.Text("المبلغ", 18), _amount, _notes, _note,
                Ui.Card(new VerticalStackLayout { Spacing = 6, Children = { _photoText, Ui.Small("تصوير الوصل", Ui.Blue, async () => await TakePhotoAsync()) } }),
                Ui.Big("حفظ المصروف", Ui.Amber, SaveAsync)
            }
        });
    }

    private string? Kind => _kind.SelectedIndex >= 0 ? RepWork.ExpenseKinds[_kind.SelectedIndex] : null;

    private void Check()
    {
        if (Ui.Number(_amount.Text) is not decimal amount) { _note.Text = ""; return; }
        var check = _work.CheckExpense(Kind ?? "-", amount);
        _note.Text = check.Error ?? check.Warning ?? $"النقد بعد المصروف: {Formats.Money(_work.Wallet() - amount)}";
        _note.TextColor = check.Error is not null ? Ui.Red : check.Warning is not null ? Ui.Amber : Ui.Green;
    }

    private async Task TakePhotoAsync()
    {
        try
        {
            if (!MediaPicker.Default.IsCaptureSupported) { await DisplayAlert("الصورة", "الكاميرا غير متاحة على هذا الجهاز", "حسنًا"); return; }
            var file = await MediaPicker.Default.CapturePhotoAsync();
            if (file is null) return;
            await using var stream = await file.OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            _photo = ms.ToArray();
            _photoText.Text = $"تم تصوير الوصل ({_photo.Length / 1024} ك.ب) ✔";
            _photoText.TextColor = Ui.Green;
        }
        catch (Exception ex) when (ex is PermissionException or FeatureNotSupportedException or IOException)
        {
            await DisplayAlert("الصورة", "تعذّر التصوير. اسمح للتطبيق باستعمال الكاميرا من إعدادات الهاتف", "حسنًا");
        }
    }

    private async Task SaveAsync()
    {
        var amount = Ui.Number(_amount.Text) ?? 0;
        var check = _work.CheckExpense(Kind, amount);
        if (!check.Ok) { await DisplayAlert("لا يمكن الحفظ", check.Error, "حسنًا"); return; }
        var message = $"{Kind}: {Formats.Money(amount)}" + (check.Warning is null ? "" : $"\n\n⚠ {check.Warning}");
        if (!await DisplayAlert("تأكيد المصروف", message, "حفظ", "رجوع")) return;
        _work.SaveExpense(Kind!, amount, _notes.Text, _photo);
        AppServices.Instance.SyncSoon();
        await DisplayAlert("تم الحفظ", "حُفظ المصروف في الهاتف ويُرسل للمعمل بانتظار الاعتماد", "حسنًا");
        await Navigation.PopAsync();
    }
}
