using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ERP.Cloud.Contracts;

namespace ERP.RepApp.Core;

/// <summary>لا شبكة أو الخادم لا يرد: الحركات تبقى في الهاتف وتُرسل لاحقًا.</summary>
public class OfflineException(string message) : Exception(message);

/// <summary>الخادم رفض الجهاز (غير مسجّل أو موقوف): لا فائدة من إعادة المحاولة حتى تراجع الإدارة.</summary>
public class DeviceRejectedException(string message) : Exception(message);

/// <summary>
/// اتصال الهاتف بخوادم المعمل بمفتاح جهازه. مع النظامين معًا: الخادم المحلي أولًا (واي فاي المعمل، أسرع وبلا إنترنت)
/// ثم السحابي، ويبقى على آخر خادم نجح (ويعيد تجربة المحلي كل دقيقتين). إن لم يرد أي منهما وفي الرمز بصمة المعمل،
/// يبحث عن الخادم المحلي على الشبكة (تغيّر عنوان الحاسبة) ويحفظ عنوانه الجديد.
/// </summary>
public class RepCloudClient
{
    private readonly HttpClient _http;
    private readonly Func<LinkCode> _link;
    private readonly Action<string>? _onLocalFound;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string? _active;
    private DateTime _nextLocalTry;
    private DateTime _nextDiscovery;

    public static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan CloudTimeout = TimeSpan.FromSeconds(30);

    /// <summary>البحث عن الخادم المحلي (الاختبارات تستبدله).</summary>
    public Func<string, CancellationToken, Task<string?>> Discover { get; set; } =
        (fid, ct) => LocalDiscovery.FindAsync(fid, TimeSpan.FromSeconds(2), ct: ct);

    public RepCloudClient(LinkCode link, HttpMessageHandler? handler = null) : this(() => link, null, handler) { }

    /// <summary>الرمز يُقرأ عند كل طلب (يتحدّث من نسخة العمل أو الاكتشاف)، وما يُكتشف يُحفظ بـ onLocalFound.</summary>
    public RepCloudClient(Func<LinkCode> link, Action<string>? onLocalFound, HttpMessageHandler? handler = null)
    {
        _link = link;
        _onLocalFound = onLocalFound;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;   // مهلة لكل خادم على حدة
    }

    /// <summary>آخر خادم ردّ، و«محلي» إن كان خادم واي فاي المعمل.</summary>
    public string? ActiveUrl => _active;
    public bool ViaLocal => _active is not null && _link().LocalUrl is { } l ? string.Equals(_active, l, StringComparison.OrdinalIgnoreCase)
                                                                              : _active is not null && IsLan(_active);

