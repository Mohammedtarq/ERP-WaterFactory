namespace ERP.RepApp.Core;

/// <summary>نتيجة مزامنة الهاتف.</summary>
public record PhoneSyncResult(bool Online, int Sent, int Updated, bool SnapshotUpdated, string? Error, DateTime? FactoryLastSeenUtc = null)
{
    public string Text => Error ?? (Online
        ? $"أُرسلت {Sent} حركة، وتحدّثت {Updated} حالة{(SnapshotUpdated ? "، ونسخة العمل محدّثة" : "")}"
        : "لا اتصال — الحركات محفوظة في الهاتف");
}

/// <summary>
/// مزامنة الهاتف: يرسل ما في صندوق الإرسال بالترتيب، ثم يسأل عن نتائج ما ينتظر، ثم يجلب نسخة العمل إن تغيّرت.
/// آمنة للتكرار في أي وقت: الرقم الفريد يمنع التكرار، وانقطاع الشبكة يترك كل شيء في مكانه.
/// </summary>
public class SyncEngine
{
    private readonly LocalStore _store;
    private readonly Func<RepCloudClient> _client;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SyncEngine(LocalStore store, Func<RepCloudClient> client)
    {
        _store = store;
        _client = client;
    }

    public async Task<PhoneSyncResult> RunAsync(CancellationToken ct = default)
    {
        if (_store.Link is null) return new(false, 0, 0, false, "الهاتف غير مربوط — صوّر رمز الربط من المعمل");
        await _gate.WaitAsync(ct);
        int sent = 0, updated = 0;
        var snapshot = false;
        try
        {
            var client = _client();
            foreach (var item in _store.Queued())
            {
                try
                {
                    _store.Apply(await client.SubmitAsync(item, ct));
                    sent++;
                }
                catch (InvalidOperationException ex)
                {
                    // رفض الخادم هذه الحركة بعينها (مثلًا نوع غير معروف): تُعلَّم متعذّرة ولا توقف البقية
                    _store.Apply(new(item.ClientId, Cloud.Contracts.RelayState.Done, "Failed", ex.Message));
                }
            }

            var open = _store.AwaitingResult().Select(i => i.ClientId).ToList();
            foreach (var chunk in open.Chunk(200))
                foreach (var s in await client.StatusesAsync(chunk, ct))
                {
                    var before = _store.Find(s.ClientId);
                    _store.Apply(s);
                    if (before is not null && (before.Status != s.Status || (before.State != OutboxState.Done && s.State == Cloud.Contracts.RelayState.Done))) updated++;
                }

            var snap = await client.SnapshotAsync(_store.SnapshotEtag, ct);
            if (snap is { } s2) { _store.SaveSnapshot(s2.json, s2.etag); snapshot = true; }

            var ping = await client.PingAsync(ct);
            _store.Set("LastContactUtc", DateTime.UtcNow.ToString("O"));
            return new(true, sent, updated, snapshot, null, ping.FactoryLastSeenUtc);
        }
        catch (OfflineException) { return new(false, sent, updated, snapshot, null); }
        catch (DeviceRejectedException ex) { return new(false, sent, updated, snapshot, ex.Message); }
        finally { _gate.Release(); }
    }
}
