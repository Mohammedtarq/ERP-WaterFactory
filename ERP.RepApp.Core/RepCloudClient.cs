using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ERP.Cloud.Contracts;

namespace ERP.RepApp.Core;

/// <summary>لا شبكة أو الخادم لا يرد: الحركات تبقى في الهاتف وتُرسل لاحقًا.</summary>
public class OfflineException(string message) : Exception(message);

/// <summary>الخادم رفض الجهاز (غير مسجّل أو موقوف): لا فائدة من إعادة المحاولة حتى تراجع الإدارة.</summary>
public class DeviceRejectedException(string message) : Exception(message);

/// <summary>اتصال الهاتف بالخادم السحابي بمفتاح جهازه.</summary>
public class RepCloudClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public RepCloudClient(LinkCode link, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri(link.ServerUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.Add(CloudRoutes.DeviceKeyHeader, link.DeviceKey);
    }

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
        using var req = new HttpRequestMessage(HttpMethod.Get, CloudRoutes.RepSnapshot);
        if (etag is not null) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var res = await RawAsync(req, ct);
        if (res.StatusCode is HttpStatusCode.NotModified or HttpStatusCode.NotFound) return null;
        await EnsureAsync(res, ct);
        return (await res.Content.ReadAsStringAsync(ct), res.Headers.ETag?.Tag);
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        using var res = await RawAsync(req, ct);
        await EnsureAsync(res, ct);
        try { return await res.Content.ReadFromJsonAsync<T>(Json, ct); }
        catch (JsonException) { throw new OfflineException("رد الخادم غير مفهوم — تحقق من العنوان"); }
    }

    private async Task<HttpResponseMessage> RawAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try { return await _http.SendAsync(req, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new OfflineException("لا اتصال بالخادم — الحركات محفوظة في الهاتف وتُرسل عند عودة الشبكة");
        }
    }

    private static async Task EnsureAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        string? message = null;
        try { message = (await res.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).GetProperty("error").GetString(); }
        catch (Exception) { /* رد بلا رسالة */ }
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new DeviceRejectedException(message ?? "الجهاز غير مسجّل أو موقوف — راجع الإدارة");
        if ((int)res.StatusCode >= 500) throw new OfflineException(message ?? "الخادم غير متاح الآن — المحاولة لاحقًا");
        throw new InvalidOperationException(message ?? $"رفض الخادم الطلب ({(int)res.StatusCode})");
    }
}
