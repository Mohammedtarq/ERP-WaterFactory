namespace ERP.Data.ProjectDb.Entities;

/// <summary>
/// حد تنبيه اسم خاص (أو الأساسي): لكل (منتج، متغير) حد يدوي بعبوة المنتج الكبرى (Database/55_variant_alerts.sql).
/// </summary>
public class VariantAlert
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;
    /// <summary>الطلب الخاص؛ NULL = الاسم الأساسي (مثل «الرحمة»).</summary>
    public int? CustomRecipeId { get; set; }
    public CustomRecipe? CustomRecipe { get; set; }
    public decimal MinPacks { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int? UpdatedByUserId { get; set; }
}
