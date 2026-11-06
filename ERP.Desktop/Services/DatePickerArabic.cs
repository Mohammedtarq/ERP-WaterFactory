using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace ERP.Desktop.Services;

/// <summary>
/// حقول التاريخ في كل الشاشات: الصيغة 2026/10/15 كبقية النظام (بدل الأمريكية 10/15/2026)،
/// والتلميح «اختر التاريخ» بدل «Select a date» الإنجليزي. يُسجَّل مرة لكل DatePicker في التطبيق.
/// </summary>
public static class DatePickerArabic
{
    public const string Format = "yyyy/MM/dd";
    public const string Hint = "اختر التاريخ";

    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(DatePicker), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Apply((DatePicker)s)));
        EventManager.RegisterClassHandler(typeof(DatePicker), DatePicker.SelectedDateChangedEvent,
            new EventHandler<SelectionChangedEventArgs>((s, _) => Apply((DatePicker)s)));
        EventManager.RegisterClassHandler(typeof(DatePicker), UIElement.LostKeyboardFocusEvent,
            new RoutedEventHandler((s, _) => Apply((DatePicker)s)));
    }

    private static void Apply(DatePicker dp)
    {
        // بعد أن يكتب DatePicker نصه بصيغة الثقافة، نعيد كتابته بصيغة النظام (والتحويل العكسي يقبل yyyy/MM/dd)
        dp.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            dp.ApplyTemplate();
            if (dp.Template?.FindName("PART_TextBox", dp) is not DatePickerTextBox box) return;
            box.ApplyTemplate();
            if (box.Template?.FindName("PART_Watermark", box) is ContentControl watermark) watermark.Content = Hint;
            if (dp.SelectedDate is DateTime d && !box.IsKeyboardFocusWithin)
                box.Text = d.ToString(Format, CultureInfo.InvariantCulture);
        });
    }
}
