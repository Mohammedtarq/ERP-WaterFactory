namespace ERP.Data.ProjectDb.Entities;

/// <summary>سعر يدوي منفصل تمامًا عن سعر البيع العادي، خاص بوكيل واحد ولصنف واحد.</summary>
public class AgentItemPrice
{
    public int Id { get; set; }

    public int CustomerId { get; set; }   // يجب أن يكون CustomerType = Agent
    public Customer Customer { get; set; } = null!;

    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;

    public decimal AgentPrice { get; set; }

    /// <summary>سعر الوكيل لطلب خاص بعينه؛ NULL = سعره للمنتج الأساسي (42_selling_prices_variants.sql).</summary>
    public int? CustomRecipeId { get; set; }
    public CustomRecipe? CustomRecipe { get; set; }
}

/// <summary>إعداد عام لسعر القطعة الواحدة لمستلزمات التحميل، بتاريخ سريان.</summary>
public class LoadingSuppliesSetting
{
    public int Id { get; set; }
    public decimal RatePerPiece { get; set; }
    public DateTime EffectiveDate { get; set; }
}
