namespace ERP.Data.ProjectDb.Entities;

public enum SourcingMethod { Manufactured, Purchased, Both }

public class Item
{
    public int Id { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string? BarCode { get; set; }
    public string BaseUnitName { get; set; } = "قطعة";
    public SourcingMethod SourcingMethod { get; set; } = SourcingMethod.Manufactured;
    public decimal SalePrice { get; set; }
    /// <summary>سعر الكلفة للوحدة الأساسية: يُحدَّث تلقائيًا من آخر استلام شراء، ويمكن تعديله يدويًا. للمنتج المصنَّع تُحسب الكلفة من وصفته.</summary>
    public decimal? CostPrice { get; set; }
    /// <summary>حد التنبيه — عند وصول الرصيد لهذه الكمية أو أقل، يظهر تنبيه نقص مخزون.</summary>
    public decimal? MinStockAlertLevel { get; set; }
    public bool IsActive { get; set; } = true;
}
