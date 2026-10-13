using ERP.Cloud.Contracts;
using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>
/// تغيير عنوان الخادم (تغيّرت شبكة المعمل أو انتقل الخادم) دون فك الربط: يبقى مفتاح الجهاز وكل الحركات،
/// وما لم يُرسل يذهب إلى العنوان الجديد. يُختبر العنوان بمفتاح الجهاز قبل حفظه.
/// </summary>
public class ServerPage : ContentPage
{
    private readonly Entry _url = new() { FontSize = 17, Keyboard = Keyboard.Url, FlowDirection = FlowDirection.LeftToRight, Placeholder = "http://192.168.1.10:5080" };
    private readonly Label _status = Ui.Text("", 16, Ui.Muted);

    public ServerPage()
    {
        var store = AppServices.Instance.Store;
        _url.Text = store.Link?.ServerUrl;
        Ui.Setup(this, "عنوان الخادم", new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Ui.Text("اكتب العنوان الجديد كما يظهر في «الإعدادات ← المزامنة السحابية» في المعمل. الربط والحركات المحفوظة تبقى كما هي.", 16),
                Ui.Card(_url, Ui.Primary),
                Ui.Big("اختبار وحفظ", Ui.Primary, SaveAsync),
                _status
            }
        });
    }

    private async Task SaveAsync()
    {
        var store = AppServices.Instance.Store;
        if (store.Link is not { } link) { _status.Text = "الهاتف غير مربوط"; return; }
        var url = (_url.Text ?? "").Trim().TrimEnd('/');
        if (CloudUrl.Validate(url) is string bad) { Show(bad, Ui.Red); return; }
        Show("جارٍ الاتصال بالعنوان الجديد…", Ui.Muted);
        string? problem = null;
        try { await new RepCloudClient(link with { ServerUrl = url }).PingAsync(); }
        catch (DeviceRejectedException ex) { problem = ex.Message; }
        catch (OfflineException ex) { problem = $"لا يرد الخادم على هذا العنوان: {ex.Message}"; }
        if (problem is not null
            && !await DisplayAlert("تعذّر الاختبار", $"{problem}\n\nحفظ العنوان على أي حال؟ (تُرسل الحركات حين يعمل)", "حفظ", "رجوع"))
        {
            Show(problem, Ui.Red);
            return;
        }
        if (store.ChangeServer(url) is string error) { Show(error, Ui.Red); return; }
        AppServices.Instance.ResetClient();
        AppServices.Instance.SyncSoon();
        await DisplayAlert("تم", "حُفظ العنوان الجديد — الحركات المحفوظة تُرسل إليه تلقائيًا", "حسنًا");
        await Navigation.PopAsync();
    }

    private void Show(string text, Color color) => (_status.Text, _status.TextColor) = (text, color);
}
