using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ERP.Cloud.Contracts;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>نتيجة دورة مزامنة واحدة.</summary>
public record CloudSyncReport(bool Ok, int Received, int Posted, int Pending, int Failed, int Updated, int Snapshots, string? Error)
{
    public string Text => Ok
        ? $"تمت المزامنة: وصلت {Received} حركة (رُحّل {Posted}، بانتظار الاعتماد {Pending}، تعذّر {Failed})، وتحدّثت {Updated} حالة، و{Snapshots} نسخة عمل"
        : Error ?? "تعذّرت المزامنة";
}

/// <summary>ذاكرة خدمة المزامنة بين الدورات: آخر فحص لنسخ العمل (كل دقيقة، أو فورًا بعد حركات جديدة).</summary>
public class CloudSyncState
{
    public Dictionary<int, string>? SnapshotHashes { get; set; }
    public DateTime? LastSnapshotCheckUtc { get; set; }
    public static readonly TimeSpan SnapshotEvery = TimeSpan.FromSeconds(60);
}

/// <summary>خطأ من الخادم برسالة عربية جاهزة للعرض.</summary>
public class CloudSyncException(string message) : Exception(message);

/// <summary>
/// خدمة المزامنة في المعمل (المرحلة 1). تتصل هي بالخادم السحابي ولا يُفتح أي منفذ في المعمل:
/// 1) ترفع قائمة الأجهزة (بصمات المفاتيح فقط، والموقوف موقوف)؛
/// 2) تسحب حركات الهواتف وترحّلها بـ <see cref="RepAppService"/> نفسها (الصلاحيات، قفل الشهر، حد الدين) ثم تعيد نتائجها؛
/// 3) تحدّث حالة المعلّقة التي اعتُمدت أو رُفضت؛
/// 4) ترفع «نسخة عمل» كل مندوب إن تغيّرت.
/// الرقم الفريد لكل حركة يجعل الانقطاع وإعادة المحاولة آمنين: لا ضياع ولا تكرار.
/// </summary>
public class CloudSyncService
{
    private readonly ProjectDbContext _db;
    public CloudSyncService(ProjectDbContext db) => _db = db;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int Batch = 20;
    private const int MaxBatchesPerRun = 5;

