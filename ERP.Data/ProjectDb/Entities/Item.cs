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
    /// <summary>
    /// متوسط الكلفة المرجّح للقطعة: يُعاد حسابه تلقائيًا في قاعدة البيانات مع كل وارد مسعَّر (استلام شراء، ناتج إنتاج).
    /// يُدخل يدويًا فقط عند إنشاء الصنف (رصيد افتتاحي).
    /// </summary>
    public decimal? CostPrice { get; set; }
    /// <summary>وزن القطعة بالغرام: يحوّل الشراء بالكغم أو الطن إلى عدد قطع (مثل رول الشرنك).</summary>
    public decimal? UnitWeightGrams { get; set; }
    /// <summary>مدة تجهيز المورد بالأيام: تدخل في نقطة إعادة الطلب ومقترح الشراء.</summary>
    public int? LeadTimeDays { get; set; }
    /// <summary>حد التنبيه — عند وصول الرصيد لهذه الكمية أو أقل، يظهر تنبيه نقص مخزون.</summary>
    public decimal? MinStockAlertLevel { get; set; }
    public bool IsActive { get; set; } = true;
}
