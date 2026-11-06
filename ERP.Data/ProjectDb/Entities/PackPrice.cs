namespace ERP.Data.ProjectDb.Entities;

/// <summary>
/// سعر عبوة كما يُكتب (كارتون 40×330 = 4,250) — 58_pack_prices.sql. CustomRecipeId فارغ = السعر العام الموحّد للمنتج،
/// وغير فارغ = سعر الطلب الخاص (المتغير) لتلك العبوة. لا أسعار بالقطعة للبيع.
/// </summary>
public class PackPrice
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;
    public int PackagingLevelId { get; set; }
    public ItemPackagingLevel PackagingLevel { get; set; } = null!;
    public int? CustomRecipeId { get; set; }
    public CustomRecipe? CustomRecipe { get; set; }
    public decimal Price { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int? UpdatedByUserId { get; set; }
}
