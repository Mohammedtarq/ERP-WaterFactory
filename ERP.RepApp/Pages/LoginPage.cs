using ERP.Cloud.Contracts;
using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>
/// دخول المندوب: اسم وكلمة مرور قصيران (حتى 4 حروف أو أرقام) يضعهما المعمل. يُتحقق منهما محليًا من نسخة العمل (بلا إنترنت)،
/// مع إيقاف مؤقت بعد المحاولات الخاطئة. لا يظهر إلا إن ضُبط للمندوب دخول في المعمل.
/// </summary>
public class LoginPage : ContentPage
{
    private readonly SnapshotLogin _login;
    private readonly Entry _user = new() { Placeholder = "اسم الدخول", MaxLength = RepLogin.MaxLength, FontSize = 22, Keyboard = Keyboard.Default, FlowDirection = FlowDirection.LeftToRight, HorizontalTextAlignment = TextAlignment.Center };
    private readonly Entry _pass = new() { Placeholder = "كلمة المرور", MaxLength = RepLogin.MaxLength, IsPassword = true, FontSize = 22, FlowDirection = FlowDirection.LeftToRight, HorizontalTextAlignment = TextAlignment.Center };
    private readonly Label _status = Ui.Text("", 16, Ui.Red);

    public LoginPage(SnapshotLogin login)
    {
        _login = login;
        _pass.Completed += async (_, _) => await TryAsync();
        Ui.Setup(this, "تسجيل الدخول", new VerticalStackLayout
        {
            Spacing = 12,
            Padding = new Thickness(0, 30, 0, 0),
            Children =
            {
                Ui.Title("أهلًا بك"),
                Ui.Text(AppServices.Instance.Work.CompanyName, 18, Ui.Muted),
                _user,
                _pass,
                Ui.Big("دخول", Ui.Primary, TryAsync),
                _status
            }
        });
    }

    private Task TryAsync()
    {
        var result = new LoginGuard(AppServices.Instance.Store).TryLogin(_login, _user.Text, _pass.Text);
        if (result.Ok) { App.ShowRoot(new HomePage()); return Task.CompletedTask; }
        _pass.Text = "";
        _status.Text = result.Message;
        return Task.CompletedTask;
    }
}
