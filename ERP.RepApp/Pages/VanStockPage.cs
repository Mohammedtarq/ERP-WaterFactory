using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>رصيد السيارة بالعبوات (بعد حركات الهاتف التي لم تصل نسخة العمل بعد)، والنقد مع المندوب.</summary>
public class VanStockPage : ContentPage
{
    public VanStockPage()
    {
        var work = AppServices.Instance.Work;
        var list = new VerticalStackLayout { Spacing = 4 };
        var snap = work.Snapshot;
        var van = work.VanStockByVariant();
        foreach (var p in snap?.Products ?? new())
            foreach (var (key, pieces) in van.Where(kv => kv.Key.ItemId == p.ItemId && kv.Value != 0).OrderBy(kv => kv.Key.RecipeId is null ? 0 : 1))
            {
                // الاسم الموحد بعبوته الكبرى: «كارتون 40×330 الرحمة»، والأساسي والطلب الخاص كل في بطاقة
                var top = p.Levels.OrderByDescending(l => l.Units).First();
                list.Children.Add(Ui.Card(new VerticalStackLayout { Children = { Ui.Text(p.Label(top, key.RecipeId), 18), Ui.Text(Formats.Packs(pieces, p.Levels), 20, Ui.Primary) } }));
            }
        if (list.Children.Count == 0) list.Children.Add(Ui.Text(snap is null ? "لم تصل نسخة العمل بعد — زامن أولًا" : "السيارة فارغة", 17, Ui.Muted));
        Ui.Setup(this, "رصيد السيارة", new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                Ui.Card(new VerticalStackLayout { Children = { Ui.Text("النقد معك", 15, Ui.Muted), Ui.Text(Formats.Money(work.Wallet()), 22) } }, Ui.Primary),
                list,
                Ui.Text(snap is null ? "" : $"آخر نسخة عمل من المعمل: {snap.GeneratedAtUtc.ToLocalTime():yyyy/MM/dd HH:mm}", 13, Ui.Muted)
            }
        });
    }
}
