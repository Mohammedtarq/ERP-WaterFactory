using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace ERP.Desktop.Mvvm;

/// <summary>
/// بحث بالكتابة داخل أي قائمة منسدلة: مربع بحث أعلى القائمة يصفّي العناصر فورًا.
/// - يُفعَّل لكل ComboBox من النمط العام (Styles.xaml)، فلا تحتاج أي شاشة تعديلًا.
/// - النتائج في قائمة مستقلة داخل النافذة المنبثقة بنسخة عرض خاصة: لا تُصفّى عناصر الـ ComboBox نفسها
///   ولا أي جدول يعرض نفس البيانات، ولا يتغير الاختيار أثناء الكتابة (فقط عند النقر أو Enter).
/// - الكتابة على القائمة وهي مغلقة تفتحها وتبدأ البحث بالحرف المكتوب.
/// - البحث في الاسم المعروض وكل الحقول النصية (الكود، الباركود...)، مع توحيد الهمزات والتاء المربوطة.
/// </summary>
public static class ComboSearch
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ComboSearch), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject o) => (bool)o.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject o, bool value) => o.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox combo || e.NewValue is not true) return;
        combo.DropDownOpened += OnOpened;
        combo.DropDownClosed += OnClosed;
        combo.PreviewTextInput += OnClosedTyping;
    }

    private static TextBox? SearchBox(ComboBox c) => c.Template?.FindName("PART_Search", c) as TextBox;
    private static ListBox? Results(ComboBox c) => c.Template?.FindName("PART_Results", c) as ListBox;

    private static void OnOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox combo || SearchBox(combo) is not { } box || Results(combo) is not { } list) return;
        if (box.Tag is null)
        {
            box.Tag = combo;
            list.Tag = combo;
            box.TextChanged += (_, _) => ApplyFilter(combo, box.Text);
            box.PreviewKeyDown += OnSearchKey;
            list.PreviewMouseLeftButtonUp += OnResultClick;
            list.PreviewKeyDown += (s, k) => { if (k.Key == Key.Enter) { Commit(combo); k.Handled = true; } };
        }
        // نسخة جديدة في كل فتح: تعكس آخر بيانات القائمة
        IList source = combo.ItemsSource as IList ?? combo.Items.Cast<object>().ToList();
        list.ItemsSource = new ListCollectionView(source);
        list.SelectedItem = combo.SelectedItem;
        if (combo.SelectedItem is not null) list.ScrollIntoView(combo.SelectedItem);
        ApplyFilter(combo, box.Text);
        combo.Dispatcher.BeginInvoke(() => { box.Focus(); box.CaretIndex = box.Text.Length; }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private static void OnClosed(object? sender, EventArgs e)
    {
        if (sender is not ComboBox combo) return;
        if (SearchBox(combo) is { } box) box.Text = "";
        if (Results(combo) is { } list) list.ItemsSource = null;
    }

    private static void OnClosedTyping(object sender, TextCompositionEventArgs e)
    {
        if (sender is not ComboBox combo || combo.IsDropDownOpen || string.IsNullOrWhiteSpace(e.Text)) return;
        if (e.OriginalSource is TextBox) return;
        if (SearchBox(combo) is { } box) { box.Text = e.Text; }
        combo.IsDropDownOpen = true;
        e.Handled = true;
    }

    private static void OnResultClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox { Tag: ComboBox combo } list) return;
        if (ItemsControl.ContainerFromElement(list, (DependencyObject)e.OriginalSource) is ListBoxItem item)
        {
            list.SelectedItem = item.DataContext;
            Commit(combo);
        }
    }

    /// <summary>اعتماد العنصر المحدد في النتائج (أو أول نتيجة) كاختيار القائمة.</summary>
    private static void Commit(ComboBox combo)
    {
        if (Results(combo) is { } list)
        {
            var chosen = list.SelectedItem ?? list.Items.Cast<object>().FirstOrDefault();
            if (chosen is not null && !Equals(chosen, combo.SelectedItem)) combo.SetCurrentValue(Selector.SelectedItemProperty, chosen);
        }
        combo.IsDropDownOpen = false;
        combo.Focus();
    }

    private static void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: ComboBox combo } || Results(combo) is not { } list) return;
        switch (e.Key)
        {
            case Key.Down:
            case Key.Up:
                var count = list.Items.Count;
                if (count == 0) return;
                var i = list.SelectedIndex;
                i = e.Key == Key.Down ? Math.Min(i + 1, count - 1) : Math.Max(i - 1, 0);
                list.SelectedIndex = i;
                list.ScrollIntoView(list.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                Commit(combo);
                e.Handled = true;
                break;
            case Key.Escape:
                combo.IsDropDownOpen = false;
                combo.Focus();
                e.Handled = true;
                break;
        }
    }

    private static void ApplyFilter(ComboBox combo, string text)
    {
        if (Results(combo) is not { ItemsSource: ICollectionView view } list) return;
        var terms = Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var path = combo.DisplayMemberPath;
        view.Filter = terms.Length == 0 ? null : item =>
        {
            var hay = Normalize(SearchText(item, path));
            return terms.All(t => hay.Contains(t, StringComparison.OrdinalIgnoreCase));
        };
        // أول نتيجة محددة دائمًا ليكفي Enter
        if (terms.Length > 0 && (list.SelectedItem is null || !view.Contains(list.SelectedItem)))
            list.SelectedIndex = list.Items.Count > 0 ? 0 : -1;
    }

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Props = new();

    /// <summary>نص العنصر المعروض + كل خصائصه النصية والرقمية (الاسم، الكود، الباركود...).</summary>
    internal static string SearchText(object? item, string? displayPath = null)
    {
        if (item is null) return "";
        if (item is string or ValueType) return Convert.ToString(item, CultureInfo.InvariantCulture) ?? "";
        var props = Props.GetOrAdd(item.GetType(), t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 &&
                        (p.PropertyType == typeof(string) || p.PropertyType == typeof(int) || p.PropertyType == typeof(int?)))
            .ToArray());
        var parts = props.Select(p => { try { return p.GetValue(item)?.ToString(); } catch { return null; } });
        var display = string.IsNullOrEmpty(displayPath) ? item.ToString() : item.GetType().GetProperty(displayPath)?.GetValue(item)?.ToString();
        return string.Join(" ", parts.Prepend(display).Where(s => !string.IsNullOrEmpty(s)));
    }

    /// <summary>توحيد الكتابة العربية: أ/إ/آ ← ا، ة ← ه، ى ← ي، وحذف التشكيل — "اسامة" تجد "أسامة".</summary>
    internal static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var chars = new List<char>(s.Length);
        foreach (var c in s.Trim())
        {
            if (c is >= 'ً' and <= 'ْ' or 'ـ') continue;   // تشكيل وتطويل
            chars.Add(c switch { 'أ' or 'إ' or 'آ' => 'ا', 'ة' => 'ه', 'ى' => 'ي', _ => c });
        }
        return new string(chars.ToArray());
    }
}
