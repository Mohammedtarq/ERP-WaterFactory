using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>حركاتي: مجموع مواد بيع اليوم، ثم كل حركة بموادها وأعدادها وحالتها ورسالة المعمل؛ والمتعذّرة تُعاد بعد زوال سببها.</summary>
public class OutboxPage : ContentPage
{
    private readonly VerticalStackLayout _list = new() { Spacing = 4 };

    public OutboxPage()
    {
        Ui.Setup(this, "حركاتي", _list);
        Fill();
    }

    private void Fill()
    {
        _list.Children.Clear();
        var store = AppServices.Instance.Store;
        var items = store.Outbox(days: 30).Take(150).ToList();
        if (items.Count == 0) { _list.Children.Add(Ui.Text("لا حركات بعد", 17, Ui.Muted)); return; }
        var today = AppServices.Instance.Work.TodayItems();
        if (today.Count > 0)
        {
            var sum = new VerticalStackLayout { Spacing = 2, Children = { Ui.Text("مواد بيع اليوم", 17, Ui.Primary) } };
            foreach (var t in today)
                sum.Children.Add(Ui.Text(t.Item.StartsWith(t.Pack) ? $"{t.Item}: {Formats.Qty(t.Quantity)}" : $"{t.Item} — {Formats.Qty(t.Quantity)} {t.Pack}", 16));
            _list.Children.Add(Ui.Card(sum, Ui.Primary));
        }
        foreach (var i in items)
        {
            var color = i.State != OutboxState.Done ? Ui.Amber
                : i.Status switch { "Posted" => Ui.Green, "Pending" => Ui.Blue, _ => Ui.Red };
            var body = new VerticalStackLayout
            {
                Spacing = 3,
                Children =
                {
                    Ui.Text(i.ReceiptNo is int no ? $"وصل رقم {no} — {i.Summary.Split(':')[0]}" : i.Summary, 16),
                    Ui.Text($"{Formats.Money(i.Amount)} — {i.OccurredAt:yyyy/MM/dd HH:mm}", 14, Ui.Muted),
                    Ui.Text(i.StateText, 15, color)
                }
            };
            var at = 1;
            foreach (var l in i.Lines ?? new())
                body.Children.Insert(at++, Ui.Text($"• {Formats.Qty(l.Quantity)} {l.Pack} {l.Item}" + (l.Total > 0 ? $" = {Formats.Money(l.Total)}" : ""), 15));
            if (i.State == OutboxState.Done && !string.IsNullOrEmpty(i.Message) && i.Status != "Posted") body.Children.Add(Ui.Text(i.Message, 14, color));
            if (!string.IsNullOrEmpty(i.Warning)) body.Children.Add(Ui.Text($"⚠ {i.Warning}", 14, Ui.Amber));
            if (i.ReceiptNo is not null)
            {
                var sale = i;
                body.Children.Add(new HorizontalStackLayout
                {
                    Spacing = 8,
                    Children =
                    {
                        Ui.Small("طباعة الوصل", Ui.Primary, async () => await ReceiptActions.PrintAsync(this, sale)),
                        Ui.Small("مشاركة", Ui.Blue, async () => await ReceiptActions.ShareAsync(this, sale))
                    }
                });
            }
            if (i.CanResubmit)
            {
                var id = i.ClientId;
                body.Children.Add(Ui.Small("إعادة الإرسال", Ui.Primary, () =>
                {
                    store.ResubmitAsIs(id);
                    AppServices.Instance.SyncSoon();
                    Fill();
                }));
            }
            _list.Children.Add(Ui.Card(body, color));
        }
    }
}
