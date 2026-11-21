namespace ERP.Data.ProjectDb.Entities;

/// <summary>سعر صرف بتاريخ سريان — يُستخدم لتحويل رواتب الدولار إلى الدينار في قيد الرواتب.</summary>
public class ExchangeRate
{
    public int Id { get; set; }
    public DateTime EffectiveDate { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public decimal RateToIQD { get; set; }      // كم دينار يساوي 1 من العملة

    public int EnteredByUserId { get; set; }
    public User EnteredByUser { get; set; } = null!;
}
