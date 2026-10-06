namespace ERP.Data.ProjectDb.Entities;

/// <summary>
/// حافز المندوب لكل عبوة (37_rep_pack_incentive.sql): مبلغ للشرنك ومبلغ للكارتون من كل صنف.
/// الحافز الشهري = (المحمّل − الراجع − المجاني) بهذه الوحدة × المبلغ، ويُصرف مع الراتب.
/// </summary>
public class RepItemIncentiveRate
{
    public int Id { get; set; }

    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;

    public int PackagingLevelId { get; set; }
    public ItemPackagingLevel PackagingLevel { get; set; } = null!;

    /// <summary>المبلغ لكل عبوة من الوحدة (شرنك واحد، كارتون واحد).</summary>
    public decimal IncentiveRatePerUnit { get; set; }
}

/// <summary>
/// حافز مدير المبيعات: شريحة واحدة من شرائح تصاعدية على إجمالي الكمية المباعة.
/// المعادلة الكاملة (تُحسب في خدمة الرواتب): SUM(كمية كل شريحة × معدلها) × أيام دوام المدير الفعلية.
/// </summary>
public class SalesManagerIncentiveTier
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }   // IsSalesManager = true
    public Employee Employee { get; set; } = null!;

    public decimal FromQuantity { get; set; }
    public decimal? ToQuantity { get; set; }   // NULL = بلا حد أعلى
    public decimal RatePerUnit { get; set; }
}
