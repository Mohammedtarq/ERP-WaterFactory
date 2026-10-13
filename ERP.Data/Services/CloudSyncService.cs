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
public record CloudSyncReport(bool Ok, int Received, int Posted, int Pending, int Failed, int Updated, int Snapshots, string? Error,
                              IReadOnlyDictionary<SyncTargetKind, string?>? Targets = null)
{
    public string Text => Ok
        ? $"تمت المزامنة: وصلت {Received} حركة (رُحّل {Posted}، بانتظار الاعتماد {Pending}، تعذّر {Failed})، وتحدّثت {Updated} حالة، و{Snapshots} نسخة عمل"
        : Error ?? "تعذّرت المزامنة";
}

/// <summary>
/// ذاكرة خدمة المزامنة بين الدورات: آخر فحص لنسخ العمل (كل دقيقة، أو فورًا بعد حركات جديدة)،
/// ونتائج تنتظر نسخها إلى خادم كان منقطعًا (كي يجد الهاتف نتيجته من أي خادم يصله).
/// </summary>
public class CloudSyncState
{
    public DateTime? LastSnapshotCheckUtc { get; set; }
    public static readonly TimeSpan SnapshotEvery = TimeSpan.FromSeconds(60);
    internal Dictionary<string, List<AgentResult>> PendingMirrors { get; } = new();
}

/// <summary>خادم وسيط تتصل به خدمة المزامنة: المحلي (واي فاي المعمل) أو السحابي (الإنترنت).</summary>
public record SyncTarget(SyncTargetKind Kind, HttpClient Http)
{
    public string Label => Kind == SyncTargetKind.Local ? "الخادم المحلي" : "الخادم السحابي";
}