    /// <summary>عميل اتصال بالخادم: العنوان ومفتاح المعمل ومهلة 30 ثانية.</summary>
    public static HttpClient CreateClient(string serverUrl, string agentKey, HttpMessageHandler? handler = null)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.Add(CloudRoutes.AgentKeyHeader, agentKey);
        return http;
    }

    // ============================ الإعداد ============================

    public async Task<CloudSyncSetting> SettingsAsync() =>
        await _db.CloudSyncSettings.AsNoTracking().FirstOrDefaultAsync() ?? new CloudSyncSetting();

    /// <summary>عنوان https، أو http داخل شبكة المعمل للتجربة (القاعدة نفسها في الهاتف).</summary>
    public static string? ValidateUrl(string? url) => CloudUrl.Validate(url);

    public async Task<FinanceOperationResult> SaveSettingsAsync(string? serverUrl, bool enabled, int intervalSeconds, int userId)
    {
        if (intervalSeconds is < 5 or > 600) return FinanceOperationResult.Fail("فترة المزامنة بين 5 و600 ثانية");
        var url = string.IsNullOrWhiteSpace(serverUrl) ? null : serverUrl.Trim().TrimEnd('/');
        if ((enabled || url is not null) && ValidateUrl(url) is string bad) return FinanceOperationResult.Fail(bad);
        var s = await _db.CloudSyncSettings.FirstOrDefaultAsync();
        if (s is null) _db.CloudSyncSettings.Add(s = new CloudSyncSetting());
        if (enabled && string.IsNullOrEmpty(s.AgentKey)) return FinanceOperationResult.Fail("ولّد مفتاح المعمل أولًا والصقه في إعدادات الخادم");
        (s.ServerUrl, s.IsEnabled, s.IntervalSeconds) = (url, enabled, intervalSeconds);
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Edit", "CloudSyncSettings", 1,
            $"المزامنة السحابية: {(enabled ? "مفعّلة" : "موقوفة")} — {url ?? "بلا عنوان"} — كل {intervalSeconds} ثانية");
        return FinanceOperationResult.Ok();
    }

    /// <returns>مفتاح المعمل الجديد: يُلصق في إعداد الخادم Relay:AgentKey (القديم يتوقف فورًا).</returns>
    public async Task<string> GenerateKeyAsync(int userId)
    {
        var s = await _db.CloudSyncSettings.FirstOrDefaultAsync();
        if (s is null) _db.CloudSyncSettings.Add(s = new CloudSyncSetting());
        s.AgentKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Edit", "CloudSyncSettings", 1, "توليد مفتاح معمل جديد للمزامنة السحابية");
        return s.AgentKey;
    }

    /// <summary>اختبار الاتصال: الخادم يرد ومفتاح المعمل مقبول.</summary>
    public static async Task<(bool ok, string message)> TestAsync(HttpClient http, CancellationToken ct = default)
    {
        try
        {
            var ping = await SendAsync<AgentPing>(http, HttpMethod.Get, CloudRoutes.AgentPing, null, ct);
            return (true, $"الاتصال ناجح — إصدار الخادم {ping.Version}، وحركات تنتظر المعمل: {ping.Waiting}");
        }
        catch (CloudSyncException ex) { return (false, ex.Message); }
    }

    // ============================ الدورة ============================

    public async Task<CloudSyncReport> RunOnceAsync(HttpClient http, string machine, CloudSyncState? state = null, CancellationToken ct = default)
    {
        state ??= new CloudSyncState();
        int received = 0, posted = 0, pending = 0, failed = 0, updated = 0, snapshots = 0;
        string? error = null;
        try
        {
            await PushDevicesAsync(http, ct);

            var app = new RepAppService(_db);
            for (var round = 0; round < MaxBatchesPerRun; round++)
            {
                var inbox = await SendAsync<List<AgentInboxItem>>(http, HttpMethod.Get, $"{CloudRoutes.AgentInbox}?max={Batch}", null, ct);
                if (inbox.Count == 0) break;
                var results = new List<AgentResult>();
                foreach (var item in inbox)
                {
                    var r = await ReceiveOneAsync(app, item);
                    received++;
                    switch (r.Status)
                    {
                        case RepRequestStatus.Posted: posted++; break;
                        case RepRequestStatus.Pending: pending++; break;
                        case RepRequestStatus.Failed: failed++; break;
                    }
                    results.Add(new AgentResult(item.ClientId, r.Accepted, r.Status.ToString(), r.Message, r.ResultId, r.Warning));
                }
                await SendAsync<JsonElement>(http, HttpMethod.Post, CloudRoutes.AgentResults, results, ct);
                if (inbox.Count < Batch) break;
            }

            // المعلّقة في الخادم: ما اعتُمد أو رُفض في المعمل يعود للهاتف
            var open = await SendAsync<List<Guid>>(http, HttpMethod.Get, CloudRoutes.AgentOpen, null, ct);
            if (open.Count > 0)
            {
                var now = await app.StatusesAsync(open);
                var changed = now.Where(kv => kv.Value.Status != RepRequestStatus.Pending)
                                 .Select(kv => new AgentResult(kv.Key, kv.Value.Accepted, kv.Value.Status.ToString(), kv.Value.Message, kv.Value.ResultId, kv.Value.Warning))
                                 .ToList();
                if (changed.Count > 0) await SendAsync<JsonElement>(http, HttpMethod.Post, CloudRoutes.AgentResults, changed, ct);
                updated = changed.Count;
            }

            var due = state.LastSnapshotCheckUtc is null || DateTime.UtcNow - state.LastSnapshotCheckUtc >= CloudSyncState.SnapshotEvery;
            if (received > 0 || updated > 0 || due) snapshots = await PushSnapshotsAsync(http, state, ct);
        }
        catch (CloudSyncException ex) { error = ex.Message; }

        await RecordAsync(machine, error, received, snapshots > 0);
        return new(error is null, received, posted, pending, failed, updated, snapshots, error);
    }

    private async Task<RepIntakeResult> ReceiveOneAsync(RepAppService app, AgentInboxItem item)
    {
        if (!Enum.TryParse<RepRequestKind>(item.Kind, out var kind))
            return new(false, RepRequestStatus.Failed, null, "نوع الحركة غير معروف — حدّث التطبيق");
        try
        {
            return await app.ReceiveRelayedAsync(item.DeviceId, new RepRequestEnvelope(item.ClientId, kind, item.OccurredAt, item.Payload, item.Photo));
        }
        catch (Exception)
        {
            // خطأ غير متوقع (أو خدمة مزامنة ثانية سبقت إلى الحركة نفسها): النتيجة المحفوظة إن وُجدت
            _db.ChangeTracker.Clear();
            var saved = await app.StatusesAsync(new[] { item.ClientId });
            return saved.TryGetValue(item.ClientId, out var r) && r.Status != RepRequestStatus.Failed
                ? r
                : new(false, RepRequestStatus.Failed, null, "تعذّر ترحيل الحركة في المعمل — أعد إرسالها أو راجع الإدارة");
        }
    }

    private async Task PushDevicesAsync(HttpClient http, CancellationToken ct)
    {
        var devices = (await _db.RepDevices.AsNoTracking().Select(d => new { d.Id, d.RepEmployeeId, d.DeviceKey, d.IsActive }).ToListAsync())
            .Select(d => new AgentDevice(d.Id, d.RepEmployeeId, CloudHash.Of(d.DeviceKey), d.IsActive)).ToList();
        await SendAsync<JsonElement>(http, HttpMethod.Put, CloudRoutes.AgentDevices, devices, ct);
    }

    private async Task<int> PushSnapshotsAsync(HttpClient http, CloudSyncState state, CancellationToken ct)
    {
        // البصمات من الخادم في كل فحص (طلب صغير): يبقى صحيحًا حتى لو أُعيد إنشاء قاعدة الخادم
        state.SnapshotHashes = await SendAsync<Dictionary<int, string>>(http, HttpMethod.Get, CloudRoutes.AgentSnapshotHashes, null, ct);
        var reps = await _db.RepDevices.AsNoTracking().Where(d => d.IsActive).Select(d => d.RepEmployeeId).Distinct().ToListAsync();
        var changed = new List<AgentSnapshot>();
        foreach (var repId in reps)
        {
            var snap = await BuildSnapshotAsync(repId);
            if (snap is null) continue;
            // البصمة بلا وقت التوليد: لا يُرفع شيء ما لم يتغير المحتوى
            var hash = CloudHash.Of(JsonSerializer.Serialize(snap with { GeneratedAtUtc = default }, Json));
            if (state.SnapshotHashes.TryGetValue(repId, out var known) && known == hash) continue;
            changed.Add(new AgentSnapshot(repId, hash, JsonSerializer.Serialize(snap, Json)));
        }
        if (changed.Count > 0)
        {
            await SendAsync<JsonElement>(http, HttpMethod.Put, CloudRoutes.AgentSnapshots, changed, ct);
            foreach (var c in changed) state.SnapshotHashes[c.RepEmployeeId] = c.Hash;
        }
        state.LastSnapshotCheckUtc = DateTime.UtcNow;
        return changed.Count;
    }

    private async Task RecordAsync(string machine, string? error, int received, bool snapshotPushed)
    {
        _db.ChangeTracker.Clear();
        var s = await _db.CloudSyncSettings.FirstOrDefaultAsync();
        if (s is null) return;
        var now = DateTime.UtcNow;
        s.LastAttemptAt = now;
        s.AgentMachine = machine.Length > 100 ? machine[..100] : machine;
        s.ReceivedCount += received;
        if (snapshotPushed) s.LastSnapshotAt = now;
        if (error is null) s.LastSuccessAt = now;
        else { s.LastError = error.Length > 500 ? error[..500] : error; s.LastErrorAt = now; }
        await _db.SaveChangesAsync();
    }

    // ============================ نسخة العمل ============================

    private class BalanceRow { public int CustomerId { get; set; } public decimal Balance { get; set; } }

    /// <summary>نسخة عمل المندوب (null إن لم يكن مندوبًا فعّالًا).</summary>
    public async Task<RepSnapshot?> BuildSnapshotAsync(int repEmployeeId)
    {
        var rep = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == repEmployeeId && e.IsSalesRep);
        if (rep is null) return null;
        var settings = await new RepAppService(_db).SettingsAsync();
        var wallet = await new RepsService(_db).GetWalletBalanceAsync(rep.Id);

        // زبائنه المسندون إليه، وإن لم يُسند له أحد بعد فكل الزبائن الفعّالين
        var assigned = await _db.RepCustomerAssignments.AsNoTracking().Where(a => a.EmployeeId == rep.Id).Select(a => a.CustomerId).ToListAsync();
        var customersQuery = _db.Customers.AsNoTracking().Where(c => c.IsActive);
        if (assigned.Count > 0) customersQuery = customersQuery.Where(c => assigned.Contains(c.Id));
        var customers = await customersQuery.OrderBy(c => c.Name).ToListAsync();
        var balances = (await _db.Database.SqlQueryRaw<BalanceRow>("SELECT CustomerId, Balance FROM vw_CustomerBalances").ToListAsync())
                       .ToDictionary(b => b.CustomerId, b => b.Balance);
        int? PriceAgent(Customer c) => c.CustomerType switch
        {
            CustomerType.Agent => c.Id,
            CustomerType.SubCustomer => c.ParentAgentId,
            _ => null
        };
        var snapCustomers = customers.Select(c => new SnapshotCustomer(c.Id, c.Name, c.Phone, c.Address, c.CustomerType.ToString(), PriceAgent(c),
                                                                       c.CreditLimit, balances.GetValueOrDefault(c.Id))).ToList();

        var items = await _db.Items.AsNoTracking().Where(i => i.IsActive && i.SourcingMethod != SourcingMethod.Purchased)
                                   .OrderBy(i => i.ItemName).ToListAsync();
        var itemIds = items.Select(i => i.Id).ToList();
        var levels = (await _db.ItemPackagingLevels.AsNoTracking().Where(l => itemIds.Contains(l.ItemId) && l.IsSellableUnit).ToListAsync())
                     .GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.EquivalentBaseUnits).ToList());
        var products = items.Where(i => levels.ContainsKey(i.Id))
            .Select(i => new SnapshotProduct(i.Id, i.ItemCode, i.ItemName, i.SalePrice,
                                             levels[i.Id].Select(l => new SnapshotLevel(l.Id, l.LevelName, l.EquivalentBaseUnits)).ToList()))
            .ToList();
        var productIds = products.Select(p => p.ItemId).ToList();

        var agentIds = snapCustomers.Where(c => c.PriceAgentId is not null).Select(c => c.PriceAgentId!.Value).Distinct().ToList();
        // أسعار الوكلاء للمنتج الأساسي (الطلب الخاص يسعّره المعمل عند الترحيل)
        var agentPrices = await _db.AgentItemPrices.AsNoTracking().Where(p => agentIds.Contains(p.CustomerId) && productIds.Contains(p.ItemId) && p.CustomRecipeId == null)
            .OrderBy(p => p.CustomerId).ThenBy(p => p.ItemId)
            .Select(p => new SnapshotAgentPrice(p.CustomerId, p.ItemId, p.AgentPrice)).ToListAsync();

        var van = await _db.Warehouses.AsNoTracking().FirstOrDefaultAsync(w => w.IsActive && w.WarehouseType == WarehouseType.RepVan && w.OwnerEmployeeId == rep.Id);
        var stock = van is null ? new List<SnapshotStock>()
            : (await _db.StockTransactions.AsNoTracking().Where(t => t.WarehouseId == van.Id)
                    .GroupBy(t => t.ItemId).Select(g => new { ItemId = g.Key, Pieces = g.Sum(t => t.QuantityBaseUnits) }).ToListAsync())
              .Where(x => x.Pieces != 0).OrderBy(x => x.ItemId).Select(x => new SnapshotStock(x.ItemId, x.Pieces)).ToList();

        var since = DateTime.UtcNow.AddDays(-7);
        var posted = await _db.RepRequests.AsNoTracking()
            .Where(r => r.RepEmployeeId == rep.Id && r.Status == RepRequestStatus.Posted && r.ReceivedAt >= since)
            .OrderBy(r => r.Id).Select(r => r.ClientId).ToListAsync();

        return new RepSnapshot(rep.Id, rep.FullName, DateTime.UtcNow, settings.AllowCreditOverLimit, settings.CashAlertDays, wallet,
                               products, snapCustomers, agentPrices, stock, posted);
    }

    // ============================ الاتصال ============================

    private static async Task<T> SendAsync<T>(HttpClient http, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body, options: Json);
            response = await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            throw new CloudSyncException($"تعذّر الاتصال بالخادم السحابي — تحقق من الإنترنت وعنوان الخادم ({ex.Message})");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string? message = null;
                try { message = (await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).GetProperty("error").GetString(); }
                catch (Exception) { /* رد بلا رسالة */ }
                throw new CloudSyncException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => message ?? "مفتاح المعمل غير مقبول في الخادم",
                    _ => $"الخادم السحابي رد بخطأ {(int)response.StatusCode}{(message is null ? "" : ": " + message)}"
                });
            }
            try
            {
                return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
            }
            catch (JsonException)
            {
                throw new CloudSyncException("رد الخادم السحابي غير مفهوم — تحقق من العنوان");
            }
        }
    }
}

