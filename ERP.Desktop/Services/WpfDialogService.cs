using System.Windows;
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
