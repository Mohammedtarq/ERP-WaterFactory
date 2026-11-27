using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>سطر في "مقترح الشراء": هل تكفي المادة حتى يصل الطلب الجديد من المورد؟</summary>
public class ReorderRow
{
    public int ItemId { get; init; }
    public string ItemCode { get; init; } = "";
    public string ItemName { get; init; } = "";
    public decimal OnHand { get; init; }
    public decimal DailyUse { get; init; }
    public int LeadTimeDays { get; init; }
    public decimal? AlertLevel { get; init; }
    /// <summary>كم يومًا يكفي الرصيد الحالي بمعدل الاستهلاك الأخير (null = لا استهلاك).</summary>
    public decimal? CoverDays => DailyUse > 0 ? Math.Round(OnHand / DailyUse, 1) : null;
    /// <summary>نقطة إعادة الطلب = الاستهلاك اليومي × (مدة التجهيز + أيام الأمان)، وحد التنبيه إن كان أكبر.</summary>
    public decimal ReorderPoint { get; init; }
    public decimal SuggestedQuantity { get; init; }
    public int Safety { get; init; } = ReorderService.SafetyDays;
    public bool NeedsOrder => OnHand <= ReorderPoint && (DailyUse > 0 || AlertLevel > 0);
    public string Status => OnHand <= 0 ? "نفد" : NeedsOrder ? "اطلب الآن" : CoverDays is decimal d && d < LeadTimeDays + Safety * 2 ? "قريبًا" : "كافٍ";
}

/// <summary>
/// مقترح الشراء: لكل مادة أولية، الاستهلاك اليومي من آخر فترة، ومدة تجهيز المورد،
/// فيظهر ما يجب طلبه الآن حتى لا يتوقف الإنتاج قبل وصول البضاعة.
/// </summary>
public class ReorderService
{
    public const int SafetyDays = 7;
    private readonly ProjectDbContext _db;
    public ReorderService(ProjectDbContext db) => _db = db;

    /// <param name="historyDays">فترة احتساب معدل الاستهلاك.</param>
    /// <param name="coverDays">بعد الوصول، يكفي الطلب المقترح هذا العدد من الأيام.</param>
    /// <param name="safetyDays">أيام أمان فوق مدة التجهيز (قابلة للتعديل من الشاشة).</param>
    public async Task<List<ReorderRow>> SuggestAsync(int historyDays = 30, int coverDays = 30, int defaultLeadTime = 7, int safetyDays = SafetyDays)
    {
        var since = DateTime.UtcNow.Date.AddDays(-historyDays);
        var items = await _db.Items.AsNoTracking()
            .Where(i => i.IsActive && i.SourcingMethod != SourcingMethod.Manufactured).OrderBy(i => i.ItemCode).ToListAsync();
        var ids = items.Select(i => i.Id).ToList();
        var stockWh = await _db.Warehouses.AsNoTracking()
            .Where(w => w.WarehouseType == WarehouseType.RawMaterial || w.WarehouseType == WarehouseType.WorkInProcess).Select(w => w.Id).ToListAsync();
        // حد التنبيه: مجموع حدود مخازن المواد الأولية (المضبوطة من داخل كل مخزن، أو حد بطاقة الصنف في المخزن الرئيسي)
        var alerts = await new StockAlertService(_db).TotalsAsync(stockWh, items);
        var onHand = await _db.StockTransactions.Where(t => ids.Contains(t.ItemId) && stockWh.Contains(t.WarehouseId))
            .GroupBy(t => t.ItemId).Select(g => new { g.Key, Qty = g.Sum(t => t.QuantityBaseUnits) }).ToDictionaryAsync(x => x.Key, x => x.Qty);
        // الاستهلاك = ما خرج من مخزن المواد إلى الإنتاج أو للصرف (لا المناقلات الداخلية بين مخازن المواد)
        var consumeTypes = new[] { StockTransactionType.ProductionConsume, StockTransactionType.WipIssue, StockTransactionType.Issue,
                                   StockTransactionType.SalesIssue, StockTransactionType.Damaged };
        var used = await _db.StockTransactions
            .Where(t => ids.Contains(t.ItemId) && t.QuantityBaseUnits < 0 && t.TransactionDate >= since && consumeTypes.Contains(t.TransactionType))
            .GroupBy(t => t.ItemId).Select(g => new { g.Key, Qty = -g.Sum(t => t.QuantityBaseUnits) }).ToDictionaryAsync(x => x.Key, x => x.Qty);
        // صرف الماكينة ثم إرجاع المتبقي: الصافي هو الاستهلاك
        var returned = await _db.StockTransactions
            .Where(t => ids.Contains(t.ItemId) && t.QuantityBaseUnits > 0 && t.TransactionDate >= since && t.TransactionType == StockTransactionType.WipReturn)
            .GroupBy(t => t.ItemId).Select(g => new { g.Key, Qty = g.Sum(t => t.QuantityBaseUnits) }).ToDictionaryAsync(x => x.Key, x => x.Qty);

        return items.Select(i =>
        {
            var qty = onHand.GetValueOrDefault(i.Id);
            var daily = Math.Max(0, used.GetValueOrDefault(i.Id) - returned.GetValueOrDefault(i.Id)) / historyDays;
            var lead = i.LeadTimeDays ?? defaultLeadTime;
            var alert = alerts.TryGetValue(i.Id, out var a) ? a : (decimal?)null;
            var point = Math.Max(Math.Round(daily * (lead + safetyDays), 0), alert ?? 0);
            var target = daily * (lead + coverDays);
            var suggested = Math.Max(0, Math.Ceiling(Math.Max(target, point) - qty));
            return new ReorderRow
            {
                ItemId = i.Id, ItemCode = i.ItemCode, ItemName = i.ItemName, OnHand = qty, DailyUse = Math.Round(daily, 2),
                LeadTimeDays = lead, AlertLevel = alert, ReorderPoint = point, Safety = safetyDays,
                SuggestedQuantity = qty <= point ? suggested : 0
            };
        }).OrderBy(r => r.Status switch { "نفد" => 0, "اطلب الآن" => 1, "قريبًا" => 2, _ => 3 }).ThenBy(r => r.ItemCode).ToList();
    }
}
