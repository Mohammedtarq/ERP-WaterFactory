using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ERP.Presentation.Services;

namespace ERP.Desktop.Services;

/// <summary>رسائل بقراءة من اليمين لليسار ومحاذاة يمنى.</summary>
public class WpfDialogService : IDialogService
{
    private const MessageBoxOptions Rtl = MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign;
    private const string Caption = "نظام إدارة الأعمال المتكامل";

    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                                    ?? Application.Current?.MainWindow;

    public void Info(string message) => Show(message, MessageBoxButton.OK, MessageBoxImage.Information);
    public void Error(string message) => Show(message, MessageBoxButton.OK, MessageBoxImage.Warning);
    public bool Confirm(string message) => Show(message, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void ShowReport(ReportDocument report)
    {
        var w = new Views.Shell.ReportPreviewWindow(report);
        if (Owner is { } owner) w.Owner = owner;
        w.ShowDialog();
    }

    public string? PickImageFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "اختر شعار الشركة",
            Filter = "صور (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg"
        };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public byte[]? PickPhoto()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "اختر الصورة الشخصية",
            Filter = "صور (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg"
        };
        if (dlg.ShowDialog(Owner) != true) return null;
        var bytes = File.ReadAllBytes(dlg.FileName);
        return PhotoImaging.SquareJpeg(bytes, 256) ?? bytes;
    }

    public string? PickFile(string title, string filter)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public void OpenUrl(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

    private static MessageBoxResult Show(string message, MessageBoxButton buttons, MessageBoxImage icon) =>
        Owner is { } owner
            ? MessageBox.Show(owner, message, Caption, buttons, icon, MessageBoxResult.None, Rtl)
            : MessageBox.Show(message, Caption, buttons, icon, MessageBoxResult.None, Rtl);
}

/// <summary>تجهيز الصورة الشخصية: قصّ مربع من الوسط ثم تصغير إلى size×size وحفظها JPEG (نحو 15–30 كيلوبايت).</summary>
public static class PhotoImaging
{
    public static byte[]? SquareJpeg(byte[] bytes, int size)
    {
        try
        {
            var src = new BitmapImage();
            src.BeginInit();
            src.CacheOption = BitmapCacheOption.OnLoad;
            src.StreamSource = new MemoryStream(bytes);
            src.EndInit();
            src.Freeze();
            int side = Math.Min(src.PixelWidth, src.PixelHeight);
            if (side <= 0) return null;
            var crop = new CroppedBitmap(src, new Int32Rect((src.PixelWidth - side) / 2, (src.PixelHeight - side) / 2, side, side));
            double scale = Math.Min(1.0, (double)size / side);
            BitmapSource result = scale < 1.0 ? new TransformedBitmap(crop, new ScaleTransform(scale, scale)) : crop;
            // الشفافية (PNG) تصير بيضاء في JPEG بدل السواد
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, result.PixelWidth, result.PixelHeight));
                dc.DrawImage(result, new Rect(0, 0, result.PixelWidth, result.PixelHeight));
            }
            var target = new RenderTargetBitmap(result.PixelWidth, result.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(target));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException) { return null; }
    }
}
