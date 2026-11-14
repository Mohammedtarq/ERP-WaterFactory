using System.IO;
using System.Media;

namespace ERP.Desktop.Views.Shell;

/// <summary>
/// نغمة تنبيه خفيفة (نغمتان قصيرتان ناعمتان) تُولَّد في الذاكرة — بلا ملفات صوت — وتُشغَّل مرة واحدة عند وصول تنبيه جديد.
/// </summary>
public static class AlertChime
{
    private static byte[]? _wav;

    public static void Play()
    {
        try
        {
            _wav ??= Build();
            using var player = new SoundPlayer(new MemoryStream(_wav));
            player.Play();   // غير متزامن: لا يعطّل الواجهة
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { }
    }

    /// <summary>E6 ثم A6، كل نغمة 140ms بتلاشٍ ناعم وعلو منخفض.</summary>
    internal static byte[] Build()
    {
        const int rate = 22050;
        var notes = new[] { (Hz: 1318.5, Ms: 140), (Hz: 1760.0, Ms: 220) };
        var samples = new List<short>();
        foreach (var (hz, ms) in notes)
        {
            var n = rate * ms / 1000;
            for (var i = 0; i < n; i++)
            {
                var t = (double)i / rate;
                var envelope = Math.Min(1, i / 400.0) * Math.Pow(1 - (double)i / n, 1.6);   // بداية ناعمة ثم تلاشٍ
                samples.Add((short)(Math.Sin(2 * Math.PI * hz * t) * envelope * 0.22 * short.MaxValue));
            }
        }
        using var ms2 = new MemoryStream();
        using var w = new BinaryWriter(ms2);
        var dataLen = samples.Count * 2;
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVEfmt "u8); w.Write(16);
        w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataLen);
        foreach (var s in samples) w.Write(s);
        return ms2.ToArray();
    }
}
