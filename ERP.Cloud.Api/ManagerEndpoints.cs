using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ERP.Cloud.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ERP.Cloud.Api;

/// <summary>
/// لوحة المدير على الإنترنت: صفحة PWA وواجهة قراءة فقط. لا نقطة كتابة للمدير أصلًا (لا POST غير الدخول والخروج).
/// الدخول باسم وكلمة مرور (مجزّأة) يرسلها المعمل؛ الرمز عشوائي، يُحفظ بصمته فقط، وإيقاف الحساب يُبطل رموزه.
/// محاولات الدخول الخاطئة تُقفل مؤقتًا (لكل عنوان + اسم).
/// </summary>
public static class ManagerEndpoints
{
    public static readonly TimeSpan SessionLife = TimeSpan.FromDays(30);
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);
    private const int MaxSummaryChars = 512 * 1024;

    private static readonly ConcurrentDictionary<string, (int Fails, DateTime FirstUtc)> Failures = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>للاختبارات: مسح أقفال الدخول (الذاكرة مشتركة بين اختبارات العملية الواحدة).</summary>
    internal static void ResetLocks() => Failures.Clear();

    public static bool IsLocked(string key, DateTime now) =>
        Failures.TryGetValue(key, out var f) && f.Fails >= MaxFailures && now - f.FirstUtc < LockFor;

    private static void Fail(string key, DateTime now)
    {
        Failures.AddOrUpdate(key, (1, now), (_, f) => now - f.FirstUtc >= LockFor ? (1, now) : (f.Fails + 1, f.FirstUtc));
        if (Failures.Count > 5000) foreach (var k in Failures.Where(x => now - x.Value.FirstUtc >= LockFor).Select(x => x.Key).ToList()) Failures.TryRemove(k, out _);
    }

    private static IResult Problem(int status, string message) => Results.Json(new { error = message }, statusCode: status);

    private static string Resource(string name)
    {
        using var s = typeof(ManagerEndpoints).Assembly.GetManifestResourceStream($"Manager/{name}")
                      ?? throw new FileNotFoundException(name);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static readonly Dictionary<string, (string File, string Type)> Files = new()
    {
        [ManagerRoutes.Page + "/"] = ("index.html", "text/html; charset=utf-8"),
        [$"{ManagerRoutes.Page}/app.js"] = ("app.js", "text/javascript; charset=utf-8"),
        [$"{ManagerRoutes.Page}/style.css"] = ("style.css", "text/css; charset=utf-8"),
        [$"{ManagerRoutes.Page}/manifest.webmanifest"] = ("manifest.webmanifest", "application/manifest+json; charset=utf-8"),
        [$"{ManagerRoutes.Page}/sw.js"] = ("sw.js", "text/javascript; charset=utf-8"),
        [$"{ManagerRoutes.Page}/icon.svg"] = ("icon.svg", "image/svg+xml"),
        [TvRoutes.Page + "/"] = ("tv.html", "text/html; charset=utf-8"),
        [$"{TvRoutes.Page}/tv.js"] = ("tv.js", "text/javascript; charset=utf-8"),
        [$"{TvRoutes.Page}/tv.css"] = ("tv.css", "text/css; charset=utf-8"),
    };

    public static void Map(WebApplication app, RouteGroupBuilder agent)
    {
        // ---------- الصفحة (ثابتة، بلا أي بيانات) ----------
        foreach (var (route, f) in Files)
            app.MapGet("/" + route, (HttpContext http) =>
            {
                http.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'";
                http.Response.Headers["X-Content-Type-Options"] = "nosniff";
                http.Response.Headers["Referrer-Policy"] = "no-referrer";
                http.Response.Headers.CacheControl = route.EndsWith("sw.js") ? "no-cache" : "public, max-age=300";
                return Results.Text(Resource(f.File), f.Type);
            });
        // بلا شرطة مائلة: تحويل إلى الصفحة (نطاق التطبيق المثبّت /manager/). بوسيط لأن المسارين يتطابقان في التوجيه
        app.Use(async (ctx, next) =>
        {
            if (HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Path.Value is { } path && (path == "/" + ManagerRoutes.Page || path == "/" + TvRoutes.Page))
            { ctx.Response.Redirect(path + "/"); return; }
            await next();
        });

        // ---------- الدخول والخروج ----------
        app.MapPost("/" + ManagerRoutes.Login, async (HttpContext http, RelayDb db, ManagerLoginRequest req) =>
        {
            var now = DateTime.UtcNow;
            var name = ManagerLogin.NormalizeName(req.Username);
            var ip = http.Connection.RemoteIpAddress?.ToString() ?? "?";
            var key = $"{ip}|{name}";
            if (IsLocked(key, now)) return Problem(429, "محاولات خاطئة كثيرة — انتظر 15 دقيقة ثم أعد المحاولة");
            var account = name.Length is >= 3 and <= 30 ? await db.ManagerAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Username == name) : null;
            // نفس العمل في حالة الاسم المجهول: لا يُعرف من الزمن هل الاسم موجود
            var ok = ManagerLogin.Verify(req.Password, account?.Salt ?? DummySalt, account?.Hash ?? DummyHash) && account is { IsActive: true };
            if (!ok) { Fail(key, now); return Problem(401, "اسم المستخدم أو كلمة المرور غير صحيحة"); }
            Failures.TryRemove(key, out _);

            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            db.ManagerSessions.Add(new RelayManagerSession { TokenHash = CloudHash.Of(token), Username = account!.Username, CreatedAt = now, ExpiresAt = now + SessionLife });
            await db.ManagerSessions.Where(s => s.ExpiresAt < now).ExecuteDeleteAsync();
            await db.SaveChangesAsync();
            return Results.Ok(new ManagerLoginResponse(token, now + SessionLife, ManagerKinds.IsValid(account.Kind) ? account.Kind : ManagerKinds.Manager));
        });

        app.MapPost("/api/manager/logout", async (HttpContext http, RelayDb db) =>
        {
            if (BearerOf(http) is { } token) await db.ManagerSessions.Where(s => s.TokenHash == CloudHash.Of(token)).ExecuteDeleteAsync();
            return Results.Ok(new { ok = true });
        });

        // ---------- الملخص (قراءة فقط) ----------
        app.MapGet("/" + ManagerRoutes.Summary, async (HttpContext http, RelayDb db) =>
        {
            var (account, error) = await AuthenticateAsync(http, db);
            if (account is null) return error!;
            // حساب التلفاز لا يرى بيانات المدير أبدًا
            if (account.Kind != ManagerKinds.Manager) return Problem(403, "هذا الحساب لشاشة التلفاز فقط");
            var row = await db.ManagerSummaries.AsNoTracking().FirstOrDefaultAsync(r => r.Id == SummaryId);
            http.Response.Headers.CacheControl = "no-store";
            if (row is null) return Problem(404, "لم يصل ملخص من المعمل بعد — حاول بعد دقيقة");
            var agent = await db.AgentState.AsNoTracking().Select(a => (DateTime?)a.LastSeenAt).FirstOrDefaultAsync();
            return Results.Json(new { serverTimeUtc = DateTime.UtcNow, factoryLastSeenUtc = agent, updatedUtc = row.UpdatedAt, summary = JsonDocument.Parse(row.Json).RootElement });
        });

        // شاشة التلفاز: حساب التلفاز (أو المدير للمعاينة) ويقرأ ملخصًا غير حساس منفصلًا
        app.MapGet("/" + TvRoutes.Summary, async (HttpContext http, RelayDb db) =>
        {
            var (account, error) = await AuthenticateAsync(http, db);
            if (account is null) return error!;
            var row = await db.ManagerSummaries.AsNoTracking().FirstOrDefaultAsync(r => r.Id == TvSummaryId);
            http.Response.Headers.CacheControl = "no-store";
            if (row is null) return Problem(404, "لم يصل ملخص من المعمل بعد — حاول بعد دقيقة");
            var agent = await db.AgentState.AsNoTracking().Select(a => (DateTime?)a.LastSeenAt).FirstOrDefaultAsync();
            return Results.Json(new { serverTimeUtc = DateTime.UtcNow, factoryLastSeenUtc = agent, updatedUtc = row.UpdatedAt, summary = JsonDocument.Parse(row.Json).RootElement });
        });

        // ---------- من حاسبة المعمل (بمفتاح المعمل) ----------
        agent.MapPut(ManagerRoutes.AgentSummary, async (RelayDb db, ManagerSummary summary) =>
        {
            var json = JsonSerializer.Serialize(summary, Json);
            if (json.Length > MaxSummaryChars) return Problem(400, "الملخص كبير جدًا");
            var row = await db.ManagerSummaries.FirstOrDefaultAsync(r => r.Id == SummaryId);
            if (row is null) db.ManagerSummaries.Add(row = new RelayManagerSummary { Id = SummaryId });
            (row.Json, row.UpdatedAt) = (json, DateTime.UtcNow);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        agent.MapPut(TvRoutes.AgentSummary, async (RelayDb db, TvSummary summary) =>
        {
            var json = JsonSerializer.Serialize(summary, Json);
            if (json.Length > MaxSummaryChars) return Problem(400, "الملخص كبير جدًا");
            var row = await db.ManagerSummaries.FirstOrDefaultAsync(r => r.Id == TvSummaryId);
            if (row is null) db.ManagerSummaries.Add(row = new RelayManagerSummary { Id = TvSummaryId });
            (row.Json, row.UpdatedAt) = (json, DateTime.UtcNow);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        // القائمة كاملة في كل مرة: ما ليس فيها يُحذف، ورموز الموقوف والمحذوف تُبطل فورًا
        agent.MapPut(ManagerRoutes.AgentAccounts, async (RelayDb db, List<AgentManagerAccount> accounts) =>
        {
            var now = DateTime.UtcNow;
            if (accounts.Any(a => ManagerLogin.ValidateUsername(a.Username) is not null || string.IsNullOrEmpty(a.Salt) || string.IsNullOrEmpty(a.Hash) || !ManagerKinds.IsValid(a.Kind)))
                return Problem(400, "حساب غير صحيح");
            var byName = accounts.GroupBy(a => ManagerLogin.NormalizeName(a.Username)).ToDictionary(g => g.Key, g => g.Last());
            var existing = await db.ManagerAccounts.ToDictionaryAsync(a => a.Username);
            var changedPassword = existing.Where(kv => byName.TryGetValue(kv.Key, out var n) && (n.Hash != kv.Value.Hash || n.Kind != kv.Value.Kind)).Select(kv => kv.Key).ToList();
            foreach (var (name, a) in byName)
            {
                if (!existing.TryGetValue(name, out var row)) db.ManagerAccounts.Add(row = new RelayManagerAccount { Username = name });
                if (row.Salt != a.Salt || row.Hash != a.Hash || row.IsActive != a.IsActive || row.Kind != a.Kind) row.UpdatedAt = now;
                (row.Salt, row.Hash, row.IsActive, row.Kind) = (a.Salt, a.Hash, a.IsActive, a.Kind);
            }
            var gone = existing.Keys.Where(k => !byName.ContainsKey(k)).ToList();
            db.ManagerAccounts.RemoveRange(gone.Select(k => existing[k]));
            await db.SaveChangesAsync();
            // كلمة مرور تغيّرت أو حساب موقوف/محذوف: جلساته تنتهي
            var revoke = gone.Concat(byName.Where(kv => !kv.Value.IsActive).Select(kv => kv.Key)).Concat(changedPassword).Distinct().ToList();
            if (revoke.Count > 0) await db.ManagerSessions.Where(s => revoke.Contains(s.Username)).ExecuteDeleteAsync();
            return Results.Ok(new { count = byName.Count });
        });
    }

    private const int SummaryId = 1, TvSummaryId = 2;

    /// <summary>الحساب صاحب الرمز (فعّال)، أو رد الخطأ المناسب.</summary>
    private static async Task<(RelayManagerAccount? account, IResult? error)> AuthenticateAsync(HttpContext http, RelayDb db)
    {
        var now = DateTime.UtcNow;
        if (BearerOf(http) is not { } token) return (null, Problem(401, "سجّل الدخول أولًا"));
        var hash = CloudHash.Of(token);
        var session = await db.ManagerSessions.AsNoTracking().FirstOrDefaultAsync(s => s.TokenHash == hash && s.ExpiresAt > now);
        var account = session is null ? null : await db.ManagerAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Username == session.Username && a.IsActive);
        return account is null ? (null, Problem(401, "انتهت الجلسة — سجّل الدخول من جديد")) : (account, null);
    }

    private static string? BearerOf(HttpContext http)
    {
        var h = http.Request.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && h.Length is > 20 and < 200 ? h[7..].Trim() : null;
    }

    private static readonly string DummySalt = ManagerLogin.NewSalt();
    private static readonly string DummyHash = ManagerLogin.Hash("x-dummy-password", DummySalt);
}
