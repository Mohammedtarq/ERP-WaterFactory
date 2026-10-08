using ERP.RepApp.Pages;

namespace ERP.RepApp;

/// <summary>يبدأ بشاشة الربط إن لم يُربط الهاتف بعد، وإلا بالرئيسية.</summary>
public class App : Application
{
    public App() => UserAppTheme = AppTheme.Light;

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(Root(AppServices.Instance.Store.Link is null ? new LinkPage() : new HomePage()));

    public static NavigationPage Root(Page page) =>
        new(page) { BarBackgroundColor = Ui.Primary, BarTextColor = Colors.White, FlowDirection = FlowDirection.RightToLeft };

    /// <summary>استبدال الشاشة الجذرية (بعد الربط أو فكّه).</summary>
    public static void ShowRoot(Page page)
    {
        if (Current?.Windows.FirstOrDefault() is { } window) window.Page = Root(page);
    }
}
