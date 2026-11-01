namespace ERP.RepApp.Pages;

/// <summary>
/// حركة الفتح قبل الصفحة التالية (الربط أو الرئيسية) حين لا توجد شاشة دخول: القطرة والشعار ثم تتلاشى إلى الصفحة. لمسة تتخطاها.
/// </summary>
public class IntroPage : ContentPage
{
    private readonly Page _next;
    private readonly RahmaIntro _stage = new();
    private bool _left;

    public IntroPage(Page next)
    {
        _next = next;
        NavigationPage.SetHasNavigationBar(this, false);
        BackgroundColor = Color.FromArgb("#071F42");
        Content = _stage;
        _stage.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => Leave(fade: false)) });
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _stage.PlayWhenReady(RahmaIntro.IntroOnly, () => Leave(fade: true));
    }

    private async void Leave(bool fade)
    {
        if (_left) return;
        _left = true;
        if (fade) await _stage.FadeTo(0, 260);
        App.ShowRoot(_next);
    }
}
