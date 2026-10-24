using System.Globalization;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>تجميع الفترة: بلا (الفترة كلها)، يومي، أسبوعي (يبدأ السبت — قرار المالك)، شهري.</summary>
public enum ReportPeriod { None, Day, Week, Month }

/// <summary>طلب تقرير: الفترة، ومنتج واحد أو كل المنتجات، ومندوب اختياري، وتجميع الفترة، وإظهار الكلفة (لمن له الصلاحية).</summary>
public record ReportRequest(DateTime From, DateTime To, int? ItemId = null, int? RepEmployeeId = null,
                            ReportPeriod Period = ReportPeriod.None, bool WithCost = false);

/// <summary>
/// جدول تقرير جاهز للعرض والطباعة: الأعمدة، والصفوف نصوصًا منسقة، والمجاميع. كل منتج بعبوته واسمه الموحد
/// («كارتون 40×330 الرحمة»)، فالكميات لا تُجمع بين منتجات مختلفة؛ المجاميع للمبالغ والعدد فقط.
/// </summary>
public sealed class ReportTable
{
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    public List<string> Columns { get; } = new();
    /// <summary>أعمدة رقمية (تُحاذى يمينًا وتُجمع عند الحاجة).</summary>
    public HashSet<int> NumericColumns { get; } = new();
    public List<string[]> Rows { get; } = new();
    public List<(string Label, string Value)> Totals { get; } = new();
    public string? Notes { get; init; }
}

/// <summary>
/// تقارير المبيعات والإنتاج والكلفة والربح من مصدر الحقيقة (حركات المخزون المرتبطة بالمستندات المرحّلة):
/// المباع والمرتجع والمجاني لكل منتج ومتغير، والإنتاج اليومي/الأسبوعي/الشهري بالكلفة، والكلفة والربح (للمدير فقط).
/// </summary>
public class ReportsService
{
    private readonly ProjectDbContext _db;
    public ReportsService(ProjectDbContext db) => _db = db;

    private static readonly CultureInfo En = CultureInfo.InvariantCulture;
    private static readonly StockTransactionType[] SaleTypes =
        { StockTransactionType.SalesIssue, StockTransactionType.RepSale, StockTransactionType.FreeIssue, StockTransactionType.RepFreeSale };

    public static string Money(decimal v) => v.ToString("#,0", En);
    private static string Num(decimal v) => v.ToString("#,0.##", En);

