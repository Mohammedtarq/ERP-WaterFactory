using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>حركاتي: كل حركة بحالتها ورسالة المعمل؛ والمتعذّرة تُعاد بعد زوال سببها.</summary>
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
        foreach (var i in items)
        {
            var color = i.State != OutboxState.Done ? Ui.Amber
                : i.Status switch { "Posted" => Ui.Green, "Pending" => Ui.Blue, _ => Ui.Red };
            var body = new VerticalStackLayout
            {
                Spacing = 3,
                Children =
                {
                    Ui.Text(i.Summary, 16),
                    Ui.Text($"{Formats.Money(i.Amount)} — {i.OccurredAt:yyyy/MM/dd HH:mm}", 14, Ui.Muted),
                    Ui.Text(i.StateText, 15, color)
                }
            };
            if (i.State == OutboxState.Done && !string.IsNullOrEmpty(i.Message) && i.Status != "Posted") body.Children.Add(Ui.Text(i.Message, 14, color));
            if (!string.IsNullOrEmpty(i.Warning)) body.Children.Add(Ui.Text($"⚠ {i.Warning}", 14, Ui.Amber));
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
