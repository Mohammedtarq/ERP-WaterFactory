using System.Text.Json;
using ERP.Cloud.Contracts;
using Microsoft.Data.Sqlite;

namespace ERP.RepApp.Core;

/// <summary>حالة الحركة في الهاتف: Queued = لم تُرسل بعد، Sent = في الخادم تنتظر المعمل، Done = عادت نتيجتها.</summary>
public enum OutboxState { Queued, Sent, Done }

/// <summary>أثر الحركة على الأرصدة المعروضة قبل أن تصل نسخة عمل جديدة من المعمل.</summary>
public record Effect(List<SnapshotStock> Stock, decimal Wallet = 0, int? CustomerId = null, decimal Balance = 0)
{
    public static readonly Effect None = new(new List<SnapshotStock>());
}

/// <summary>سطر مادة في الحركة كما يُعرض في «حركاتي» ويُطبع على الوصل.</summary>
public record ReceiptLine(string Item, string Pack, decimal Quantity, decimal UnitPrice, decimal Total);

/// <summary>حركة في صندوق الإرسال بالهاتف. ReceiptNo: رقم وصل البيع المتسلسل في هذا الهاتف.</summary>
public record OutboxItem(
    Guid ClientId, string Kind, DateTime OccurredAt, string Payload, byte[]? Photo, string Summary, decimal Amount,
    Effect Effect, DateTime CreatedAtUtc, OutboxState State, string? Status, string? Message, int? ResultId, string? Warning, DateTime UpdatedAtUtc,
    List<ReceiptLine>? Lines = null, int? ReceiptNo = null)
{
    /// <summary>الحالة كما يقرؤها المندوب.</summary>
    public string StateText => State switch
    {
        OutboxState.Queued => "بانتظار الإرسال",
        OutboxState.Sent => "وصلت الخادم — بانتظار المعمل",
        _ => Status switch
        {
            "Posted" => "رُحّلت",
            "Pending" => "بانتظار الاعتماد",
            "Rejected" => "مرفوضة",
            _ => "متعذّرة"
        }
    };

    /// <summary>متعذّرة: يصحّحها المندوب ويعيد إرسالها بالرقم نفسه.</summary>
    public bool CanResubmit => State == OutboxState.Done && Status == "Failed";
}

