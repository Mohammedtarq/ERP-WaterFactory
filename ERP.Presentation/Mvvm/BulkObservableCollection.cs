using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ERP.Presentation.Mvvm;

/// <summary>
/// مجموعة تستبدل كل عناصرها بإشعار واحد. الإضافة عنصرًا عنصرًا تُطلق إشعارًا لكل عنصر، والقوائم المنسدلة والجداول المربوطة
/// تعالج كل إشعار على حدة (وفي المنسدلة تبحث عن القيمة المختارة بعد كل إضافة): مع آلاف العملاء تصير الشاشة معلّقة ثوانيَ عند فتحها.
/// </summary>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public static class CollectionExtensions
{
    /// <summary>يستبدل المحتوى بإشعار واحد إن كانت المجموعة BulkObservableCollection، وإلا عنصرًا عنصرًا.</summary>
    public static void ReplaceAll<T>(this ObservableCollection<T> target, IEnumerable<T> items)
    {
        if (target is BulkObservableCollection<T> bulk) { bulk.ReplaceAll(items); return; }
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}
