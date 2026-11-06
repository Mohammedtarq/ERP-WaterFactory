using System.Windows;

namespace ERP.Desktop.Mvvm;

/// <summary>
/// يمرّر سياق البيانات إلى عناصر خارج الشجرة المرئية (مثل أعمدة الجدول): يُعرَّف كمورد
/// ثم يُربط به العمود — مثلًا إخفاء عمود الكلفة لمن لا يملك صلاحية رؤيتها.
/// </summary>
public class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
