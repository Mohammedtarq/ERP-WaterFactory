using Android.Bluetooth;
using Android.Graphics;
using Android.Text;
using ERP.RepApp.Core;
using Java.Util;
using AColor = Android.Graphics.Color;
using APaint = Android.Graphics.Paint;
using ARect = Android.Graphics.Rect;
using ALayout = Android.Text.Layout;

namespace ERP.RepApp;

/// <summary>
/// طباعة وصل البيع في أندرويد: يُرسم الوصل صورةً (العربي سليم مهما كانت الطابعة) ثم يُرسل بأوامر ESC/POS عبر البلوتوث
/// إلى طابعة جهاز نقاط البيع المدمجة (تظهر عادة طابعة مقترنة باسم مثل InnerPrinter) أو طابعة بلوتوث محمولة مع هاتف عادي.
/// أو تُشارك الصورة (واتساب للزبون، أو تطبيق طباعة مثل RawBT).
/// </summary>
public static class ReceiptPrinter
{
    private static readonly UUID Spp = UUID.FromString("00001101-0000-1000-8000-00805F9B34FB")!;

    // ============================ الرسم ============================

    public static Bitmap Render(Receipt r, int width)
    {
        const int pad = 8;
        var inner = width - 2 * pad;
        var scale = width / 384f;
        var bmp = Bitmap.CreateBitmap(width, 700 + (int)(r.Lines.Count * 110 * scale) + 260, Bitmap.Config.Argb8888!)!;
        var canvas = new Canvas(bmp);
        canvas.DrawColor(AColor.White);
        float y = pad;

        StaticLayout Make(string text, float size, bool bold, ALayout.Alignment align, int w)
        {
            var paint = new TextPaint(PaintFlags.AntiAlias) { Color = AColor.Black, TextSize = size * scale };
            paint.SetTypeface(bold ? Typeface.DefaultBold : Typeface.Default);
            return StaticLayout.Builder.Obtain(text, 0, text.Length, paint, w)!
                .SetAlignment(align)!.SetTextDirection(TextDirectionHeuristics.Rtl!)!.Build()!;
        }
        void Text(string text, float size, bool bold = false, ALayout.Alignment? align = null)
        {
            var l = Make(text, size, bold, align ?? ALayout.Alignment.AlignNormal!, inner);
            canvas.Save();
            canvas.Translate(pad, y);
            l.Draw(canvas);
            canvas.Restore();
            y += l.Height + 4 * scale;
        }
        // صف من ثلاث خانات: العدد (يمين) والسعر والمبلغ (يسار)
        void Row(string qty, string price, string total, bool bold = false)
        {
            var w = inner / 3;
            var h = 0;
            foreach (var (text, i) in new[] { (qty, 2), (price, 1), (total, 0) })
            {
                var l = Make(text, 21, bold, ALayout.Alignment.AlignCenter!, w);
                canvas.Save();
                canvas.Translate(pad + i * w, y);
                l.Draw(canvas);
                canvas.Restore();
                h = Math.Max(h, l.Height);
            }
            y += h + 4 * scale;
        }
        void Rule(float thickness = 2)
        {
            canvas.DrawLine(pad, y + 4, width - pad, y + 4, new APaint { Color = AColor.Black, StrokeWidth = thickness * scale });
            y += 12 * scale;
        }
        var center = ALayout.Alignment.AlignCenter!;

        if (r.Logo is { Length: > 0 } && BitmapFactory.DecodeByteArray(r.Logo, 0, r.Logo.Length) is { } logo)
        {
            var k = Math.Min(inner * 0.5f / logo.Width, 140 * scale / logo.Height);
            var (lw, lh) = ((int)(logo.Width * k), (int)(logo.Height * k));
            canvas.DrawBitmap(logo, null, new ARect((width - lw) / 2, (int)y, (width + lw) / 2, (int)y + lh), new APaint(PaintFlags.FilterBitmap));
            y += lh + 6 * scale;
        }
        Text(r.Company, 30, true, center);
        Text($"وصل {r.Kind}", 24, true, center);
        Text($"رقم الوصل: {r.Number}", 22);
        Text($"التاريخ: {r.At:yyyy/MM/dd HH:mm}", 22);
        Text($"الزبون: {r.Customer}", 22);
        Rule();
        Row("العدد", "السعر", "المبلغ", true);
        Rule(1);
        foreach (var l in r.Lines)
        {
            Text(Receipt.LineName(l), 22, true);
            Row(Formats.Qty(l.Quantity), l.Total > 0 ? Formats.Qty(l.UnitPrice) : "—", l.Total > 0 ? Formats.Qty(l.Total) : "مجاني");
        }
        Rule();
        Text($"الإجمالي: {Formats.Money(r.Total)}", 28, true, center);
        Rule(1);
        Text($"المندوب: {r.Rep}", 22);
        Text(r.Thanks, 26, true, center);
        y += 10 * scale;

        var height = Math.Min((int)y, bmp.Height);
        return Bitmap.CreateBitmap(bmp, 0, 0, width, height)!;
    }

