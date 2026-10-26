namespace ERP.RepApp.Pages;

/// <summary>الصفحات الأربع التي يتنقل بينها الشريط السفلي، وتُبدَّل كجذر بلا تكديس.</summary>
public static class MainPages
{
    public static Page Create(string key) => key switch
    {
        "debts" => new DebtsPage(),
        "month" => new MonthPage(),
        "notices" => new NoticesPage(),
        _ => new HomePage()
    };
}