public enum CloudSyncLevel { Off, Ok, Warning, Error }

/// <summary>وصف حالة المزامنة للمستخدم (الشاشة ومؤشر الشريط الجانبي).</summary>
public static class CloudSyncStatus
{
    public static (CloudSyncLevel level, string text) Describe(CloudSyncSetting s, DateTime nowUtc)
    {
        if (!s.IsEnabled) return (CloudSyncLevel.Off, "المزامنة السحابية غير مفعّلة");
        if (s.LastErrorAt is DateTime errAt && (s.LastSuccessAt is null || errAt > s.LastSuccessAt))
            return (CloudSyncLevel.Error, $"متعذّرة منذ {Ago(nowUtc - errAt)}: {s.LastError}");
        if (s.LastSuccessAt is not DateTime ok)
            return (CloudSyncLevel.Warning, "لم تتم أي مزامنة بعد — تأكد من تثبيت خدمة المزامنة على جهاز السيرفر");
        var stale = TimeSpan.FromSeconds(Math.Max(120, s.IntervalSeconds * 3));
        return nowUtc - ok > stale
            ? (CloudSyncLevel.Warning, $"آخر مزامنة قبل {Ago(nowUtc - ok)} — هل خدمة المزامنة تعمل على {s.AgentMachine ?? "جهاز السيرفر"}؟")
            : (CloudSyncLevel.Ok, $"متصلة — آخر مزامنة قبل {Ago(nowUtc - ok)}");
    }

    public static string Ago(TimeSpan t) => t.TotalSeconds < 60 ? "أقل من دقيقة"
        : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} دقيقة"
        : t.TotalHours < 48 ? $"{(int)t.TotalHours} ساعة"
        : $"{(int)t.TotalDays} يوم";
}