    public static byte[] ToEscPos(Bitmap bmp)
    {
        var (w, h) = (bmp.Width, bmp.Height);
        var pixels = new int[w * h];
        bmp.GetPixels(pixels, 0, w, 0, 0, w, h);
        return EscPos.Raster(EscPos.ToMono(pixels, w, h), w, h);
    }

    public static async Task<string> SavePngAsync(Bitmap bmp, string name)
    {
        var path = System.IO.Path.Combine(FileSystem.CacheDirectory, $"{name}.png");
        await using var file = File.Create(path);
        await bmp.CompressAsync(Bitmap.CompressFormat.Png!, 100, file);
        return path;
    }

    // ============================ البلوتوث ============================

    private static BluetoothAdapter Adapter() =>
        (Android.App.Application.Context.GetSystemService(Android.Content.Context.BluetoothService) as BluetoothManager)?.Adapter
        ?? throw new InvalidOperationException("لا يوجد بلوتوث في هذا الجهاز — شارك صورة الوصل");

    /// <summary>الأجهزة المقترنة (الطابعة المدمجة في أجهزة نقاط البيع تظهر هنا عادة).</summary>
    public static List<(string Name, string Address)> Paired()
    {
        var adapter = Adapter();
        if (!adapter.IsEnabled) throw new InvalidOperationException("شغّل البلوتوث ثم أعد المحاولة");
        return (adapter.BondedDevices ?? new List<BluetoothDevice>())
            .Select(d => (d.Name ?? d.Address ?? "", d.Address ?? ""))
            .Where(d => d.Item2.Length > 0)
            .OrderByDescending(d => d.Item1.Contains("print", StringComparison.OrdinalIgnoreCase) || d.Item1.Contains("POS", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static Task SendAsync(string address, byte[] data) => Task.Run(() =>
    {
        var adapter = Adapter();
        if (!adapter.IsEnabled) throw new InvalidOperationException("شغّل البلوتوث ثم أعد الطباعة");
        var device = adapter.GetRemoteDevice(address) ?? throw new InvalidOperationException("الطابعة غير موجودة — اخترها من جديد");
        using var socket = device.CreateRfcommSocketToServiceRecord(Spp) ?? throw new InvalidOperationException("تعذّر الاتصال بالطابعة");
        try
        {
            socket.Connect();
            var stream = socket.OutputStream!;
            for (var i = 0; i < data.Length; i += 1024)
            {
                stream.Write(data, i, Math.Min(1024, data.Length - i));
                stream.Flush();
                Thread.Sleep(20);                    // مخزن الطابعة صغير
            }
            Thread.Sleep(400);
        }
        catch (Java.IO.IOException ex)
        {
            throw new InvalidOperationException($"تعذّر الاتصال بالطابعة — تأكد أنها مشغّلة وقريبة ({ex.Message})");
        }
        finally
        {
            socket.Close();
        }
    });
}

/// <summary>إذن الاتصال بالبلوتوث (أندرويد 12 فأحدث يطلبه وقت التشغيل).</summary>
public class BluetoothConnectPermission : Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
        OperatingSystem.IsAndroidVersionAtLeast(31)
            ? new[] { ("android.permission.BLUETOOTH_CONNECT", true) }
            : new[] { ("android.permission.BLUETOOTH", false) };
}
