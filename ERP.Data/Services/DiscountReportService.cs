using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>سطر تقرير الخصم الشهري: وكيل أو صاحب خصم تشجيعي.</summary>
public class DiscountReportRow
{
    public int CustomerId { get; init; }
    public string CustomerName { get; init; } = "";
    public DiscountKind Kind { get; init; }
    public int InvoicesCount { get; init; }
    /// <summary>مجموع ما اشتراه بسعر القائمة (قبل الخصم).</summary>
    public decimal GrossBeforeDiscount { get; init; }
    public decimal Discount { get; init; }
    public decimal Net => GrossBeforeDiscount - Discount;
    public decimal DiscountPercent => GrossBeforeDiscount == 0 ? 0 : Math.Round(Discount / GrossBeforeDiscount * 100, 1);
}

/// <summary>سطر «خصم عملاء الوكلاء»: عميل فرعي ووكيله، وخصم فواتيره (عن السعر العام) في الشهر.</summary>
public class AgentCustomerDiscountRow
{
    public int AgentId { get; init; }
    public string AgentName { get; init; } = "";
    public int CustomerId { get; init; }
    public string CustomerName { get; init; } = "";
    public int InvoicesCount { get; init; }
    public decimal GrossBeforeDiscount { get; init; }
    public decimal Discount { get; init; }
    public decimal Net => GrossBeforeDiscount - Discount;
    public decimal DiscountPercent => GrossBeforeDiscount == 0 ? 0 : Math.Round(Discount / GrossBeforeDiscount * 100, 1);
}

/// <summary>
/// تقرير الخصم الشهري (docs/agreed-pricing-and-names.md قسم «ج»): لكل زبون عليه علامة «وكيل» أو «صاحب خصم تشجيعي»:
/// إجمالي الخصم، عدد فواتيره، مجموع ما اشتراه قبل الخصم، ونسبة الخصم — من الفواتير المرحّلة غير المجانية دون الملغاة.
/// الخصم لكل سطر = (سعر القائمة − سعر البيع) × الكمية.
/// </summary>
public class DiscountReportService
{
    private readonly ProjectDbContext _db;
    public DiscountReportService(ProjectDbContext db) => _db = db;

    public async Task<List<DiscountReportRow>> MonthAsync(int year, int month)
    {
        var from = new DateTime(year, month, 1);
        var to = from.AddMonths(1);
        var flagged = await _db.Customers.AsNoTracking().Where(c => c.DiscountKind != null)
            .Select(c => new { c.Id, c.Name, Kind = c.DiscountKind!.Value }).ToListAsync();
        var ids = flagged.Select(c => c.Id).ToList();
        var lines = await _db.SalesInvoiceLines.AsNoTracking()
            .Where(l => ids.Contains(l.SalesInvoice.CustomerId) && l.SalesInvoice.Status == DocumentStatus.Posted && !l.SalesInvoice.IsFreeSale
                        && l.SalesInvoice.InvoiceDate >= from && l.SalesInvoice.InvoiceDate < to)
            .Select(l => new { l.SalesInvoice.CustomerId, l.SalesInvoiceId, l.QuantityInLevel, l.UnitPrice, List = l.ListUnitPrice ?? l.UnitPrice })
            .ToListAsync();
        return flagged.Select(c =>
        {
            var mine = lines.Where(l => l.CustomerId == c.Id).ToList();
            return new DiscountReportRow
            {
                CustomerId = c.Id, CustomerName = c.Name, Kind = c.Kind, InvoicesCount = mine.Select(l => l.SalesInvoiceId).Distinct().Count(),
                GrossBeforeDiscount = Math.Round(mine.Sum(l => Math.Max(l.List, l.UnitPrice) * l.QuantityInLevel), 2),
                Discount = Math.Round(mine.Sum(l => Math.Max(0, l.List - l.UnitPrice) * l.QuantityInLevel), 2)
            };
        }).OrderBy(r => r.Kind).ThenByDescending(r => r.Discount).ThenBy(r => r.CustomerName).ToList();
    }

    /// <summary>
    /// خصم عملاء الوكلاء: لكل عميل فرعي (تابع لوكيل) ما اشتراه في الشهر من الفواتير المرحّلة غير المجانية، وخصمه عن السعر العام؛
    /// مجمّعًا حسب الوكيل. لا يدخل خصمهم في صف الوكيل نفسه في تقرير الخصم الشهري.
    /// </summary>
    public async Task<List<AgentCustomerDiscountRow>> AgentCustomersMonthAsync(int year, int month)
    {
        var from = new DateTime(year, month, 1);
        var to = from.AddMonths(1);
        var lines = await _db.SalesInvoiceLines.AsNoTracking()
            .Where(l => l.SalesInvoice.Customer.CustomerType == CustomerType.SubCustomer && l.SalesInvoice.Customer.ParentAgentId != null
                        && l.SalesInvoice.Status == DocumentStatus.Posted && !l.SalesInvoice.IsFreeSale
                        && l.SalesInvoice.InvoiceDate >= from && l.SalesInvoice.InvoiceDate < to)
            .Select(l => new
            {
                Customer = l.SalesInvoice.Customer.Id, CustomerName = l.SalesInvoice.Customer.Name,
                Agent = l.SalesInvoice.Customer.ParentAgent!.Id, AgentName = l.SalesInvoice.Customer.ParentAgent.Name,
                l.SalesInvoiceId, l.QuantityInLevel, l.UnitPrice, List = l.ListUnitPrice ?? l.UnitPrice
            }).ToListAsync();
        return lines.GroupBy(l => (l.Agent, l.AgentName, l.Customer, l.CustomerName)).Select(g => new AgentCustomerDiscountRow
        {
            AgentId = g.Key.Agent, AgentName = g.Key.AgentName, CustomerId = g.Key.Customer, CustomerName = g.Key.CustomerName,
            InvoicesCount = g.Select(l => l.SalesInvoiceId).Distinct().Count(),
            GrossBeforeDiscount = Math.Round(g.Sum(l => Math.Max(l.List, l.UnitPrice) * l.QuantityInLevel), 2),
            Discount = Math.Round(g.Sum(l => Math.Max(0, l.List - l.UnitPrice) * l.QuantityInLevel), 2)
        }).OrderBy(r => r.AgentName).ThenByDescending(r => r.Discount).ThenBy(r => r.CustomerName).ToList();
    }
}