/// <summary>
/// الحفظ المحلي في الهاتف (SQLite): ربط الجهاز، وآخر نسخة عمل، وصندوق الإرسال.
/// كل ما يسجّله المندوب يُحفظ هنا أولًا، فلا يضيع بانقطاع الشبكة أو إغلاق التطبيق.
/// </summary>
public class LocalStore
{
    private readonly string _cs;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public LocalStore(string databasePath)
    {
        _cs = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS Settings (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Outbox (
                ClientId TEXT PRIMARY KEY, Kind TEXT NOT NULL, OccurredAt TEXT NOT NULL, Payload TEXT NOT NULL, Photo BLOB,
                Summary TEXT NOT NULL, Amount TEXT NOT NULL, Effect TEXT NOT NULL, CreatedAt TEXT NOT NULL,
                State INTEGER NOT NULL, Status TEXT, Message TEXT, ResultId INTEGER, Warning TEXT, UpdatedAt TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Outbox_State ON Outbox (State, CreatedAt);
            """);
        // أعمدة أُضيفت بعد الإصدار الأول (الهواتف المثبّت عليها تطبيق قديم تُرقّى هنا)
        foreach (var (column, type) in new[] { ("Lines", "TEXT"), ("ReceiptNo", "INTEGER") })
            if (!HasColumn(c, "Outbox", column)) Exec(c, $"ALTER TABLE Outbox ADD COLUMN {column} {type}");
    }

    private static bool HasColumn(SqliteConnection c, string table, string column)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $n";
        cmd.Parameters.AddWithValue("$n", column);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql, params (string name, object? value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // ============================ الإعدادات ============================

    public string? Get(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Settings WHERE Key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void Set(string key, string? value)
    {
        using var c = Open();
        if (value is null) Exec(c, "DELETE FROM Settings WHERE Key = $k", ("$k", key));
        else Exec(c, "INSERT INTO Settings (Key, Value) VALUES ($k, $v) ON CONFLICT(Key) DO UPDATE SET Value = $v", ("$k", key), ("$v", value));
    }

    public LinkCode? Link => Get("ServerUrl") is string u && Get("DeviceKey") is string k ? new LinkCode(u, k, Get("LocalUrl"), Get("FactoryId")) : null;

    public void SaveLink(LinkCode link)
    {
        Set("ServerUrl", link.ServerUrl);
        Set("DeviceKey", link.DeviceKey);
        Set("LocalUrl", link.LocalUrl);
        Set("FactoryId", link.FactoryId);
    }

    /// <summary>
    /// عناوين المعمل الحالية من نسخة العمل (تغيّر عنوان الحاسبة، أو أُضيف الخادم السحابي): تُحدَّث وحدها بلا كتابة يدوية.
    /// لا يُقبل إلا عنوان صحيح، ولا يُمسح عنوان يعمل بقيمة فارغة.
    /// </summary>
    /// <returns>true إن تغيّر شيء.</returns>
    public bool ApplyServers(SnapshotServers? servers)
    {
        if (servers is null || Link is not { } link) return false;
        var cloud = servers.CloudUrl is { } c && CloudUrl.Validate(c) is null ? c.Trim().TrimEnd('/') : null;
        var local = servers.LocalUrl is { } l && CloudUrl.Validate(l) is null ? l.Trim().TrimEnd('/') : null;
        if (cloud is null && local is null) return false;
        var updated = link with
        {
            ServerUrl = cloud ?? local!,
            LocalUrl = cloud is null ? null : local,
            FactoryId = servers.FactoryId is { Length: FactoryIdentity.Length } f ? f : link.FactoryId
        };
        if (updated == link) return false;
        SaveLink(updated);
        return true;
    }

    /// <summary>عنوان جديد للخادم المحلي وجده الهاتف على الشبكة (بصمة المعمل نفسها).</summary>
    public void UpdateLocal(string url)
    {
        if (Link is not { } link || CloudUrl.Validate(url) is not null) return;
        // خادم محلي فقط: هو العنوان الرئيسي؛ ومع السحابي يبقى السحابي رئيسيًا
        var onlyLocal = link.LocalUrl is null && IsLan(link.ServerUrl);
        SaveLink(onlyLocal ? link with { ServerUrl = url } : link with { LocalUrl = url });
    }

    private static bool IsLan(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && (CloudUrl.IsPrivateLan(u.Host) || u.IsLoopback);

    /// <summary>فك الربط (جهاز جديد للمندوب، أو نقل الهاتف): يُرفض ما دامت حركات لم تصل المعمل.</summary>
    public string? Unlink()
    {
        if (Outbox().Any(i => i.State != OutboxState.Done)) return "توجد حركات لم تصل المعمل بعد — أرسلها أولًا";
        foreach (var k in new[] { "ServerUrl", "DeviceKey", "LocalUrl", "FactoryId", "Snapshot", "SnapshotEtag" }) Set(k, null);
        return null;
    }

    /// <summary>
    /// تغيير عنوان الخادم فقط (تغيّرت شبكة المعمل أو انتقل الخادم): يبقى مفتاح الجهاز ونسخة العمل وكل الحركات،
    /// وما لم يُرسل يُرسل إلى العنوان الجديد.
    /// </summary>
    public string? ChangeServer(string? url)
    {
        if (Link is null) return "الهاتف غير مربوط — صوّر رمز الربط";
        if (CloudUrl.Validate(url) is string bad) return bad;
        Set("ServerUrl", url!.Trim().TrimEnd('/'));
        return null;
    }

    public RepSnapshot? Snapshot => Get("Snapshot") is string s ? JsonSerializer.Deserialize<RepSnapshot>(s, Json) : null;
    public string? SnapshotEtag => Get("SnapshotEtag");

    public void SaveSnapshot(string json, string? etag)
    {
        _ = JsonSerializer.Deserialize<RepSnapshot>(json, Json) ?? throw new JsonException();
        Set("Snapshot", json);
        Set("SnapshotEtag", etag);
    }

    // ============================ صندوق الإرسال ============================

    /// <param name="receipt">true للبيع: يأخذ رقم الوصل التالي في هذا الهاتف.</param>
    public OutboxItem Enqueue(string kind, object payload, string summary, decimal amount, Effect effect, byte[]? photo = null, DateTime? occurredAt = null,
                              List<ReceiptLine>? lines = null, bool receipt = false)
    {
        var now = DateTime.UtcNow;
        using var c = Open();
        Exec(c, "BEGIN IMMEDIATE");   // رقم الوصل والحركة معًا
        int? receiptNo = null;
        if (receipt)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(MAX(ReceiptNo), 0) + 1 FROM Outbox";
            receiptNo = Math.Max(Convert.ToInt32(cmd.ExecuteScalar()), int.TryParse(Get(c, "ReceiptSeq"), out var seq) ? seq + 1 : 1);
            Exec(c, "INSERT INTO Settings (Key, Value) VALUES ('ReceiptSeq', $v) ON CONFLICT(Key) DO UPDATE SET Value = $v", ("$v", receiptNo.Value.ToString()));
        }
        var item = new OutboxItem(Guid.NewGuid(), kind, occurredAt ?? DateTime.Now, JsonSerializer.Serialize(payload, payload.GetType(), Json), photo,
                                  summary, amount, effect, now, OutboxState.Queued, null, null, null, null, now, lines, receiptNo);
        Exec(c, """
            INSERT INTO Outbox (ClientId, Kind, OccurredAt, Payload, Photo, Summary, Amount, Effect, CreatedAt, State, UpdatedAt, Lines, ReceiptNo)
            VALUES ($id, $kind, $at, $payload, $photo, $summary, $amount, $effect, $created, 0, $created, $lines, $receipt)
            """,
            ("$id", item.ClientId.ToString()), ("$kind", kind), ("$at", item.OccurredAt.ToString("O")), ("$payload", item.Payload), ("$photo", photo),
            ("$summary", summary), ("$amount", amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("$effect", JsonSerializer.Serialize(effect, Json)), ("$created", now.ToString("O")),
            ("$lines", lines is null ? null : JsonSerializer.Serialize(lines, Json)), ("$receipt", receiptNo));
        Exec(c, "COMMIT");
        return item;
    }

    private static string? Get(SqliteConnection c, string key)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Settings WHERE Key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>تصحيح حركة متعذّرة: محتوى جديد بالرقم نفسه، وتعود لصندوق الإرسال.</summary>
    public string? Resubmit(Guid clientId, object payload, string summary, decimal amount, Effect effect, List<ReceiptLine>? lines = null)
    {
        var item = Find(clientId);
        if (item is null) return "الحركة غير موجودة";
        if (!item.CanResubmit) return "تُعاد الحركة المتعذّرة فقط";
        using var c = Open();
        Exec(c, """
            UPDATE Outbox SET Payload = $payload, Summary = $summary, Amount = $amount, Effect = $effect, Lines = COALESCE($lines, Lines),
                   State = 0, Status = NULL, Message = NULL, ResultId = NULL, Warning = NULL, UpdatedAt = $now WHERE ClientId = $id
            """,
            ("$id", clientId.ToString()), ("$payload", JsonSerializer.Serialize(payload, payload.GetType(), Json)), ("$summary", summary),
            ("$amount", amount.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("$effect", JsonSerializer.Serialize(effect, Json)),
            ("$lines", lines is null ? null : JsonSerializer.Serialize(lines, Json)), ("$now", DateTime.UtcNow.ToString("O")));
        return null;
    }

    /// <summary>إعادة إرسال متعذّرة كما هي (السبب زال: مثلًا حُمّلت السيارة أو سُجّل الزبون).</summary>
    public string? ResubmitAsIs(Guid clientId)
    {
        var item = Find(clientId);
        if (item is null) return "الحركة غير موجودة";
        return Resubmit(clientId, System.Text.Json.Nodes.JsonNode.Parse(item.Payload)!, item.Summary, item.Amount, item.Effect);
    }

    /// <summary>تطبيق ما رد به الخادم (Waiting = وصلت ولم يستلمها المعمل، Done = عادت النتيجة).</summary>
    public void Apply(PhoneRequestStatus s)
    {
        using var c = Open();
        var state = s.State == RelayState.Done ? OutboxState.Done : OutboxState.Sent;
        Exec(c, """
            UPDATE Outbox SET State = $state, Status = $status, Message = $message, ResultId = $result, Warning = $warning, UpdatedAt = $now
            WHERE ClientId = $id
            """,
            ("$id", s.ClientId.ToString()), ("$state", (int)state), ("$status", s.Status), ("$message", s.Message), ("$result", s.ResultId),
            ("$warning", s.Warning), ("$now", DateTime.UtcNow.ToString("O")));
    }

    public OutboxItem? Find(Guid clientId) => Query("WHERE ClientId = $id", ("$id", clientId.ToString())).FirstOrDefault();

    /// <summary>كل الحركات، الأحدث أولًا.</summary>
    public List<OutboxItem> Outbox(int days = 30) =>
        Query("WHERE CreatedAt >= $since ORDER BY CreatedAt DESC", ("$since", DateTime.UtcNow.AddDays(-days).ToString("O")));

    public List<OutboxItem> Queued() => Query("WHERE State = 0 ORDER BY CreatedAt");

    /// <summary>ما ينتظر نتيجة من المعمل: في الخادم، أو بانتظار الاعتماد.</summary>
    public List<OutboxItem> AwaitingResult() => Query("WHERE State = 1 OR (State = 2 AND Status = 'Pending') ORDER BY CreatedAt");

    private List<OutboxItem> Query(string where, params (string name, object? value)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT ClientId, Kind, OccurredAt, Payload, Photo, Summary, Amount, Effect, CreatedAt, State, Status, Message, ResultId, Warning, UpdatedAt, Lines, ReceiptNo FROM Outbox {where}";
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<OutboxItem>();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        while (r.Read())
            list.Add(new OutboxItem(
                Guid.Parse(r.GetString(0)), r.GetString(1), DateTime.Parse(r.GetString(2), inv, System.Globalization.DateTimeStyles.RoundtripKind), r.GetString(3),
                r.IsDBNull(4) ? null : (byte[])r[4], r.GetString(5), decimal.Parse(r.GetString(6), inv),
                JsonSerializer.Deserialize<Effect>(r.GetString(7), Json) ?? Effect.None,
                DateTime.Parse(r.GetString(8), inv, System.Globalization.DateTimeStyles.RoundtripKind), (OutboxState)r.GetInt32(9),
                r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetInt32(12),
                r.IsDBNull(13) ? null : r.GetString(13), DateTime.Parse(r.GetString(14), inv, System.Globalization.DateTimeStyles.RoundtripKind),
                r.IsDBNull(15) ? null : JsonSerializer.Deserialize<List<ReceiptLine>>(r.GetString(15), Json), r.IsDBNull(16) ? null : r.GetInt32(16)));
        return list;
    }
}
