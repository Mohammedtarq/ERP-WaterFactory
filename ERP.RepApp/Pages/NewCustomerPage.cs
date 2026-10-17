using ERP.Cloud.Contracts;
using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>
/// زبون جديد بحقول شاشة «العملاء» في المعمل: الاسم والنوع (مباشر / وكيل / فرعي لوكيل) والهاتف والعنوان والمحافظة وحد الدين.
/// يُسند للمندوب ويظهر فورًا في الهاتف فيُباع له حتى بلا إنترنت، ثم يُسجَّل في المعمل عند المزامنة.
/// </summary>
public class NewCustomerPage : ContentPage
{
    private static readonly (string Text, string Key)[] Types = { ("مباشر", "Direct"), ("وكيل", "Agent"), ("عميل فرعي لوكيل", "SubCustomer") };
    private readonly RepWork _work = AppServices.Instance.Work;
    private readonly Entry _name = Field("اسم الزبون أو المحل");
    private readonly Entry _phone = Field("رقم الهاتف", Keyboard.Telephone);
    private readonly Entry _address = Field("العنوان (المنطقة، أقرب نقطة دالة)");
    private readonly Entry _province = Field("المحافظة");
    private readonly Entry _limit = Field("حد الدين بالدينار (اختياري)", Keyboard.Numeric);
    private readonly Picker _type = new() { Title = "نوع الزبون", FontSize = 17, ItemsSource = Types.Select(t => t.Text).ToList(), SelectedIndex = 0 };
    private readonly Picker _agent = new() { Title = "الوكيل", FontSize = 17, IsVisible = false };
    private readonly Label _status = Ui.Text("", 15, Ui.Red);
    private readonly List<LiveCustomer> _agents;
    private readonly TaskCompletionSource<LiveCustomer?> _done = new();

    /// <summary>الزبون المضاف (أو null إن رجع المندوب دون حفظ).</summary>
    public Task<LiveCustomer?> Result => _done.Task;

    public NewCustomerPage()
    {
        _province.Text = "البصرة";
        _agents = _work.Agents();
        _agent.ItemsSource = _agents.Select(a => a.Name).ToList();
        _type.SelectedIndexChanged += (_, _) => _agent.IsVisible = Types[Math.Max(0, _type.SelectedIndex)].Key == "SubCustomer";
        Ui.Setup(this, "زبون جديد", new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Ui.Card(new VerticalStackLayout { Spacing = 6, Children = { _name, _type, _agent, _phone, _address, _province, _limit } }, Ui.Primary),
                Ui.Text("يُسند الزبون إليك ويظهر فورًا في البيع والتحصيل، ويُسجَّل في المعمل عند المزامنة (الإدارة تراجع النوع وحد الدين).", 14, Ui.Muted),
                Ui.Big("حفظ الزبون", Ui.Primary, SaveAsync),
                _status
            }
        });
    }

    private static Entry Field(string placeholder, Keyboard? keyboard = null) =>
        new() { Placeholder = placeholder, FontSize = 17, Keyboard = keyboard ?? Keyboard.Text };

    private async Task SaveAsync()
    {
        var type = Types[Math.Max(0, _type.SelectedIndex)].Key;
        int? agentId = type == "SubCustomer" && _agent.SelectedIndex >= 0 ? _agents[_agent.SelectedIndex].Id : null;
        decimal? limit = null;
        if (!string.IsNullOrWhiteSpace(_limit.Text))
        {
            limit = Ui.Number(_limit.Text);
            if (limit is null) { _status.Text = "حد الدين رقم بالدينار"; return; }
        }
        var payload = new NewCustomerPayload(_name.Text ?? "", _phone.Text, _address.Text, _province.Text, type, agentId, limit);
        var check = _work.CheckNewCustomer(payload);
        if (!check.Ok) { _status.Text = check.Error; return; }
        if (check.Warning is not null && !await DisplayAlert("تنبيه", $"{check.Warning}. حفظه زبونًا جديدًا؟", "حفظ", "رجوع")) return;
        var customer = _work.SaveNewCustomer(payload);
        AppServices.Instance.SyncSoon();
        _done.TrySetResult(customer);
        await DisplayAlert("تم الحفظ", $"أُضيف «{customer.Name}» — يُرسل للمعمل تلقائيًا", "حسنًا");
        await Navigation.PopAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _done.TrySetResult(null);
    }
}