    /// <summary>بداية الأسبوع: السبت (قرار المالك).</summary>
    public static DateTime WeekStart(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek - (int)DayOfWeek.Saturday + 7) % 7));

    public static (DateTime Key, string Label) PeriodOf(DateTime localDate, ReportPeriod p) => p switch
    {
        ReportPeriod.Day => (localDate.Date, localDate.ToString("yyyy/MM/dd", En)),
        ReportPeriod.Week => (WeekStart(localDate), $"أسبوع {WeekStart(localDate):yyyy/MM/dd} – {WeekStart(localDate).AddDays(6):MM/dd}"),
        ReportPeriod.Month => (new DateTime(localDate.Year, localDate.Month, 1), localDate.ToString("yyyy/MM", En)),
        _ => (DateTime.MinValue, "")
    };

    private static string RangeText(ReportRequest r) => $"من {r.From:yyyy/MM/dd} إلى {r.To:yyyy/MM/dd}";

    /// <summary>العدد بعبوة المنتج الكبرى: «120» أو «120 + 5 قطعة».</summary>
    private static string Packs(ProductNames names, int itemId, decimal pieces)
    {
        var top = names.TopLevel(itemId);
        if (top is null || top.EquivalentBaseUnits <= 1) return Num(pieces);
        var sign = pieces < 0 ? "-" : "";
        var abs = Math.Abs(pieces);
        var n = Math.Floor(abs / top.EquivalentBaseUnits);
        var rest = abs - n * top.EquivalentBaseUnits;
        return sign + Num(n) + (rest > 0 ? $" + {Num(rest)} قطعة" : "");
    }

    // ============================ حركات البيع ============================

    private sealed record SaleRow(int InvoiceId, int ItemId, int? RecipeId, bool Free, decimal Pieces, decimal Cost);

    /// <summary>المصروف فعلًا لفواتير مرحّلة في الفترة (لا الملغاة): لكل فاتورة وصنف ومتغير التشغيلة، بالقطعة والكلفة.</summary>
    private async Task<List<SaleRow>> SalesAsync(ReportRequest r)
    {
        var from = r.From.Date;
        var to = r.To.Date.AddDays(1);
        var invoices = _db.SalesInvoices.AsNoTracking()
            .Where(i => i.Status == DocumentStatus.Posted && !i.IsOpeningBalance && i.InvoiceDate >= from && i.InvoiceDate < to);
        if (r.RepEmployeeId is int rep) invoices = invoices.Where(i => i.SalesRepEmployeeId == rep);
        var tx = _db.StockTransactions.AsNoTracking().Where(t => t.ReferenceTable == "SalesInvoices" && SaleTypes.Contains(t.TransactionType));
        if (r.ItemId is int item) tx = tx.Where(t => t.ItemId == item);
        return (await (from t in tx
                       join i in invoices on t.ReferenceId equals i.Id
                       select new { i.Id, t.ItemId, Recipe = t.Batch != null ? t.Batch.CustomRecipeId : null, i.IsFreeSale,
                                    t.QuantityBaseUnits, Cost = t.QuantityBaseUnits * (t.UnitCost ?? 0) })
                .ToListAsync())
            .GroupBy(x => (x.Id, x.ItemId, x.Recipe, x.IsFreeSale))
            .Select(g => new SaleRow(g.Key.Id, g.Key.ItemId, g.Key.Recipe, g.Key.IsFreeSale, -g.Sum(x => x.QuantityBaseUnits), -g.Sum(x => x.Cost)))
            .Where(x => x.Pieces != 0).ToList();
    }

    /// <summary>قيمة المبيع لكل فاتورة وصنف (من سطورها) موزعة على متغيرات التشغيلات المصروفة بنسبة القطع.</summary>
    private async Task<Dictionary<(int ItemId, int? RecipeId), decimal>> SalesAmountsAsync(ReportRequest r, List<SaleRow> sales)
    {
        var ids = sales.Where(s => !s.Free).Select(s => s.InvoiceId).Distinct().ToList();
        var lines = (await _db.SalesInvoiceLines.AsNoTracking().Where(l => ids.Contains(l.SalesInvoiceId))
                .GroupBy(l => new { l.SalesInvoiceId, l.ItemId }).Select(g => new { g.Key.SalesInvoiceId, g.Key.ItemId, Amount = g.Sum(l => l.LineTotal) })
                .ToListAsync())
            .ToDictionary(x => (x.SalesInvoiceId, x.ItemId), x => x.Amount);
        var result = new Dictionary<(int, int?), decimal>();
        foreach (var g in sales.Where(s => !s.Free).GroupBy(s => (s.InvoiceId, s.ItemId)))
        {
            var amount = lines.GetValueOrDefault(g.Key);
            var total = g.Sum(s => s.Pieces);
            foreach (var s in g)
                result[(s.ItemId, s.RecipeId)] = result.GetValueOrDefault((s.ItemId, s.RecipeId)) + (total == 0 ? 0 : amount * s.Pieces / total);
        }
        return result;
    }

    private sealed record ReturnRow(int ItemId, int? RecipeId, decimal Pieces, decimal Amount, decimal Cost);

    /// <summary>مرتجعات الزبائن في الفترة: القطع والقيمة، وكلفة ما عاد (تُطرح من كلفة المبيع).</summary>
    private async Task<List<ReturnRow>> ReturnsAsync(ReportRequest r)
    {
        var from = r.From.Date;
        var to = r.To.Date.AddDays(1);
        var q = _db.CustomerReturnLines.AsNoTracking().Where(l => l.CustomerReturn.ReturnDate >= from && l.CustomerReturn.ReturnDate < to);
        if (r.RepEmployeeId is int rep) q = q.Where(l => l.CustomerReturn.RepEmployeeId == rep);
        if (r.ItemId is int item) q = q.Where(l => l.ItemId == item);
        var lines = await q.Select(l => new { l.CustomerReturnId, l.ItemId, Recipe = l.Batch != null ? l.Batch.CustomRecipeId : null, l.QuantityBaseUnits, l.LineTotal })
                           .ToListAsync();
        var returnIds = lines.Select(l => (int?)l.CustomerReturnId).Distinct().ToList();
        var costs = (await _db.StockTransactions.AsNoTracking()
                .Where(t => t.ReferenceTable == "CustomerReturns" && returnIds.Contains(t.ReferenceId) && t.TransactionType == StockTransactionType.CustomerReturn)
                .Select(t => new { t.ItemId, Recipe = t.Batch != null ? t.Batch.CustomRecipeId : null, Cost = t.QuantityBaseUnits * (t.UnitCost ?? 0) })
                .ToListAsync())
            .GroupBy(x => (x.ItemId, x.Recipe)).ToDictionary(g => g.Key, g => g.Sum(x => x.Cost));
        return lines.GroupBy(l => (l.ItemId, l.Recipe))
            .Select(g => new ReturnRow(g.Key.ItemId, g.Key.Recipe, g.Sum(l => l.QuantityBaseUnits), g.Sum(l => l.LineTotal), costs.GetValueOrDefault(g.Key)))
            .ToList();
    }

    // ============================ التقارير ============================

    /// <summary>المبيعات حسب المنتج: المباع وقيمته، والمرتجع وقيمته، والمجاني، والصافي — لمنتج واحد أو للكل، ولمندوب أو للكل.</summary>
    public async Task<ReportTable> SalesByProductAsync(ReportRequest r)
    {
        var sales = await SalesAsync(r);
        var amounts = await SalesAmountsAsync(r, sales);
        var returns = await ReturnsAsync(r);
        var keys = sales.Select(s => (s.ItemId, s.RecipeId)).Concat(returns.Select(x => (x.ItemId, x.RecipeId))).Distinct().ToList();
        var names = await ProductNames.LoadAsync(_db, keys.Select(k => k.ItemId));
        var t = new ReportTable
        {
            Title = "المبيعات والمرتجع والمجاني حسب المنتج",
            Subtitle = RangeText(r),
            Notes = "الكميات بعبوة كل منتج كما في اسمه (لا تُجمع منتجات مختلفة في رقم واحد). الصافي = المباع − المرتجع."
        };
        t.Columns.AddRange(new[] { "المنتج", "المباع", "قيمة المبيع", "المرتجع", "قيمة المرتجع", "المجاني", "الصافي", "صافي القيمة" });
        foreach (var i in new[] { 1, 2, 3, 4, 5, 6, 7 }) t.NumericColumns.Add(i);
        decimal sumSales = 0, sumReturns = 0;
        foreach (var k in keys.OrderBy(k => names.Name(k.ItemId)).ThenBy(k => k.RecipeId is null ? 0 : 1).ThenBy(k => names.Variant(k.RecipeId)))
        {
            var sold = sales.Where(s => !s.Free && (s.ItemId, s.RecipeId) == k).Sum(s => s.Pieces);
            var free = sales.Where(s => s.Free && (s.ItemId, s.RecipeId) == k).Sum(s => s.Pieces);
            var ret = returns.Where(x => (x.ItemId, x.RecipeId) == k).ToList();
            var retPieces = ret.Sum(x => x.Pieces);
            var amount = Math.Round(amounts.GetValueOrDefault(k), 0);
            var retAmount = ret.Sum(x => x.Amount);
            sumSales += amount;
            sumReturns += retAmount;
            t.Rows.Add(new[]
            {
                names.Name(k.ItemId, k.RecipeId), Packs(names, k.ItemId, sold), Money(amount), Packs(names, k.ItemId, retPieces), Money(retAmount),
                Packs(names, k.ItemId, free), Packs(names, k.ItemId, sold - retPieces), Money(amount - retAmount)
            });
        }
        t.Totals.Add(("عدد المنتجات", t.Rows.Count.ToString()));
        t.Totals.Add(("قيمة المبيع", Money(sumSales)));
        t.Totals.Add(("قيمة المرتجع", Money(sumReturns)));
        t.Totals.Add(("صافي المبيعات", Money(sumSales - sumReturns)));
        return t;
    }

    /// <summary>
    /// الإنتاج اليومي/الأسبوعي/الشهري لمنتج أو للكل: الكمية المنتجة لكل منتج ومتغير، وبالكلفة (الإجمالية وكلفة العبوة) لمن يحق له.
    /// </summary>
    public async Task<ReportTable> ProductionAsync(ReportRequest r)
    {
        var fromUtc = r.From.Date.ToUniversalTime();
        var toUtc = r.To.Date.AddDays(1).ToUniversalTime();
        var q = _db.StockTransactions.AsNoTracking()
            .Where(t => t.TransactionType == StockTransactionType.ProductionOutput && t.QuantityBaseUnits > 0 && t.TransactionDate >= fromUtc && t.TransactionDate < toUtc);
        if (r.ItemId is int item) q = q.Where(t => t.ItemId == item);
        var rows = await q.Select(t => new { t.ItemId, Recipe = t.Batch != null ? t.Batch.CustomRecipeId : null, t.TransactionDate, t.QuantityBaseUnits,
                                             Cost = t.QuantityBaseUnits * (t.UnitCost ?? 0) }).ToListAsync();
        var names = await ProductNames.LoadAsync(_db, rows.Select(x => x.ItemId));
        var title = r.Period switch
        {
            ReportPeriod.Day => "الإنتاج اليومي", ReportPeriod.Week => "الإنتاج الأسبوعي", ReportPeriod.Month => "الإنتاج الشهري", _ => "الإنتاج"
        } + (r.WithCost ? " بالكلفة" : "");
        var t = new ReportTable
        {
            Title = title,
            Subtitle = RangeText(r) + (r.ItemId is null ? " — كل المنتجات" : ""),
            Notes = r.Period == ReportPeriod.Week ? "الأسبوع يبدأ السبت." : null
        };
        if (r.Period != ReportPeriod.None) t.Columns.Add("الفترة");
        t.Columns.Add("المنتج");
        t.Columns.Add("المنتَج");
        t.NumericColumns.Add(t.Columns.Count - 1);
        if (r.WithCost)
        {
            t.Columns.Add("الكلفة");
            t.NumericColumns.Add(t.Columns.Count - 1);
            t.Columns.Add("كلفة العبوة");
            t.NumericColumns.Add(t.Columns.Count - 1);
        }
        decimal totalCost = 0;
        foreach (var g in rows.GroupBy(x => (Period: PeriodOf(x.TransactionDate.ToLocalTime(), r.Period), x.ItemId, x.Recipe))
                              .OrderBy(g => g.Key.Period.Key).ThenBy(g => names.Name(g.Key.ItemId)).ThenBy(g => g.Key.Recipe is null ? 0 : 1))
        {
            var pieces = g.Sum(x => x.QuantityBaseUnits);
            var cost = g.Sum(x => x.Cost);
            totalCost += cost;
            var row = new List<string>();
            if (r.Period != ReportPeriod.None) row.Add(g.Key.Period.Label);
            row.Add(names.Name(g.Key.ItemId, g.Key.Recipe));
            row.Add(Packs(names, g.Key.ItemId, pieces));
            if (r.WithCost)
            {
                var per = names.TopLevel(g.Key.ItemId)?.EquivalentBaseUnits ?? 1;
                row.Add(Money(cost));
                row.Add(pieces == 0 ? "0" : (cost / pieces * per).ToString("#,0.##", En));
            }
            t.Rows.Add(row.ToArray());
        }
        t.Totals.Add(("عدد السطور", t.Rows.Count.ToString()));
        if (r.WithCost) t.Totals.Add(("كلفة الإنتاج", Money(totalCost)));
        return t;
    }

    /// <summary>
    /// الكلفة والربح لكل منتج (للمدير فقط — صلاحية «رؤية الكلفة والأرباح»): صافي المبيع، وكلفة المباع (بمتوسط الكلفة لحظة الصرف)
    /// بعد طرح المرتجع، والربح وهامشه، وكلفة المجاني خسارةً مستقلة.
    /// </summary>
    public async Task<ReportTable> CostAndProfitAsync(ReportRequest r)
    {
        var sales = await SalesAsync(r);
        var amounts = await SalesAmountsAsync(r, sales);
        var returns = await ReturnsAsync(r);
        var keys = sales.Select(s => (s.ItemId, s.RecipeId)).Concat(returns.Select(x => (x.ItemId, x.RecipeId))).Distinct().ToList();
        var names = await ProductNames.LoadAsync(_db, keys.Select(k => k.ItemId));
        var t = new ReportTable
        {
            Title = "الكلفة والربح حسب المنتج",
            Subtitle = RangeText(r),
            Notes = "الكلفة بمتوسط الكلفة المرجّح لحظة الصرف. الربح = صافي المبيع − صافي الكلفة. كلفة المجاني لا تدخل الربح وتظهر مستقلة."
        };
        t.Columns.AddRange(new[] { "المنتج", "صافي المباع", "صافي المبيع", "صافي الكلفة", "الربح", "الهامش %", "كلفة المجاني" });
        foreach (var i in new[] { 1, 2, 3, 4, 5, 6 }) t.NumericColumns.Add(i);
        decimal sRev = 0, sCost = 0, sFree = 0;
        foreach (var k in keys.OrderBy(k => names.Name(k.ItemId)).ThenBy(k => k.RecipeId is null ? 0 : 1).ThenBy(k => names.Variant(k.RecipeId)))
        {
            var sold = sales.Where(s => !s.Free && (s.ItemId, s.RecipeId) == k).ToList();
            var ret = returns.Where(x => (x.ItemId, x.RecipeId) == k).ToList();
            var revenue = Math.Round(amounts.GetValueOrDefault(k), 0) - ret.Sum(x => x.Amount);
            var cost = Math.Round(sold.Sum(s => s.Cost) - ret.Sum(x => x.Cost), 0);
            var freeCost = Math.Round(sales.Where(s => s.Free && (s.ItemId, s.RecipeId) == k).Sum(s => s.Cost), 0);
            var profit = revenue - cost;
            (sRev, sCost, sFree) = (sRev + revenue, sCost + cost, sFree + freeCost);
            t.Rows.Add(new[]
            {
                names.Name(k.ItemId, k.RecipeId), Packs(names, k.ItemId, sold.Sum(s => s.Pieces) - ret.Sum(x => x.Pieces)), Money(revenue), Money(cost),
                Money(profit), revenue == 0 ? "—" : (profit / revenue * 100).ToString("0.0", En), Money(freeCost)
            });
        }
        t.Totals.Add(("صافي المبيعات", Money(sRev)));
        t.Totals.Add(("صافي الكلفة", Money(sCost)));
        t.Totals.Add(("الربح الإجمالي", Money(sRev - sCost)));
        t.Totals.Add(("هامش الربح", sRev == 0 ? "—" : $"{((sRev - sCost) / sRev * 100).ToString("0.0", En)}%"));
        t.Totals.Add(("كلفة المجاني", Money(sFree)));
        return t;
    }
}