public enum SyncTargetKind { Local, Cloud }

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
    private const int MaxPendingMirrors = 2000;

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

    /// <summary>
    /// حفظ الإعداد. cloudUrl: الخادم السحابي (اختياري)، localEnabled: الخادم المحلي على حاسبة السيرفر (null = كما هو).
    /// مفتاح المعمل يُولَّد تلقائيًا عند أول تفعيل: الخادم المحلي يأخذه من خدمة المزامنة، والسحابي من ملفات النشر.
    /// </summary>
    public async Task<FinanceOperationResult> SaveSettingsAsync(string? cloudUrl, bool enabled, int intervalSeconds, int userId, bool? localEnabled = null)
    {
        if (intervalSeconds is < 5 or > 600) return FinanceOperationResult.Fail("فترة المزامنة بين 5 و600 ثانية");
        var url = string.IsNullOrWhiteSpace(cloudUrl) ? null : cloudUrl.Trim().TrimEnd('/');
        if (url is not null && ValidateUrl(url) is string bad) return FinanceOperationResult.Fail(bad);
        var s = await _db.CloudSyncSettings.FirstOrDefaultAsync();
        if (s is null) _db.CloudSyncSettings.Add(s = new CloudSyncSetting());
        var local = localEnabled ?? s.LocalEnabled;
        if (enabled && url is null && !local) return FinanceOperationResult.Fail("فعّل الخادم المحلي أو اكتب عنوان الخادم السحابي");
        var newKey = enabled && string.IsNullOrEmpty(s.AgentKey);
        if (newKey) s.AgentKey = NewKey();
        (s.ServerUrl, s.IsEnabled, s.IntervalSeconds, s.LocalEnabled) = (url, enabled, intervalSeconds, local);
        if (!local) s.LocalUrl = null;
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Edit", "CloudSyncSettings", 1,
            $"المزامنة: {(enabled ? "مفعّلة" : "موقوفة")} — المحلي {(local ? "يعمل" : "موقوف")} — السحابي {url ?? "بلا عنوان"} — كل {intervalSeconds} ثانية"
            + (newKey ? " — وُلّد مفتاح المعمل تلقائيًا" : ""));
        return FinanceOperationResult.Ok();
    }

    private static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    /// <returns>
    /// مفتاح المعمل الجديد (القديم يتوقف فورًا): الخادم المحلي يأخذه تلقائيًا من خدمة المزامنة،
    /// والسحابي يحتاج ملفات نشر جديدة (أو Relay__AgentKey).
    /// </returns>
    public async Task<string> GenerateKeyAsync(int userId)
    {
        var s = await _db.CloudSyncSettings.FirstOrDefaultAsync();
        if (s is null) _db.CloudSyncSettings.Add(s = new CloudSyncSetting());
        s.AgentKey = NewKey();
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

    /// <summary>دورة مع خادم واحد (السحابي) — «مزامنة الآن» القديمة والاختبارات.</summary>
    public Task<CloudSyncReport> RunOnceAsync(HttpClient http, string machine, CloudSyncState? state = null, CancellationToken ct = default) =>
        RunOnceAsync(new[] { new SyncTarget(SyncTargetKind.Cloud, http) }, machine, state, ct);

    /// <summary>
    /// دورة مع الخادمين معًا: لكل خادم يُرفع الأجهزة، وتُسحب حركاته وتُرحّل، وتُحدّث معلّقاته، وتُرفع نسخ العمل إن تغيّرت.
    /// نتيجة كل حركة تُنسخ إلى الخادم الآخر أيضًا، فيعرفها الهاتف أينما اتصل (واي فاي المعمل أو الإنترنت).
    /// عنوانان يصلان الخادم نفسه (نفق إلى الحاسبة) يُعاملان خادمًا واحدًا. انقطاع أحدهما لا يوقف الآخر.
    /// </summary>
    public async Task<CloudSyncReport> RunOnceAsync(IReadOnlyList<SyncTarget> targets, string machine, CloudSyncState? state = null, CancellationToken ct = default)
    {
        state ??= new CloudSyncState();
        int received = 0, posted = 0, pending = 0, failed = 0, updated = 0, snapshots = 0;
        var errors = new Dictionary<SyncTargetKind, string?>();
        var live = new List<SyncTarget>();
        var relayIds = new HashSet<Guid>();
        var duplicates = new HashSet<SyncTargetKind>();
        foreach (var t in targets)
        {
            try
            {
                var ping = await SendAsync<AgentPing>(t.Http, HttpMethod.Get, CloudRoutes.AgentPing, null, ct);
                if (ping.RelayId != Guid.Empty && !relayIds.Add(ping.RelayId)) { errors[t.Kind] = null; duplicates.Add(t.Kind); continue; }   // الخادم نفسه بعنوان ثانٍ
                live.Add(t);
            }
            catch (CloudSyncException ex) { errors[t.Kind] = ex.Message; }
        }

        var due = state.LastSnapshotCheckUtc is null || DateTime.UtcNow - state.LastSnapshotCheckUtc >= CloudSyncState.SnapshotEvery;
        var snapshotsByTarget = new Dictionary<SyncTargetKind, List<AgentSnapshot>?>();
        var app = new RepAppService(_db);
        foreach (var t in live)
        {
            try
            {
                // نتائج تأخر نسخها إلى هذا الخادم (كان منقطعًا)
                if (state.PendingMirrors.Remove(t.Kind.ToString(), out var late) && late.Count > 0)
                    await SendAsync<JsonElement>(t.Http, HttpMethod.Post, CloudRoutes.AgentResults, late, ct);

                await PushDevicesAsync(t.Http, ct);
                for (var round = 0; round < MaxBatchesPerRun; round++)
                {
                    var inbox = await SendAsync<List<AgentInboxItem>>(t.Http, HttpMethod.Get, $"{CloudRoutes.AgentInbox}?max={Batch}", null, ct);
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
                        results.Add(new AgentResult(item.ClientId, r.Accepted, r.Status.ToString(), r.Message, r.ResultId, r.Warning, item.DeviceId));
                    }
                    await SendAsync<JsonElement>(t.Http, HttpMethod.Post, CloudRoutes.AgentResults, results, ct);
                    foreach (var other in targets.Where(o => o.Kind != t.Kind && !duplicates.Contains(o.Kind)))
                    {
                        if (!state.PendingMirrors.TryGetValue(other.Kind.ToString(), out var list)) state.PendingMirrors[other.Kind.ToString()] = list = new();
                        list.AddRange(results);
                        if (list.Count > MaxPendingMirrors) list.RemoveRange(0, list.Count - MaxPendingMirrors);
                    }
                    if (inbox.Count < Batch) break;
                }

                // المعلّقة في الخادم: ما اعتُمد أو رُفض في المعمل يعود للهاتف
                var open = await SendAsync<List<Guid>>(t.Http, HttpMethod.Get, CloudRoutes.AgentOpen, null, ct);
                if (open.Count > 0)
                {
                    var now = await app.StatusesAsync(open);
                    var changed = now.Where(kv => kv.Value.Status != RepRequestStatus.Pending)
                                     .Select(kv => new AgentResult(kv.Key, kv.Value.Accepted, kv.Value.Status.ToString(), kv.Value.Message, kv.Value.ResultId, kv.Value.Warning))
                                     .ToList();
                    if (changed.Count > 0) await SendAsync<JsonElement>(t.Http, HttpMethod.Post, CloudRoutes.AgentResults, changed, ct);
                    updated += changed.Count;
                }
                errors[t.Kind] = null;
            }
            catch (CloudSyncException ex) { errors[t.Kind] = ex.Message; }
        }

        // نسخ النتائج إلى الخادم الآخر الآن (وما تعذّر يبقى للدورة التالية)
        foreach (var t in live.Where(t => errors.GetValueOrDefault(t.Kind) is null))
        {
            if (!state.PendingMirrors.Remove(t.Kind.ToString(), out var mirror) || mirror.Count == 0) continue;
            try { await SendAsync<JsonElement>(t.Http, HttpMethod.Post, CloudRoutes.AgentResults, mirror, ct); }
            catch (CloudSyncException ex) { state.PendingMirrors[t.Kind.ToString()] = mirror; errors[t.Kind] = ex.Message; }
        }
        // لا تُنسخ إلى خادم ليس ضمن هذه الدورة أصلًا
        foreach (var key in state.PendingMirrors.Keys.Where(k => targets.All(t => t.Kind.ToString() != k) || duplicates.Any(d => d.ToString() == k)).ToList())
            state.PendingMirrors.Remove(key);

        if (received > 0 || updated > 0 || due)
        {
            List<AgentSnapshot>? built = null;
            foreach (var t in live.Where(t => errors.GetValueOrDefault(t.Kind) is null))
            {
                try
                {
                    built ??= await BuildAllSnapshotsAsync();
                    snapshots += await PushSnapshotsAsync(t.Http, built, ct);
                }
                catch (CloudSyncException ex) { errors[t.Kind] = ex.Message; }
            }
            state.LastSnapshotCheckUtc = DateTime.UtcNow;
        }

        var failedTargets = errors.Where(e => e.Value is not null).ToList();
        string? error = failedTargets.Count == 0 ? null
            : targets.Count == 1 ? failedTargets[0].Value
            : string.Join(" — ", failedTargets.Select(e => $"{(e.Key == SyncTargetKind.Local ? "الخادم المحلي" : "الخادم السحابي")}: {e.Value}"));
        await RecordAsync(machine, errors, received, snapshots > 0);
        return new(error is null, received, posted, pending, failed, updated, snapshots, error, errors);
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

    /// <summary>نسخ عمل كل المندوبين الفعّالين مرة واحدة لكل دورة (تُرفع لكل خادم ما تغيّر عنده فقط).</summary>
    private async Task<List<AgentSnapshot>> BuildAllSnapshotsAsync()
    {
        var reps = await _db.RepDevices.AsNoTracking().Where(d => d.IsActive).Select(d => d.RepEmployeeId).Distinct().ToListAsync();
        var all = new List<AgentSnapshot>();
        foreach (var repId in reps)
        {
            var snap = await BuildSnapshotAsync(repId);
            if (snap is null) continue;
            // البصمة بلا وقت التوليد: لا يُرفع شيء ما لم يتغير المحتوى
            var hash = CloudHash.Of(JsonSerializer.Serialize(snap with { GeneratedAtUtc = default }, Json));
            all.Add(new AgentSnapshot(repId, hash, JsonSerializer.Serialize(snap, Json)));
        }
        return all;
    }

    private static async Task<int> PushSnapshotsAsync(HttpClient http, List<AgentSnapshot> all, CancellationToken ct)
    {
        // البصمات من الخادم في كل فحص (طلب صغير): يبقى صحيحًا حتى لو أُعيد إنشاء قاعدة الخادم
        var known = await SendAsync<Dictionary<int, string>>(http, HttpMethod.Get, CloudRoutes.AgentSnapshotHashes, null, ct);
        var changed = all.Where(s => !(known.TryGetValue(s.RepEmployeeId, out var h) && h == s.Hash)).ToList();
        if (changed.Count > 0) await SendAsync<JsonElement>(http, HttpMethod.Put, CloudRoutes.AgentSnapshots, changed, ct);
        return changed.Count;
    }

    private async Task RecordAsync(string machine, IReadOnlyDictionary<SyncTargetKind, string?> errors, int received, bool snapshotPushed)
    {
        _db.ChangeTracker.Clear();
        var s = await _db.CloudSyncSettings.FirstOrDefaultAsync();
        if (s is null) return;
        var now = DateTime.UtcNow;
        s.LastAttemptAt = now;
        s.AgentMachine = machine.Length > 100 ? machine[..100] : machine;
        s.ReceivedCount += received;
        if (snapshotPushed) s.LastSnapshotAt = now;
        static string Cut(string e) => e.Length > 500 ? e[..500] : e;
        foreach (var (kind, error) in errors)
        {
            if (kind == SyncTargetKind.Local)
            {
                if (error is null) s.LocalLastSuccessAt = now;
                else (s.LocalLastError, s.LocalLastErrorAt) = (Cut(error), now);
            }
            else
            {
                if (error is null) s.LastSuccessAt = now;
                else (s.LastError, s.LastErrorAt) = (Cut(error), now);
            }
        }
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
        // الاسم الموحد في الهاتف: الحجم واسم الأساسي والمتغيرات (الطلبات الخاصة بأسمائها المختصرة وأسعارها العامة)
        var baseVariant = (await new CompanyProfileService(_db).GetAsync())?.BaseVariantName;
        var variants = (await _db.CustomRecipes.AsNoTracking().Where(r => r.IsActive && itemIds.Contains(r.FinishedItemId))
                .Select(r => new { r.Id, r.FinishedItemId, r.ShortName, Customer = r.Customer != null ? r.Customer.Name : null, r.Name, r.SalePrice }).ToListAsync())
            .GroupBy(r => r.FinishedItemId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Id)
                .Select(r => new SnapshotVariant(r.Id, ProductName.Variant(r.ShortName, r.Customer, r.Name, false, null), r.SalePrice)).ToList());
        var products = items.Where(i => levels.ContainsKey(i.Id))
            .Select(i => new SnapshotProduct(i.Id, i.ItemCode, i.ItemName, i.SalePrice,
                                             levels[i.Id].Select(l => new SnapshotLevel(l.Id, l.LevelName, l.EquivalentBaseUnits)).ToList(),
                                             i.SizeMl, ProductName.Variant(null, null, null, true, baseVariant), variants.GetValueOrDefault(i.Id)))
            .ToList();
        var productIds = products.Select(p => p.ItemId).ToList();

        var agentIds = snapCustomers.Where(c => c.PriceAgentId is not null).Select(c => c.PriceAgentId!.Value).Distinct().ToList();
        // أسعار الوكلاء للمنتج الأساسي (الطلب الخاص يسعّره المعمل عند الترحيل)
        var agentPrices = await _db.AgentItemPrices.AsNoTracking().Where(p => agentIds.Contains(p.CustomerId) && productIds.Contains(p.ItemId))
            .OrderBy(p => p.CustomerId).ThenBy(p => p.ItemId).ThenBy(p => p.CustomRecipeId)
            .Select(p => new SnapshotAgentPrice(p.CustomerId, p.ItemId, p.AgentPrice, p.CustomRecipeId)).ToListAsync();

        var van = await _db.Warehouses.AsNoTracking().FirstOrDefaultAsync(w => w.IsActive && w.WarehouseType == WarehouseType.RepVan && w.OwnerEmployeeId == rep.Id);
        var stock = van is null ? new List<SnapshotStock>()
            : (await _db.StockTransactions.AsNoTracking().Where(t => t.WarehouseId == van.Id)
                    .GroupBy(t => new { t.ItemId, RecipeId = t.Batch != null ? t.Batch.CustomRecipeId : null })
                    .Select(g => new { g.Key.ItemId, g.Key.RecipeId, Pieces = g.Sum(t => t.QuantityBaseUnits) }).ToListAsync())
              .Where(x => x.Pieces != 0).OrderBy(x => x.ItemId).ThenBy(x => x.RecipeId)
              .Select(x => new SnapshotStock(x.ItemId, x.Pieces, x.RecipeId)).ToList();

        var since = DateTime.UtcNow.AddDays(-7);
        var posted = await _db.RepRequests.AsNoTracking()
            .Where(r => r.RepEmployeeId == rep.Id && r.Status == RepRequestStatus.Posted && r.ReceivedAt >= since)
            .OrderBy(r => r.Id).Select(r => r.ClientId).ToListAsync();

        var sync = await SettingsAsync();
        var servers = sync.IsEnabled
            ? new SnapshotServers(sync.LocalEnabled ? sync.LocalUrl : null, sync.ServerUrl,
                                  string.IsNullOrEmpty(sync.AgentKey) ? null : FactoryIdentity.Of(sync.AgentKey))
            : null;
        var company = await new CompanyProfileService(_db).GetAsync();
        var logo = company?.Logo is { Length: > 0 and <= RepSnapshot.MaxLogoBytes } l ? l : null;
        return new RepSnapshot(rep.Id, rep.FullName, DateTime.UtcNow, settings.AllowCreditOverLimit, settings.CashAlertDays, wallet,
                               products, snapCustomers, agentPrices, stock, posted,
                               string.IsNullOrWhiteSpace(company?.NameAr) ? null : company.NameAr.Trim(), logo, servers);
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
            throw new CloudSyncException($"تعذّر الاتصال بالخادم — تحقق من الشبكة وعنوان الخادم ({ex.Message})");
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
                    _ => $"الخادم رد بخطأ {(int)response.StatusCode}{(message is null ? "" : ": " + message)}"
                });
            }
            try
            {
                return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
            }
            catch (JsonException)
            {
                throw new CloudSyncException("رد الخادم غير مفهوم — تحقق من العنوان");
            }
        }
    }
}

