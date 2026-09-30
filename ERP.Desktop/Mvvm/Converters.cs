using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ERP.Presentation;

namespace ERP.Desktop.Mvvm;

/// <summary>يعرض أي قيمة Enum (أو نص حالة من قاعدة البيانات) بالعربية.</summary>
public class EnumArabicConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => ArabicLabels.Of(value);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>"#10B981" ← فرشاة، مع شفافية اختيارية للخلفيات الفاتحة خلف الأيقونات.</summary>
public class HexBrushConverter : IValueConverter
{
    public double Opacity { get; set; } = 1;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex || hex.Length == 0) return Brushes.Transparent;
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) { Opacity = Opacity };
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return Brushes.Transparent;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>null ← مخفي (أو العكس مع Invert).</summary>
public class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool hasValue = value is not null && value is not string { Length: 0 };
        return hasValue ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>الأرصدة: موجب ← أحمر (مستحق)، سالب ← أخضر، صفر ← رمادي.</summary>
public class SignBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var d = value switch { decimal x => x, double x => (decimal)x, int x => x, _ => 0m };
        return d > 0 ? new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26))
             : d < 0 ? new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69))
             : new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
