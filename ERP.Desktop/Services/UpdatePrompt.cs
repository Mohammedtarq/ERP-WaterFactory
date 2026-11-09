using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using ERP.Data.Setup;
using ERP.Presentation.Services;

namespace ERP.Desktop.Services;

/// <summary>
/// عند فتح البرنامج: إن نشر السيرفر نسخة أحدث تظهر رسالة التحديث. بعد الموافقة يُنزَّل Apply-Update.ps1 من السيرفر ويُشغَّل ثم يُغلق البرنامج؛
/// السكربت ينزّل الملفات المتغيّرة فقط، يتحقق منها، يستبدلها (مع نسخة احتياطية للتراجع)، ثم يعيد تشغيل البرنامج.
/// تعذّر الاتصال أو أي خطأ هنا لا يمنع العمل أبدًا.
/// </summary>
public static class UpdatePrompt
{
    /// <returns>true إن بدأ التحديث (البرنامج يُغلق نفسه ولا يُكمل الدخول).</returns>
    public static async Task<bool> RunAsync(string? controlConnectionString, IDialogService dialogs)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("ERP_NO_UPDATE") == "1") return false;
            var host = UpdateService.ServerHost(controlConnectionString);
            if (host is null) return false;
            var current = typeof(App).Assembly.GetName().Version ?? new Version(0, 0);
            var baseUri = UpdateService.BaseUri(host);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var offer = await UpdateService.CheckAsync(http, baseUri, current);
            if (offer is null) return false;

            var text = $"يوجد تحديث جديد للبرنامج (النسخة {offer.Version} — نسختك {current}).\nيستغرق التحديث دقيقة تقريبًا ثم يعود البرنامج للعمل وحده.";
            if (offer.Mandatory)
                MessageBox.Show(text + "\n\nهذا التحديث مطلوب لأن قاعدة البيانات تغيّرت.", "تحديث إجباري", MessageBoxButton.OK, MessageBoxImage.Information,
                                MessageBoxResult.OK, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            else if (!dialogs.Confirm(text + "\n\nتحديث الآن؟")) return false;

            var script = Path.Combine(Path.GetTempPath(), "ERP-Apply-Update.ps1");
            await File.WriteAllBytesAsync(script, await http.GetByteArrayAsync(new Uri(baseUri, "Apply-Update.ps1")));
            var installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -BaseUrl \"{baseUri}\" -InstallDir \"{installDir}\" -ProcessId {Environment.ProcessId}")
            { UseShellExecute = false, CreateNoWindow = true });
            Application.Current.Shutdown();
            return true;
        }
        catch (Exception ex)
        {
            dialogs.Error($"تعذّر بدء التحديث، وسيعمل البرنامج بنسخته الحالية.\n{ex.GetBaseException().Message}");
            return false;
        }
    }
}
