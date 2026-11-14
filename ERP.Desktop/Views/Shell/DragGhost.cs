using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using System.Windows.Interop;
using System.Windows.Media;

namespace ERP.Desktop.Views.Shell;

/// <summary>
/// «ظل البطاقة» أثناء السحب: بطاقة مصغّرة بأيقونة القسم ولونه واسمه، مائلة قليلًا وبظل كبير، تتبع المؤشر فوق النوافذ كلها.
/// نافذة شفافة لا تلتقط الفأرة (حتى لا تحجب هدف الإفلات) ولا تأخذ التركيز.
/// </summary>
internal sealed class DragGhost
{
    private readonly Window _window;
    private readonly Visual _owner;

    public DragGhost(Window owner, string title, string subtitle, string glyph, Brush color)
    {
        _owner = owner;
        var icon = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(11), Background = color,
            Child = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 19, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        var texts = new StackPanel { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Bold, FontSize = 15, Foreground = new SolidColorBrush(Color.FromRgb(0x0F, 0x24, 0x47)) });
        if (subtitle.Length > 0)
            texts.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x7A, 0x99)) });
        var row = new StackPanel { Orientation = Orientation.Horizontal, FlowDirection = FlowDirection.RightToLeft };
        row.Children.Add(icon);
        row.Children.Add(texts);
        var card = new Border
        {
            Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)), BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 10, 18, 10), Child = row,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { new ScaleTransform(1.05, 1.05), new RotateTransform(-4) } },
            // ظل ساقط كبير + هالة زرقاء: تبدو البطاقة معلّقة فوق الشاشة
            Effect = new DropShadowEffect { BlurRadius = 34, ShadowDepth = 20, Direction = 270, Opacity = 0.55, Color = Color.FromRgb(0x0B, 0x1A, 0x33) }
        };
        _window = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false,
            Topmost = true, ShowActivated = false, Focusable = false, IsHitTestVisible = false, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight, FlowDirection = FlowDirection.LeftToRight,
            Content = new Border { Padding = new Thickness(26, 18, 26, 44), Child = card }
        };
        _window.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(_window).Handle;
            SetWindowLong(h, GwlExStyle, GetWindowLong(h, GwlExStyle) | WsExTransparent | WsExNoActivate | WsExToolWindow);
        };
    }

    public void Show() { Move(); _window.Show(); Move(); }

    /// <summary>يضع الظل قرب المؤشر (إحداثيات الشاشة الفعلية محوّلة إلى وحدات WPF).</summary>
    public void Move()
    {
        if (!GetCursorPos(out var p)) return;
        var source = PresentationSource.FromVisual(_owner);
        var scale = source?.CompositionTarget is { } t ? t.TransformFromDevice : Matrix.Identity;
        var dip = scale.Transform(new Point(p.X, p.Y));
        _window.Left = dip.X - 40;
        _window.Top = dip.Y - 20;
    }

    public void Close() => _window.Close();

    private const int GwlExStyle = -20, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
    [StructLayout(LayoutKind.Sequential)] private struct Pt { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Pt p);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}

/// <summary>الخط الذهبي المضيء الذي يبيّن مكان إدراج البطاقة بين عناصر «مفضلتي».</summary>
internal sealed class InsertionAdorner : System.Windows.Documents.Adorner
{
    private double? _y;
    public InsertionAdorner(UIElement target) : base(target) { IsHitTestVisible = false; }

    public void Show(double y) { _y = y; InvalidateVisual(); }
    public void Hide() { _y = null; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        if (_y is not { } y) return;
        var width = ((FrameworkElement)AdornedElement).ActualWidth;
        var gold = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
        // هالة ثم الخط نفسه
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x55, 0xFB, 0xBF, 0x24)), null, new Rect(2, y - 5, Math.Max(0, width - 4), 10), 5, 5);
        dc.DrawRoundedRectangle(gold, null, new Rect(4, y - 2, Math.Max(0, width - 8), 4), 2, 2);
    }
}
