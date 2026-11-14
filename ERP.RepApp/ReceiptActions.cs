using ERP.RepApp.Core;

namespace ERP.RepApp;

/// <summary>
/// طباعة الوصل ومشاركته من الشاشات: الطابعة المختارة تُحفظ في الهاتف (عنوانها وعرض الورق 58 أو 80 ملم)،
/// وأول طباعة تطلب اختيارها من الأجهزة المقترنة.
/// </summary>
public static class ReceiptActions
{
    private const string PrinterKey = "PrinterAddress", PrinterNameKey = "PrinterName", PaperKey = "PaperMm";
    private const string ShareInstead = "مشاركة صورة الوصل بدل الطباعة";

    private static LocalStore Store => AppServices.Instance.Store;
    private static int Paper => int.TryParse(Store.Get(PaperKey), out var mm) ? mm : 58;

    public static string PrinterText => Store.Get(PrinterNameKey) is string n ? $"{n} — ورق {Paper} ملم" : "لم تُختر طابعة";

    /// <summary>بعد حفظ البيع: طباعة أو مشاركة أو لاحقًا.</summary>
    public static async Task OfferAsync(Page page, OutboxItem item)
    {
        if (AppServices.Instance.Work.ReceiptFor(item) is null) return;
        var choice = await page.DisplayActionSheet("حُفظ البيع — الوصل", "لاحقًا (من «حركاتي»)", null, "طباعة الوصل", "مشاركة صورة الوصل");
        if (choice == "طباعة الوصل") await PrintAsync(page, item);
        else if (choice == "مشاركة صورة الوصل") await ShareAsync(page, item);
    }

    public static async Task PrintAsync(Page page, OutboxItem item)
    {
        if (AppServices.Instance.Work.ReceiptFor(item) is not { } receipt) return;
        if (Store.Get(PrinterKey) is null && !await ChoosePrinterAsync(page, allowShare: true, item)) return;
        if (Store.Get(PrinterKey) is not string address) return;
        try
        {
            using var bmp = ReceiptPrinter.Render(receipt, EscPos.DotsFor(Paper));
            await ReceiptPrinter.SendAsync(address, ReceiptPrinter.ToEscPos(bmp));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Java.Lang.SecurityException)
        {
            var next = await page.DisplayActionSheet($"لم يُطبع الوصل: {ex.Message}", "إغلاق", null, "إعادة المحاولة", "اختيار طابعة أخرى", "مشاركة صورة الوصل");
            if (next == "إعادة المحاولة") await PrintAsync(page, item);
            else if (next == "اختيار طابعة أخرى" && await ChoosePrinterAsync(page, allowShare: false, item)) await PrintAsync(page, item);
            else if (next == "مشاركة صورة الوصل") await ShareAsync(page, item);
        }
    }

    /// <summary>وصل تجريبي صغير للتأكد من الطابعة وعرض الورق قبل أول بيع.</summary>
    public static async Task PrintSampleAsync(Page page)
    {
        if (Store.Get(PrinterKey) is null && !await ChoosePrinterAsync(page)) return;
        if (Store.Get(PrinterKey) is not string address) return;
        var snap = AppServices.Instance.Work.Snapshot;
        var receipt = new Receipt(AppServices.Instance.Work.CompanyName, snap?.CompanyLogo, "تجريبي", DateTime.Now, "تجريبي", "زبون تجريبي", snap?.RepName ?? "",
                                  new() { new ReceiptLine("كارتون 40×330 الرحمة", "كارتون", 2, 4_250, 8_500), new ReceiptLine("شرنك 20×330 الرحمة", "شرنك", 3, 2_250, 6_750) }, 15_250);
        try
        {
            using var bmp = ReceiptPrinter.Render(receipt, EscPos.DotsFor(Paper));
            await ReceiptPrinter.SendAsync(address, ReceiptPrinter.ToEscPos(bmp));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Java.Lang.SecurityException)
        {
            await page.DisplayAlert("لم يُطبع الوصل التجريبي", ex.Message, "حسنًا");
        }
    }

    public static async Task ShareAsync(Page page, OutboxItem item)
    {
        if (AppServices.Instance.Work.ReceiptFor(item) is not { } receipt) return;
        using var bmp = ReceiptPrinter.Render(receipt, EscPos.DotsFor(Paper));
        var path = await ReceiptPrinter.SavePngAsync(bmp, $"وصل-{receipt.Number}");
        await Share.Default.RequestAsync(new ShareFileRequest($"وصل {receipt.Number}", new ShareFile(path, "image/png")));
    }

    /// <summary>اختيار الطابعة من الأجهزة المقترنة ثم عرض الورق.</summary>
    /// <returns>true إن اختيرت طابعة.</returns>
    public static async Task<bool> ChoosePrinterAsync(Page page, bool allowShare = false, OutboxItem? item = null)
    {
        if (await Permissions.RequestAsync<BluetoothConnectPermission>() != PermissionStatus.Granted)
        {
            await page.DisplayAlert("الطابعة", "اسمح للتطبيق بالاتصال بالأجهزة القريبة (البلوتوث) للطباعة، أو شارك صورة الوصل", "حسنًا");
            return false;
        }
        List<(string Name, string Address)> devices;
        try { devices = ReceiptPrinter.Paired(); }
        catch (InvalidOperationException ex) { await page.DisplayAlert("الطابعة", ex.Message, "حسنًا"); return false; }
        if (devices.Count == 0)
        {
            await page.DisplayAlert("لا طابعة مقترنة",
                "في جهاز نقاط البيع تظهر الطابعة المدمجة عادة باسم مثل InnerPrinter عند تشغيل البلوتوث.\n" +
                "مع هاتف عادي: شغّل طابعة البلوتوث واقرنها من إعدادات الهاتف ← البلوتوث، ثم عد هنا.\n" +
                "أو شارك صورة الوصل.", "حسنًا");
            if (allowShare && item is not null) await ShareAsync(page, item);
            return false;
        }
        var names = devices.Select(d => $"{d.Name} ({d.Address})").ToList();
        var buttons = allowShare ? names.Append(ShareInstead).ToArray() : names.ToArray();
        var picked = await page.DisplayActionSheet("اختر الطابعة", "إلغاء", null, buttons);
        if (picked == ShareInstead && item is not null) { await ShareAsync(page, item); return false; }
        var index = names.IndexOf(picked ?? "");
        if (index < 0) return false;
        var paper = await page.DisplayActionSheet("عرض ورق الطابعة", null, null, "58 ملم (أغلب أجهزة نقاط البيع والمحمولة)", "80 ملم");
        Store.Set(PaperKey, paper?.StartsWith("80") == true ? "80" : "58");
        Store.Set(PrinterKey, devices[index].Address);
        Store.Set(PrinterNameKey, devices[index].Name);
        return true;
    }
}
