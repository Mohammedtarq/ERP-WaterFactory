using System.Security.Cryptography;
using System.Text;
using ERP.Cloud.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ERP.Cloud.Api;

/// <summary>
/// واجهة الخادم السحابي. الهاتف يعرّف نفسه بمفتاح جهازه (يُقارن ببصمته)، وخدمة المزامنة بمفتاح المعمل.
/// الخادم لا يرحّل شيئًا: يحفظ الحركة حتى تسحبها خدمة المزامنة وترحّلها في قاعدة المعمل، ثم يعيد نتيجتها للهاتف.
/// </summary>
public static class RelayEndpoints
{
    public const string Version = "1.0";

    public static string Hash(string value) => CloudHash.Of(value);

    private static IResult Problem(int status, string message) => Results.Json(new { error = message }, statusCode: status);

    public static void Map(WebApplication app)
    {
        app.MapGet(CloudRoutes.Health, async (RelayDb db) =>
            await db.Database.CanConnectAsync() ? Results.Ok(new { ok = true, version = Version }) : Problem(503, "قاعدة الخادم غير متاحة"));

        // ============================ الهاتف ============================
        var rep = app.MapGroup("").AddEndpointFilter(async (ctx, next) =>
        {
            var key = ctx.HttpContext.Request.Headers[CloudRoutes.DeviceKeyHeader].ToString();
            if (key.Length is < 32 or > 128) return Problem(401, "الجهاز غير مسجّل — راجع الإدارة");
            var db = ctx.HttpContext.RequestServices.GetRequiredService<RelayDb>();
            var hash = Hash(key);
            var device = await db.Devices.FirstOrDefaultAsync(d => d.KeyHash == hash);
            if (device is null || !device.IsActive) return Problem(401, "الجهاز غير مسجّل أو موقوف — راجع الإدارة");
            device.LastSeenAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            ctx.HttpContext.Items[nameof(RelayDevice)] = device;
            return await next(ctx);
        });

        rep.MapPost(CloudRoutes.RepRequests, async (HttpContext http, RelayDb db, PhoneRequest req) =>
        {
            var device = (RelayDevice)http.Items[nameof(RelayDevice)]!;
            if (req.ClientId == Guid.Empty) return Problem(400, "الحركة بلا رقم فريد");
            if (!RepKinds.All.Contains(req.Kind ?? "")) return Problem(400, "نوع الحركة غير معروف");
            if (string.IsNullOrWhiteSpace(req.Payload) || req.Payload.Length > RelayLimits.MaxPayloadChars) return Problem(400, "محتوى الحركة فارغ أو كبير جدًا");
            if (req.Photo is { Length: > RelayLimits.MaxPhotoBytes }) return Problem(400, "الصورة كبيرة جدًا — صوّرها بدقة أقل");

            var now = DateTime.UtcNow;
            var existing = await db.Requests.FirstOrDefaultAsync(r => r.ClientId == req.ClientId);
            if (existing is not null && existing.DeviceId != device.Id) return Problem(409, "رقم الحركة مستخدم من جهاز آخر");
            if (existing is not null && !(existing.State == RelayState.Done && existing.ResultStatus == "Failed")) return Results.Ok(StatusOf(existing));

            if (existing is null)
            {
                existing = new RelayRequest { ClientId = req.ClientId, DeviceId = device.Id, ReceivedAt = now };
                db.Requests.Add(existing);
            }
            // حركة جديدة، أو إعادة محاولة لحركة تعذّر ترحيلها (بعد تصحيحها في الهاتف)
            existing.RepEmployeeId = device.RepEmployeeId;
            (existing.Kind, existing.OccurredAt, existing.Payload, existing.Photo) = (req.Kind!, req.OccurredAt, req.Payload, req.Photo);
            (existing.State, existing.Accepted, existing.ResultStatus, existing.ResultMessage, existing.ResultId, existing.Warning, existing.UpdatedAt)
                = (RelayState.Waiting, null, null, null, null, null, now);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // إرسالان متزامنان للحركة نفسها: الأول يكفي
                db.ChangeTracker.Clear();
                existing = await db.Requests.AsNoTracking().FirstAsync(r => r.ClientId == req.ClientId);
            }
            return Results.Ok(StatusOf(existing));
        });