    private static bool IsLan(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && (CloudUrl.IsPrivateLan(u.Host) || u.IsLoopback);

    public Task<PhoneRequestStatus> SubmitAsync(OutboxItem item, CancellationToken ct = default) =>
        SendAsync<PhoneRequestStatus>(HttpMethod.Post, CloudRoutes.RepRequests,
            new PhoneRequest(item.ClientId, item.Kind, item.OccurredAt, item.Payload, item.Photo), ct)!;

    public async Task<List<PhoneRequestStatus>> StatusesAsync(IEnumerable<Guid> ids, CancellationToken ct = default) =>
        await SendAsync<List<PhoneRequestStatus>>(HttpMethod.Post, CloudRoutes.RepStatuses, new PhoneStatusQuery(ids.ToList()), ct) ?? new();

    public async Task<PhonePing> PingAsync(CancellationToken ct = default) =>
        (await SendAsync<PhonePing>(HttpMethod.Get, CloudRoutes.RepPing, null, ct))!;

    /// <returns>(json, etag) أو null إن لم تتغير نسخة العمل منذ etag، أو لم تصل من المعمل بعد.</returns>
    public async Task<(string json, string? etag)?> SnapshotAsync(string? etag, CancellationToken ct = default)
    {
        using var res = await RawAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, CloudRoutes.RepSnapshot);
            if (etag is not null) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
            return req;
        }, ct);
        if (res.StatusCode is HttpStatusCode.NotModified or HttpStatusCode.NotFound) return null;
        await EnsureAsync(res, ct);
        return (await res.Content.ReadAsStringAsync(ct), res.Headers.ETag?.Tag);
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var res = await RawAsync(() =>
        {
            var req = new HttpRequestMessage(method, path);
            if (body is not null) req.Content = JsonContent.Create(body, options: Json);
            return req;
        }, ct);
        await EnsureAsync(res, ct);
        try { return await res.Content.ReadFromJsonAsync<T>(Json, ct); }
        catch (JsonException) { throw new OfflineException("رد الخادم غير مفهوم — تحقق من العنوان"); }
    }

    /// <summary>ترتيب المحاولة: آخر خادم نجح أولًا، إلا أن المحلي يُجرَّب أولًا كل دقيقتين (عاد المندوب إلى المعمل).</summary>
    private List<string> Order(LinkCode link)
    {
        var all = link.Candidates.ToList();
        if (_active is not null && all.Remove(_active)) all.Insert(0, _active);
        if (link.LocalUrl is { } local && DateTime.UtcNow >= _nextLocalTry && all.Remove(local)) all.Insert(0, local);
        return all;
    }

    private async Task<HttpResponseMessage> RawAsync(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        var link = _link();
        var (res, rejected) = await TryAllAsync(Order(link), link, make, ct);
        if (res is not null) return res;

        // لا خادم يرد: ربما تغيّر عنوان حاسبة المعمل — يُبحث عنها على الشبكة (مرة كل دقيقة على الأكثر)
        if (rejected is null && link.FactoryId is { } fid && DateTime.UtcNow >= _nextDiscovery)
        {
            _nextDiscovery = DateTime.UtcNow.AddMinutes(1);
            var found = await Discover(fid, ct);
            if (found is not null && !link.Candidates.Contains(found, StringComparer.OrdinalIgnoreCase))
            {
                _onLocalFound?.Invoke(found);
                var moved = link with { LocalUrl = found };
                (res, rejected) = await TryAllAsync(new List<string> { found }, moved, make, ct);
                if (res is not null) return res;
            }
        }
        if (rejected is not null) throw rejected;
        throw new OfflineException("لا اتصال بالخادم — الحركات محفوظة في الهاتف وتُرسل عند عودة الشبكة");
    }

    /// <returns>أول رد صالح، أو رفض الجهاز إن رفضه خادم ولم يقبله غيره.</returns>
    private async Task<(HttpResponseMessage?, DeviceRejectedException?)> TryAllAsync(List<string> order, LinkCode link, Func<HttpRequestMessage> make, CancellationToken ct)
    {
        DeviceRejectedException? rejected = null;
        foreach (var url in order)
        {
            var local = string.Equals(url, link.LocalUrl, StringComparison.OrdinalIgnoreCase);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(local ? LocalTimeout : CloudTimeout);
            var req = make();
            req.RequestUri = new Uri(new Uri(url.TrimEnd('/') + "/"), req.RequestUri!.OriginalString);
            req.Headers.TryAddWithoutValidation(CloudRoutes.DeviceKeyHeader, link.DeviceKey);
            HttpResponseMessage res;
            try { res = await _http.SendAsync(req, timeout.Token); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                if (local) _nextLocalTry = DateTime.UtcNow.AddMinutes(2);
                continue;
            }
            finally { req.Dispose(); }
            if (res.StatusCode == HttpStatusCode.Unauthorized || (int)res.StatusCode >= 500)
            {
                // هذا الخادم لا يعرف الجهاز بعد (نُشر حديثًا) أو متعطل: يُجرَّب الآخر
                if (res.StatusCode == HttpStatusCode.Unauthorized) rejected = new DeviceRejectedException(await ErrorOf(res, ct) ?? "الجهاز غير مسجّل أو موقوف — راجع الإدارة");
                res.Dispose();
                if (local) _nextLocalTry = DateTime.UtcNow.AddMinutes(2);
                continue;
            }
            _active = url;
            if (local) _nextLocalTry = DateTime.MinValue;
            return (res, null);
        }
        return (null, rejected);
    }

    private static async Task<string?> ErrorOf(HttpResponseMessage res, CancellationToken ct)
    {
        try { return (await res.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).GetProperty("error").GetString(); }
        catch (Exception) { return null; }
    }

    private static async Task EnsureAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var message = await ErrorOf(res, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new DeviceRejectedException(message ?? "الجهاز غير مسجّل أو موقوف — راجع الإدارة");
        if ((int)res.StatusCode >= 500) throw new OfflineException(message ?? "الخادم غير متاح الآن — المحاولة لاحقًا");
        throw new InvalidOperationException(message ?? $"رفض الخادم الطلب ({(int)res.StatusCode})");
    }
}
