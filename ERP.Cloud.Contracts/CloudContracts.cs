namespace ERP.Cloud.Contracts;

/// <summary>أسماء الترويسات ومسارات الواجهة — مصدر واحد للهاتف والخادم وخدمة المزامنة.</summary>
public static class CloudRoutes
{
    public const string DeviceKeyHeader = "X-Device-Key";
    public const string AgentKeyHeader = "X-Agent-Key";

    // الهاتف
    public const string RepRequests = "api/rep/requests";
    public const string RepStatuses = "api/rep/requests/status";
    public const string RepSnapshot = "api/rep/snapshot";
    public const string RepPing = "api/rep/ping";

    // خدمة المزامنة في المعمل
    public const string AgentPing = "api/agent/ping";
    public const string AgentDevices = "api/agent/devices";
    public const string AgentInbox = "api/agent/inbox";
    public const string AgentResults = "api/agent/results";
    public const string AgentOpen = "api/agent/open";
    public const string AgentSnapshots = "api/agent/snapshots";
    public const string AgentSnapshotHashes = "api/agent/snapshots/hashes";

    public const string Health = "api/health";
}

/// <summary>حالة الحركة في الخادم: Waiting = لم يستلمها المعمل بعد، Done = عادت نتيجتها.</summary>
public static class RelayState
{
    public const string Waiting = "Waiting";
    public const string Done = "Done";
}

// ============================ الهاتف ⇄ الخادم ============================

/// <summary>حركة يرسلها الهاتف: رقم فريد يولّده الهاتف، والنوع (CashSale، Return…)، ووقتها، ومحتواها JSON، وصورة اختيارية.</summary>
public record PhoneRequest(Guid ClientId, string Kind, DateTime OccurredAt, string Payload, byte[]? Photo = null);

/// <summary>
/// حالة الحركة كما يراها الهاتف. Status بعد وصولها للمعمل: Posted / Pending / Rejected / Failed.
/// </summary>
public record PhoneRequestStatus(Guid ClientId, string State, string? Status, string Message, int? ResultId = null, string? Warning = null);

public record PhoneStatusQuery(List<Guid> ClientIds);

/// <summary>آخر اتصال للمعمل بالخادم: يعرضه الهاتف («المعمل متصل قبل دقيقتين»).</summary>
public record PhonePing(DateTime ServerTimeUtc, DateTime? FactoryLastSeenUtc, DateTime? SnapshotUpdatedUtc);

// ============================ خدمة المزامنة ⇄ الخادم ============================

/// <summary>جهاز مسجّل في المعمل: الخادم يحفظ بصمة المفتاح فقط (SHA-256)، لا المفتاح نفسه.</summary>
public record AgentDevice(int DeviceId, int RepEmployeeId, string KeyHash, bool IsActive);

public record AgentInboxItem(Guid ClientId, int DeviceId, string Kind, DateTime OccurredAt, string Payload, byte[]? Photo);

public record AgentResult(Guid ClientId, bool Accepted, string Status, string Message, int? ResultId = null, string? Warning = null);

public record AgentSnapshot(int RepEmployeeId, string Hash, string Json);

public record AgentPing(DateTime ServerTimeUtc, int Waiting, string Version);

/// <summary>أنواع حركات الهاتف المقبولة (بنفس أسماء RepRequestKind في المعمل).</summary>
public static class RepKinds
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { "CashSale", "CreditSale", "Free", "Collection", "NewCustomer", "Expense", "Return" };
}

/// <summary>حدود الحجم: المحتوى نص قصير، وصورة الوصل مضغوطة من الهاتف.</summary>
public static class RelayLimits
{
    public const int MaxPayloadChars = 64 * 1024;
    public const int MaxPhotoBytes = 3 * 1024 * 1024;
    public const int MaxInboxBatch = 50;
    public const int MaxStatusQuery = 500;
}

/// <summary>بصمة المفاتيح: SHA-256 بحروف كبيرة — الخادم والمعمل يحسبانها بالطريقة نفسها.</summary>
public static class CloudHash
{
    public static string Of(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}
