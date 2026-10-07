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
        var van = work.VanStock();
        foreach (var p in snap?.Products ?? new())
        {
            var pieces = van.GetValueOrDefault(p.ItemId);
            if (pieces == 0) continue;
            list.Children.Add(Ui.Card(new VerticalStackLayout { Children = { Ui.Text(p.Name, 18), Ui.Text(Formats.Packs(pieces, p.Levels), 20, Ui.Primary) } }));
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