public enum CloudSyncLevel { Off, Ok, Warning, Error }

/// <summary>وصف حالة المزامنة للمستخدم (الشاشة ومؤشر الشريط الجانبي).</summary>
public static class CloudSyncStatus
{
    /// <summary>الحالة العامة (المؤشر في الشريط): الأسوأ بين الخادمين المفعّلين، مع وصف كل منهما.</summary>
    public static (CloudSyncLevel level, string text) Describe(CloudSyncSetting s, DateTime nowUtc)
    {
        if (!s.IsEnabled) return (CloudSyncLevel.Off, "المزامنة غير مفعّلة");
        var parts = new List<(CloudSyncLevel level, string text)>();
        if (s.LocalEnabled) parts.Add(DescribeLocal(s, nowUtc));
        if (!string.IsNullOrEmpty(s.ServerUrl)) parts.Add(DescribeCloud(s, nowUtc));
        if (parts.Count == 0) return (CloudSyncLevel.Warning, "لا خادم مفعّل — فعّل الخادم المحلي أو اكتب عنوان الخادم السحابي");
        if (parts.Count == 1) return parts[0];
        var level = parts.Max(p => p.level);
        return (level, $"المحلي: {parts[0].text} — السحابي: {parts[1].text}");
    }

    public static (CloudSyncLevel level, string text) DescribeLocal(CloudSyncSetting s, DateTime nowUtc) =>
        !s.LocalEnabled ? (CloudSyncLevel.Off, "غير مفعّل")
        : One(s.LocalLastSuccessAt, s.LocalLastError, s.LocalLastErrorAt, s, nowUtc,
              "لم يتصل بعد — يعمل تلقائيًا إن ثُبّت النظام على هذه الحاسبة بوضع «السيرفر»");

