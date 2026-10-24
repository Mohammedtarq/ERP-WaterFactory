using ZXing.Net.Maui.Controls;

namespace ERP.RepApp;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>().UseBarcodeReader();
        return builder.Build();
    }
}
