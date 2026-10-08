namespace ERP.Data.ProjectDb.Entities;

public class CustomRecipe
{
    public int Id { get; set; }

    public int FinishedItemId { get; set; }
    public Item FinishedItem { get; set; } = null!;

    public int? CustomerId { get; set; }          // فارغ = ملصق مناسبة عام (رمضان، عيد، زواج...)
    public Customer? Customer { get; set; }

    public string Name { get; set; } = string.Empty;   // مثال: وصفة مطعم الحسون
    public bool IsActive { get; set; } = true;
    /// <summary>السعر العام للطلب الخاص بالقطعة؛ NULL = سعر المنتج الأساسي (42_selling_prices_variants.sql).</summary>
    public decimal? SalePrice { get; set; }

    public ICollection<CustomRecipeLine> Lines { get; set; } = new List<CustomRecipeLine>();
}

/// <summary>مكوّن يستبدل مكوّنًا من الوصفة الأساسية (غطاء، لاصق أمامي/خلفي...).</summary>
public class CustomRecipeLine
{
    public int Id { get; set; }

    public int CustomRecipeId { get; set; }
    public CustomRecipe CustomRecipe { get; set; } = null!;

    public int ComponentItemId { get; set; }   // صنف مادة أولية خاص بهذا العميل (مثل لاصق باسمه)
    public Item ComponentItem { get; set; } = null!;

    public string ComponentLabel { get; set; } = string.Empty;   // غطاء القنينة / لاصق أمامي / لاصق خلفي
    public decimal QuantityPerUnit { get; set; }

    /// <summary>المادة التي يستبدلها هذا المكوّن في الوصفة الأساسية (NULL = مكوّن إضافي). ملف 11.</summary>
    public int? ReplacesRawMaterialItemId { get; set; }
    public Item? ReplacesRawMaterialItem { get; set; }
}
