using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ERP.Data.Services;
using ERP.Desktop.Printing;

namespace ERP.Desktop.Views.Controls;

/// <summary>
/// صورة شخصية دائرية (موظف أو مستخدم). بلا صورة: أول حرفين من الاسم في دائرة بلون ثابت مشتق من الاسم،
/// فيبقى لكل شخص لونه نفسه في كل الشاشات.
/// </summary>
public class Avatar : Border
{
    private static readonly string[] Palette = { "#0EA5E9", "#10B981", "#F59E0B", "#8B5CF6", "#EC4899", "#14B8A6", "#F97316", "#6366F1" };

    public static readonly DependencyProperty PhotoProperty =
        DependencyProperty.Register(nameof(Photo), typeof(object), typeof(Avatar), new PropertyMetadata(null, (d, _) => ((Avatar)d).Refresh()));
    public static readonly DependencyProperty DisplayNameProperty =
        DependencyProperty.Register(nameof(DisplayName), typeof(string), typeof(Avatar), new PropertyMetadata(null, (d, _) => ((Avatar)d).Refresh()));
    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(Avatar), new PropertyMetadata(32.0, (d, _) => ((Avatar)d).Refresh()));

    private readonly TextBlock _initials = new()
    {
        Foreground = Brushes.White, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.RightToLeft
    };

    public Avatar()
    {
        Child = _initials;
        VerticalAlignment = VerticalAlignment.Center;
        Refresh();
    }

    /// <summary>بايتات الصورة (byte[]) — النوع object لأن مترجم XAML لا يقبل خاصية مصفوفة داخل القوالب.</summary>
    public object? Photo { get => GetValue(PhotoProperty); set => SetValue(PhotoProperty, value); }
    public string? DisplayName { get => (string?)GetValue(DisplayNameProperty); set => SetValue(DisplayNameProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    private void Refresh()
    {
        Width = Height = Size;
        CornerRadius = new CornerRadius(Size / 2);
        if (ReportRenderer.LoadLogo(Photo as byte[]) is { } image)
        {
            Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            _initials.Visibility = Visibility.Collapsed;
            return;
        }
        var name = DisplayName ?? "";
        int hash = 0;
        foreach (var ch in name) hash = unchecked(hash * 31 + ch);
        Background = (Brush)new BrushConverter().ConvertFromString(Palette[(hash & 0x7FFFFFFF) % Palette.Length])!;
        _initials.Text = PhotoService.Initials(name);
        _initials.FontSize = Math.Max(9, Size * 0.36);
        _initials.Visibility = Visibility.Visible;
    }
}
