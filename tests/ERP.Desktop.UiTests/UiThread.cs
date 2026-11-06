using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ERP.Presentation.Mvvm;

namespace ERP.Desktop.UiTests;

/// <summary>
/// خيط STA واحد يحمل تطبيق WPF حقيقيًا (نفس موارد App.xaml: الألوان، الأنماط، ربط الشاشات)
/// لكل الاختبارات. الاختبار يرسل عمله إليه وينتظر النتيجة.
/// </summary>
public static class UiThread
{
    private static readonly Lazy<Dispatcher> _dispatcher = new(Start);
    public static List<string> BindingErrors { get; } = new();
    public static List<Exception> Unhandled { get; } = new();

    public static string OutputDir { get; } = Path.GetFullPath(
        Environment.GetEnvironmentVariable("ERP_UI_SCREENSHOTS") ?? Path.Combine(AppContext.BaseDirectory, "ui-screenshots"));

    private static Dispatcher Start()
    {
        Dispatcher? d = null;
        var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();   // يحمّل الموارد فقط؛ OnStartup لا يُستدعى لأننا لا نستدعي Run
            app.DispatcherUnhandledException += (_, e) => { lock (Unhandled) Unhandled.Add(e.Exception); e.Handled = true; };
            AsyncRelayCommand.UnhandledErrorHandler = ex => { lock (Unhandled) Unhandled.Add(ex); };

            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(new Collector());
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

            d = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "WPF UI" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait();
        Directory.CreateDirectory(OutputDir);
        return d!;
    }

    public static Task RunAsync(Func<Task> work) => _dispatcher.Value.InvokeAsync(work).Task.Unwrap();
    public static Task RunAsync(Action work) => _dispatcher.Value.InvokeAsync(work).Task;

    /// <summary>ينتظر حتى تُنهي WPF التخطيط والرسم وكل الأعمال المعلّقة.</summary>
    public static async Task SettleAsync()
    {
        for (var i = 0; i < 3; i++)
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    public static string Save(FrameworkElement element, string relativePath)
    {
        element.UpdateLayout();
        var w = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        var h = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            ctx.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            // انعكاس الاتجاه (RTL) تطبّقه النافذة عند الرسم على الشاشة، والفرشاة تنسخ الشكل المنطقي غير المعكوس:
            // نعكسه هنا حتى تطابق الصورة ما يراه المستخدم (القائمة يمينًا والنص مقروء)
            if (element.FlowDirection == FlowDirection.RightToLeft)
                ctx.PushTransform(new MatrixTransform(-1, 0, 0, 1, w, 0));
            ctx.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, w, h));
        }
        bmp.Render(dv);
        var path = Path.Combine(OutputDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
        return path;
    }

    public static string SafeName(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));

    private sealed class Collector : TraceListener
    {
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (message is null) return;
            lock (BindingErrors) BindingErrors.Add(message);
        }
    }
}
