using ERP.RepApp.Pages;

namespace ERP.RepApp;

/// <summary>يبدأ بشاشة الربط إن لم يُربط الهاتف بعد، وإلا بالرئيسية.</summary>
public class App : Application
{
    public App()
    {
        UserAppTheme = AppTheme.Light;
        // المظهر المحفوظ (فاتح، أو «شمس») قبل بناء أي صفحة
        Ui.ApplyTheme(AppServices.Instance.Store.Get(Ui.ThemeKey) == "sun");
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(Root(Start()));

    /// <summary>غير مربوط ← الربط؛ ومربوط وللمندوب دخول في المعمل ← شاشة الدخول؛ وإلا الرئيسية.</summary>
    private static Page Start()
    {
        var store = AppServices.Instance.Store;
        if (store.Link is null) return new LinkPage();
        return store.Snapshot?.Login is { } login ? new LoginPage(login) : new HomePage();
    }

    public static NavigationPage Root(Page page) =>
        new(page) { BarBackgroundColor = Ui.Deep, BarTextColor = Colors.White, FlowDirection = FlowDirection.RightToLeft };

    /// <summary>استبدال الشاشة الجذرية (بعد الربط أو فكّه).</summary>
    public static void ShowRoot(Page page)
    {
        if (Current?.Windows.FirstOrDefault() is { } window) window.Page = Root(page);
    }
}
