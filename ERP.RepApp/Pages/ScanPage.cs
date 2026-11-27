using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace ERP.RepApp.Pages;

/// <summary>الكاميرا لقراءة رمز الربط؛ تعود بالنص عند أول قراءة.</summary>
public class ScanPage : ContentPage
{
    private readonly TaskCompletionSource<string?> _result = new();
    private readonly CameraBarcodeReaderView _camera;
    public Task<string?> Result => _result.Task;

    public ScanPage()
    {
        Title = "تصوير رمز الربط";
        FlowDirection = FlowDirection.RightToLeft;
        _camera = new CameraBarcodeReaderView
        {
            Options = new BarcodeReaderOptions { Formats = BarcodeFormat.QrCode, AutoRotate = true, Multiple = false },
            IsDetecting = true
        };
        _camera.BarcodesDetected += (_, e) =>
        {
            var value = e.Results.FirstOrDefault()?.Value;
            if (string.IsNullOrEmpty(value) || _result.Task.IsCompleted) return;
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                _camera.IsDetecting = false;
                _result.TrySetResult(value);
                await Navigation.PopAsync();
            });
        };
        Content = new Grid
        {
            Children =
            {
                _camera,
                new Label { Text = "وجّه الكاميرا نحو الرمز المربع على شاشة المعمل", TextColor = Colors.White, BackgroundColor = Color.FromArgb("#99000000"),
                            Padding = 12, FontSize = 16, VerticalOptions = LayoutOptions.End, HorizontalTextAlignment = TextAlignment.Center }
            }
        };
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _camera.IsDetecting = false;
        _result.TrySetResult(null);
    }
}
