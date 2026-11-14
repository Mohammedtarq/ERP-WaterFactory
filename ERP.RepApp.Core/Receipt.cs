using ERP.Cloud.Contracts;

namespace ERP.RepApp.Core;

/// <summary>
/// وصل البيع كما يُطبع: شعار المعمل واسمه، رقم الوصل والتاريخ والزبون، المواد (العدد والسعر والمبلغ)، الإجمالي، اسم المندوب، والشكر.
/// يُرسم صورةً في الهاتف (فيبقى العربي سليمًا على أي طابعة) ثم يُرسل للطابعة أو يُشارك.
/// </summary>
public record Receipt(
    string Company, byte[]? Logo, string Number, DateTime At, string Kind, string Customer, string Rep,
    List<ReceiptLine> Lines, decimal Total, string Thanks = Receipt.DefaultThanks)
{
    public const string DefaultThanks = "شكرا لتعاملكم معنا";

    /// <summary>اسم السطر على الوصل: الاسم الموحد وحده («كارتون 40×330 الرحمة»)؛ والوصولات الأقدم «الصنف — العبوة».</summary>
    public static string LineName(ReceiptLine l) => l.Item.StartsWith(l.Pack) ? l.Item : $"{l.Item} — {l.Pack}";

    /// <summary>النص بلا صور (للمعاينة والاختبار، ولطابعة نصية عند الحاجة).</summary>
    public IEnumerable<string> TextLines()
    {
        yield return Company;
        yield return $"وصل {Kind} رقم {Number}";
        yield return At.ToString("yyyy/MM/dd HH:mm");
        yield return $"الزبون: {Customer}";
        foreach (var l in Lines)
        {
            // الاسم الموحد يبدأ بالعبوة («كارتون 40×330 الرحمة»)؛ الوصولات الأقدم: «الصنف — العبوة»
            yield return LineName(l);
            yield return l.Total > 0 ? $"{Formats.Qty(l.Quantity)} × {Formats.Money(l.UnitPrice)} = {Formats.Money(l.Total)}" : $"{Formats.Qty(l.Quantity)} (مجاني)";
        }
        yield return $"الإجمالي: {Formats.Money(Total)}";
        yield return $"المندوب: {Rep}";
        yield return Thanks;
    }
}

/// <summary>
/// أوامر ESC/POS لطابعات الوصولات الحرارية (المدمجة في أجهزة نقاط البيع، والبلوتوث المحمولة): الوصل صورة نقطية (GS v 0)
/// على شرائح صغيرة كي لا يمتلئ مخزن الطابعة، ثم تغذية وقطع (الطابعة بلا قاطع تتجاهله).
/// </summary>
public static class EscPos
{
    /// <summary>عرض الورق بالنقاط: 58 ملم ← 384، و80 ملم ← 576.</summary>
    public static int DotsFor(int paperMm) => paperMm >= 80 ? 576 : 384;

    /// <param name="black">نقطة سوداء لكل بكسل، صفًا صفًا (width × height).</param>
    public static byte[] Raster(bool[] black, int width, int height, int feedLines = 4, int band = 128)
    {
        if (width <= 0 || height <= 0 || black.Length < width * height) throw new ArgumentException("أبعاد الصورة غير صحيحة");
        var rowBytes = (width + 7) / 8;
        var output = new List<byte>(rowBytes * height + 64) { 0x1B, 0x40 };                 // ESC @ تهيئة
        for (var top = 0; top < height; top += band)
        {
            var rows = Math.Min(band, height - top);
            output.AddRange(new byte[] { 0x1D, 0x76, 0x30, 0x00, (byte)(rowBytes & 0xFF), (byte)(rowBytes >> 8), (byte)(rows & 0xFF), (byte)(rows >> 8) });
            for (var y = top; y < top + rows; y++)
                for (var bx = 0; bx < rowBytes; bx++)
                {
                    byte b = 0;
                    for (var bit = 0; bit < 8; bit++)
                    {
                        var x = bx * 8 + bit;
                        if (x < width && black[y * width + x]) b |= (byte)(0x80 >> bit);
                    }
                    output.Add(b);
                }
        }
        output.AddRange(new byte[] { 0x1B, 0x64, (byte)feedLines });                         // ESC d n تغذية
        output.AddRange(new byte[] { 0x1D, 0x56, 0x42, 0x00 });                              // GS V B قطع جزئي
        return output.ToArray();
    }

    /// <summary>
    /// تحويل صورة ملوّنة (ARGB) إلى أبيض وأسود للطابعة الحرارية بانتشار الخطأ (Floyd–Steinberg)،
    /// فيبقى الشعار واضحًا؛ والشفاف أبيض.
    /// </summary>
    public static bool[] ToMono(int[] argb, int width, int height)
    {
        var gray = new float[width * height];
        for (var i = 0; i < gray.Length; i++)
        {
            var c = argb[i];
            var a = ((c >> 24) & 0xFF) / 255f;
            var lum = 0.299f * ((c >> 16) & 0xFF) + 0.587f * ((c >> 8) & 0xFF) + 0.114f * (c & 0xFF);
            gray[i] = lum * a + 255 * (1 - a);
        }
        var black = new bool[gray.Length];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                var old = gray[i];
                var isBlack = old < 128;
                black[i] = isBlack;
                var err = old - (isBlack ? 0 : 255);
                if (x + 1 < width) gray[i + 1] += err * 7 / 16;
                if (y + 1 < height)
                {
                    if (x > 0) gray[i + width - 1] += err * 3 / 16;
                    gray[i + width] += err * 5 / 16;
                    if (x + 1 < width) gray[i + width + 1] += err * 1 / 16;
                }
            }
        return black;
    }
}