    public static (CloudSyncLevel level, string text) DescribeCloud(CloudSyncSetting s, DateTime nowUtc) =>
        string.IsNullOrEmpty(s.ServerUrl) ? (CloudSyncLevel.Off, "غير مضبوط")
        : One(s.LastSuccessAt, s.LastError, s.LastErrorAt, s, nowUtc,
              "لم يتصل بعد — تأكد من نشر الخادم السحابي بملفات النشر الجاهزة");

    private static (CloudSyncLevel, string) One(DateTime? success, string? error, DateTime? errorAt, CloudSyncSetting s, DateTime nowUtc, string never)
    {
        if (!s.IsEnabled) return (CloudSyncLevel.Off, "المزامنة موقوفة");
        if (errorAt is DateTime errAt && (success is null || errAt > success))
            return (CloudSyncLevel.Error, $"متعذّر منذ {Ago(nowUtc - errAt)}: {error}");
        if (success is not DateTime ok) return (CloudSyncLevel.Warning, never);
        var stale = TimeSpan.FromSeconds(Math.Max(120, s.IntervalSeconds * 3));
        return nowUtc - ok > stale
            ? (CloudSyncLevel.Warning, $"آخر مزامنة قبل {Ago(nowUtc - ok)} — هل خدمة المزامنة تعمل على {s.AgentMachine ?? "جهاز السيرفر"}؟")
            : (CloudSyncLevel.Ok, $"متصل — آخر مزامنة قبل {Ago(nowUtc - ok)}");
    }

    public static string Ago(TimeSpan t) => t.TotalSeconds < 60 ? "أقل من دقيقة"
        : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} دقيقة"
        : t.TotalHours < 48 ? $"{(int)t.TotalHours} ساعة"
        : $"{(int)t.TotalDays} يوم";
}