        rep.MapPost(CloudRoutes.RepStatuses, async (HttpContext http, RelayDb db, PhoneStatusQuery q) =>
        {
            var device = (RelayDevice)http.Items[nameof(RelayDevice)]!;
            var ids = (q.ClientIds ?? new()).Distinct().Take(RelayLimits.MaxStatusQuery).ToList();
            var rows = await db.Requests.AsNoTracking().Where(r => r.DeviceId == device.Id && ids.Contains(r.ClientId)).ToListAsync();
            return Results.Ok(rows.Select(StatusOf).ToList());
        });

        rep.MapGet(CloudRoutes.RepSnapshot, async (HttpContext http, RelayDb db) =>
        {
            var device = (RelayDevice)http.Items[nameof(RelayDevice)]!;
            var snap = await db.Snapshots.AsNoTracking().FirstOrDefaultAsync(s => s.RepEmployeeId == device.RepEmployeeId);
            if (snap is null) return Problem(404, "لم تصل نسخة العمل من المعمل بعد — حاول بعد دقيقة");
            var etag = $"\"{snap.Hash}\"";
            if (http.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(304);
            http.Response.Headers.ETag = etag;
            return Results.Text(snap.Json, "application/json; charset=utf-8");
        });

        rep.MapGet(CloudRoutes.RepPing, async (HttpContext http, RelayDb db) =>
        {
            var device = (RelayDevice)http.Items[nameof(RelayDevice)]!;
            var agent = await db.AgentState.AsNoTracking().FirstOrDefaultAsync();
            var snap = await db.Snapshots.AsNoTracking().Where(s => s.RepEmployeeId == device.RepEmployeeId).Select(s => (DateTime?)s.UpdatedAt).FirstOrDefaultAsync();
            return Results.Ok(new PhonePing(DateTime.UtcNow, agent?.LastSeenAt, snap));
        });

        // ============================ خدمة المزامنة ============================
        var agentKey = app.Configuration["Relay:AgentKey"] ?? "";
        var keepDays = Math.Max(1, app.Configuration.GetValue("Relay:KeepDays", 30));
        var agent = app.MapGroup("").AddEndpointFilter(async (ctx, next) =>
        {
            if (agentKey.Length < 32) return Problem(503, "مفتاح المعمل غير مضبوط في إعدادات الخادم (Relay:AgentKey)");
            var given = ctx.HttpContext.Request.Headers[CloudRoutes.AgentKeyHeader].ToString();
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(agentKey))))
                return Problem(401, "مفتاح المعمل غير صحيح");
            var db = ctx.HttpContext.RequestServices.GetRequiredService<RelayDb>();
            var state = await db.AgentState.FirstOrDefaultAsync();
            if (state is null) db.AgentState.Add(state = new RelayAgentState());
            state.LastSeenAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return await next(ctx);
        });

        agent.MapGet(CloudRoutes.AgentPing, async (RelayDb db) =>
            Results.Ok(new AgentPing(DateTime.UtcNow, await db.Requests.CountAsync(r => r.State == RelayState.Waiting), Version)));

        // القائمة كاملة في كل مرة: ما ليس فيها يُوقف
        agent.MapPut(CloudRoutes.AgentDevices, async (RelayDb db, List<AgentDevice> devices) =>
        {
            var now = DateTime.UtcNow;
            if (devices.Any(d => d.KeyHash is not { Length: 64 } || !d.KeyHash.All(Uri.IsHexDigit))) return Problem(400, "بصمة مفتاح غير صحيحة");
            var byId = devices.GroupBy(d => d.DeviceId).ToDictionary(g => g.Key, g => g.Last() with { KeyHash = g.Last().KeyHash.ToUpperInvariant() });
            var hashes = byId.Values.Select(d => d.KeyHash).ToList();
            // بصمة انتقلت لجهاز آخر (لا يحدث عادة): تُفرَّغ أولًا كي لا يتعارض الفهرس الفريد
            foreach (var stale in await db.Devices.Where(d => hashes.Contains(d.KeyHash)).ToListAsync())
                if (!byId.TryGetValue(stale.Id, out var d) || d.KeyHash != stale.KeyHash) { stale.KeyHash = $"X{stale.Id}"; stale.IsActive = false; }
            await db.SaveChangesAsync();
            var existing = await db.Devices.ToDictionaryAsync(d => d.Id);
            foreach (var d in byId.Values)
            {
                if (!existing.TryGetValue(d.DeviceId, out var row)) db.Devices.Add(row = new RelayDevice { Id = d.DeviceId });
                (row.RepEmployeeId, row.KeyHash, row.IsActive, row.UpdatedAt) = (d.RepEmployeeId, d.KeyHash, d.IsActive, now);
            }
            foreach (var row in existing.Values.Where(r => !byId.ContainsKey(r.Id) && r.IsActive)) (row.IsActive, row.UpdatedAt) = (false, now);
            await db.SaveChangesAsync();
            return Results.Ok(new { count = byId.Count });
        });

        agent.MapGet(CloudRoutes.AgentInbox, async (RelayDb db, int? max) =>
        {
            var take = Math.Clamp(max ?? 20, 1, RelayLimits.MaxInboxBatch);
            var items = await db.Requests.AsNoTracking().Where(r => r.State == RelayState.Waiting).OrderBy(r => r.Id).Take(take)
                .Select(r => new AgentInboxItem(r.ClientId, r.DeviceId, r.Kind, r.OccurredAt, r.Payload, r.Photo)).ToListAsync();
            return Results.Ok(items);
        });

        agent.MapPost(CloudRoutes.AgentResults, async (RelayDb db, List<AgentResult> results) =>
        {
            var now = DateTime.UtcNow;
            var ids = results.Select(r => r.ClientId).Distinct().ToList();
            var rows = await db.Requests.Where(r => ids.Contains(r.ClientId)).ToDictionaryAsync(r => r.ClientId);
            foreach (var r in results)
            {
                if (!rows.TryGetValue(r.ClientId, out var row)) continue;
                (row.State, row.Accepted, row.ResultStatus, row.ResultId, row.UpdatedAt) = (RelayState.Done, r.Accepted, r.Status, r.ResultId, now);
                row.ResultMessage = r.Message.Length > 500 ? r.Message[..500] : r.Message;
                row.Warning = r.Warning is { Length: > 300 } w ? w[..300] : r.Warning;
                row.Photo = null;   // الصورة وصلت المعمل: لا داعي لبقائها في السحابة
            }
            await db.SaveChangesAsync();
            // تنظيف: المنتهية قديمًا (المعلّقة تبقى حتى يُبت فيها)
            var cutoff = now.AddDays(-keepDays);
            await db.Requests.Where(r => r.State == RelayState.Done && r.ResultStatus != "Pending" && r.UpdatedAt < cutoff).ExecuteDeleteAsync();
            return Results.Ok(new { count = rows.Count });
        });

        agent.MapGet(CloudRoutes.AgentOpen, async (RelayDb db) =>
            Results.Ok(await db.Requests.AsNoTracking().Where(r => r.State == RelayState.Done && r.ResultStatus == "Pending").Select(r => r.ClientId).ToListAsync()));

        agent.MapGet(CloudRoutes.AgentSnapshotHashes, async (RelayDb db) =>
            Results.Ok(await db.Snapshots.AsNoTracking().ToDictionaryAsync(s => s.RepEmployeeId, s => s.Hash)));

        agent.MapPut(CloudRoutes.AgentSnapshots, async (RelayDb db, List<AgentSnapshot> snaps) =>
        {
            var now = DateTime.UtcNow;
            var ids = snaps.Select(s => s.RepEmployeeId).ToList();
            var rows = await db.Snapshots.Where(s => ids.Contains(s.RepEmployeeId)).ToDictionaryAsync(s => s.RepEmployeeId);
            foreach (var s in snaps)
            {
                if (!rows.TryGetValue(s.RepEmployeeId, out var row)) { db.Snapshots.Add(row = new RelaySnapshot { RepEmployeeId = s.RepEmployeeId }); rows[s.RepEmployeeId] = row; }
                (row.Hash, row.Json, row.UpdatedAt) = (s.Hash, s.Json, now);
            }
            await db.SaveChangesAsync();
            return Results.Ok(new { count = snaps.Count });
        });
    }

    private static PhoneRequestStatus StatusOf(RelayRequest r) =>
        r.State == RelayState.Waiting
            ? new(r.ClientId, r.State, null, "وصلت الخادم — بانتظار المعمل")
            : new(r.ClientId, r.State, r.ResultStatus, r.ResultMessage ?? "", r.ResultId, r.Warning);
}
