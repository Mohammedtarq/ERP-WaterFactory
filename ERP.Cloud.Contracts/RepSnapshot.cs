namespace ERP.Cloud.Contracts;

/// <summary>
/// «نسخة عمل» المندوب: كل ما يحتاجه الهاتف ليعمل بلا إنترنت — زبائنه وأرصدتهم وحدود دينهم، والأصناف بعبواتها وأسعارها،
/// ورصيد سيارته ومحفظته، وإعدادات التطبيق. لا حسابات ولا قيود ولا بيانات مندوب آخر.
/// السعر هنا للعرض فقط؛ المعمل يسعّر الحركة بنفسه عند ترحيلها.
/// </summary>
public record RepSnapshot(
    int RepEmployeeId,
    string RepName,
    DateTime GeneratedAtUtc,
    bool AllowCreditOverLimit,
    int CashAlertDays,
    decimal WalletBalance,
    List<SnapshotProduct> Products,
    List<SnapshotCustomer> Customers,
    List<SnapshotAgentPrice> AgentPrices,
    List<SnapshotStock> VanStock);

/// <summary>صنف قابل للبيع: سعر القطعة العادي، وعبواته (الأكبر أولًا).</summary>
public record SnapshotProduct(int ItemId, string Code, string Name, decimal PiecePrice, List<SnapshotLevel> Levels);

public record SnapshotLevel(int LevelId, string Name, decimal Units);

/// <summary>
/// زبون المندوب. PriceAgentId: الوكيل الذي يُسعَّر هذا الزبون بأسعاره الخاصة (الوكيل نفسه، أو وكيل العميل الفرعي).
/// </summary>
public record SnapshotCustomer(int Id, string Name, string? Phone, string? Address, string Type, int? PriceAgentId, decimal? CreditLimit, decimal Balance);

/// <summary>سعر قطعة خاص بوكيل لصنف.</summary>
public record SnapshotAgentPrice(int AgentCustomerId, int ItemId, decimal PiecePrice);

/// <summary>رصيد السيارة بالقطعة؛ الهاتف يعرضه بالعبوة.</summary>
public record SnapshotStock(int ItemId, decimal Pieces);
