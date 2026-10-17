namespace ERP.Cloud.Contracts;

/// <summary>
/// «نسخة عمل» المندوب: كل ما يحتاجه الهاتف ليعمل بلا إنترنت — زبائنه وأرصدتهم وحدود دينهم، والأصناف بعبواتها وأسعارها،
/// ورصيد سيارته ومحفظته، وإعدادات التطبيق. لا حسابات ولا قيود ولا بيانات مندوب آخر.
/// السعر هنا للعرض فقط؛ المعمل يسعّر الحركة بنفسه عند ترحيلها.
/// PostedClientIds: حركات الهاتف المرحّلة (آخر 7 أيام) الداخلة في هذه الأرصدة — الهاتف لا يطرحها مرة ثانية.
/// CompanyName و CompanyLogo: من «هوية الطباعة» يظهران في رأس التطبيق وعلى وصل البيع (الشعار حتى MaxLogoBytes وإلا يُترك).
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
    List<SnapshotStock> VanStock,
    List<Guid>? PostedClientIds = null,
    string? CompanyName = null,
    byte[]? CompanyLogo = null,
    SnapshotServers? Servers = null,
    SnapshotLogin? Login = null)
{
    /// <summary>الشعار ينتقل مع كل نسخة عمل، فيبقى صغيرًا.</summary>
    public const int MaxLogoBytes = 200 * 1024;
}

/// <summary>
/// عناوين خوادم المعمل الحالية: الهاتف يحدّث بها رمز ربطه تلقائيًا (تغيّر عنوان الحاسبة أو أُضيف الخادم السحابي)
/// فلا يُكتب عنوان يدويًا.
/// </summary>
public record SnapshotServers(string? LocalUrl, string? CloudUrl, string? FactoryId);

/// <summary>
/// صنف قابل للبيع: سعر القطعة العادي، وعبواته (الأكبر أولًا)، وحجمه واسم منتجه الأساسي ومتغيراته (الطلبات الخاصة)
/// للاسم الموحد «كارتون 40×330 الرحمة». الحقول الجديدة اختيارية: نسخة عمل أقدم تبقى مقروءة.
/// </summary>
public record SnapshotProduct(int ItemId, string Code, string Name, decimal PiecePrice, List<SnapshotLevel> Levels,
                              decimal? SizeMl = null, string? BaseVariant = null, List<SnapshotVariant>? Variants = null)
{
    public SnapshotVariant? VariantOf(int? recipeId) =>
        recipeId is int id ? Variants?.FirstOrDefault(v => v.RecipeId == id) ?? new SnapshotVariant(id, "طلب خاص", null) : null;

    /// <summary>الاسم الموحد لعبوة ومتغير: «كارتون 40×330 مطعم داون تاون».</summary>
    public string Label(SnapshotLevel level, int? recipeId) =>
        ProductName.Of(level.Name, level.Units, SizeMl ?? ProductName.ParseSizeMl(Name), Name,
                       recipeId is null ? ProductName.Variant(null, null, null, true, BaseVariant) : VariantOf(recipeId)!.Name);
}

/// <summary>
/// متغير المنتج (طلب خاص لمطعم أو مناسبة): اسمه في الاسم الموحد، وسعر قطعته العام (فارغ = سعر الأساسي)،
/// وأسعار عبواته كما تُكتب (فارغ = سعر عبوة الأساسي).
/// </summary>
public record SnapshotVariant(int RecipeId, string Name, decimal? PiecePrice, List<SnapshotPackPrice>? PackPrices = null);

/// <summary>عبوة بيع وسعرها العام الموحّد كما يُكتب (فارغ في نسخة أقدم = سعر القطعة × قطعها).</summary>
public record SnapshotLevel(int LevelId, string Name, decimal Units, decimal? Price = null);

/// <summary>سعر عبوة لطلب خاص.</summary>
public record SnapshotPackPrice(int LevelId, decimal Price);

/// <summary>
/// زبون المندوب. PriceAgentId: لم يعد مستعملًا — السعر موحّد لكل الزبائن، والوكلاء وعملاؤهم الفرعيون لا يصلون للهاتف (المكتب يبيع لهم).
/// </summary>
public record SnapshotCustomer(int Id, string Name, string? Phone, string? Address, string Type, int? PriceAgentId, decimal? CreditLimit, decimal Balance);

/// <summary>سعر قطعة خاص بوكيل لصنف.</summary>
public record SnapshotAgentPrice(int AgentCustomerId, int ItemId, decimal PiecePrice, int? RecipeId = null);

/// <summary>رصيد السيارة بالقطعة لكل منتج ومتغيره (RecipeId فارغ = الأساسي)؛ الهاتف يعرضه بالعبوة.</summary>
public record SnapshotStock(int ItemId, decimal Pieces, int? RecipeId = null);
