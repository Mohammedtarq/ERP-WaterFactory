using ERP.Cloud.Contracts;

namespace ERP.RepApp.Pages;

/// <summary>ربط الهاتف بالمندوب: تصوير رمز QR من شاشة «أجهزة التطبيق» في المعمل (أو لصق نصه).</summary>
public class LinkPage : ContentPage
{
    private readonly Editor _code = new() { Placeholder = "أو الصق نص رمز الربط هنا", HeightRequest = 110, FontSize = 14, FlowDirection = FlowDirection.LeftToRight };
    private readonly Label _status = Ui.Text("", 16, Ui.Red);

    public LinkPage()
    {
        Ui.Setup(this, "ربط الجهاز", new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Ui.Title("أهلًا بك في تطبيق المندوب"),
                Ui.Text("في المعمل: «المندوبون ← أجهزة التطبيق» ← سجّل هذا الهاتف باسمك، فيظهر رمز ربط مربع. صوّره من هنا:", 17),
                Ui.Big("تصوير رمز الربط", Ui.Primary, ScanAsync),
                _code,
                Ui.Big("ربط", Ui.Blue, () => LinkAsync(_code.Text)),
                _status
            }
        });
    }

    private async Task ScanAsync()
    {
        if (await Permissions.RequestAsync<Permissions.Camera>() != PermissionStatus.Granted)
        {
            _status.Text = "اسمح للتطبيق باستخدام الكاميرا، أو الصق نص الرمز";
            return;
        }
        var scan = new ScanPage();
        await Navigation.PushAsync(scan);
        var text = await scan.Result;
        if (text is not null) await LinkAsync(text);
    }

    private async Task LinkAsync(string? text)
    {
        var (code, error) = LinkCode.Parse(text);
        if (code is null) { _status.Text = error; return; }
        var app = AppServices.Instance;
        app.Store.SaveLink(code);
        app.ResetClient();
        _status.TextColor = Ui.Primary;
        _status.Text = "رُبط الجهاز — جارٍ جلب نسخة العمل…";
        var r = await app.SyncNowAsync();
        if (r.Error is not null)
            await DisplayAlert("تم الربط", $"{r.Error}\nإن كان الجهاز سُجّل للتو فانتظر دقيقة حتى تصل بياناته للخادم، ثم «مزامنة الآن».", "حسنًا");
        App.ShowRoot(new HomePage());
    }
}
