using ERP.Cloud.Contracts;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>
/// ملخص شاشة التلفاز في المعمل: المبيعات والإنتاج والجودة والمندوبون والمخازن والحضور حسب القسم — أرقام عامة بلا أسماء موظفين.
/// لا ديون ولا أرصدة صناديق ولا رواتب: ما يُعرض أمام الجميع يجب ألا يكون حساسًا.
/// </summary>
public class TvSummaryService
{
    private readonly ProjectDbContext _db;
    public TvSummaryService(ProjectDbContext db) => _db = db;

    public async Task<TvSummary> BuildAsync(DateTime? now = null)
    {
        var today = (now ?? DateTime.Today).Date;
        var from10 = today.AddDays(-9);
        var factory = await _db.CompanyProfiles.AsNoTracking().Select(c => c.NameAr).FirstOrDefaultAsync();
        var message = await _db.CloudSyncSettings.AsNoTracking().Select(c => c.TvMessage).FirstOrDefaultAsync();

        var sales = (await _db.SalesInvoices.AsNoTracking()
                .Where(i => i.Status == DocumentStatus.Posted && i.VoidedAt == null && !i.IsOpeningBalance && !i.IsFreeSale && i.InvoiceDate >= from10)
                .Select(i => new { i.InvoiceDate, i.TotalAmount }).ToListAsync())
            .Select(i => (Date: i.InvoiceDate.Date, i.TotalAmount)).ToList();

        // الإنتاج: أوامر اليوم، وما أُنتج اليوم بالاسم الموحد (عدد العبوات الكبرى)
        var (utcFrom, utcTo) = (today.ToUniversalTime(), today.AddDays(1).ToUniversalTime());
        var orders = await _db.ProductionOrders.AsNoTracking().Where(o => o.CreatedAt >= utcFrom && o.CreatedAt < utcTo && o.Status != ProductionOrderStatus.Cancelled)
            .Select(o => o.Status).ToListAsync();
        var output = await _db.StockTransactions.AsNoTracking()
            .Where(t => t.TransactionType == StockTransactionType.ProductionOutput && t.TransactionDate >= utcFrom && t.TransactionDate < utcTo)
            .GroupBy(t => new { t.ItemId, RecipeId = t.Batch != null ? t.Batch.CustomRecipeId : null })
            .Select(g => new { g.Key.ItemId, g.Key.RecipeId, Pieces = g.Sum(t => t.QuantityBaseUnits) }).ToListAsync();
        var names = await ProductNames.LoadAsync(_db, output.Select(o => o.ItemId).Distinct());
        var produced = output.Where(o => o.Pieces > 0).Select(o =>
        {
            var per = names.TopLevel(o.ItemId)?.EquivalentBaseUnits ?? 1;
            return new TvProduced(names.Name(o.ItemId, o.RecipeId), Math.Floor(o.Pieces / per));
        }).OrderByDescending(p => p.Packs).Take(6).ToList();

        var qc = await _db.QCBatchResults.AsNoTracking().Where(q => q.TestDate >= utcFrom && q.TestDate < utcTo).Select(q => q.OverallResult).ToListAsync();

        // المندوبون: مبيعات اليوم وحالة الاتصال (متصل خلال 10 دقائق)
        var cards = await new RepVanBoardService(_db).CardsAsync(today);
        var todaySales = await _db.SalesInvoices.AsNoTracking()
            .Where(i => i.Status == DocumentStatus.Posted && i.VoidedAt == null && !i.IsFreeSale && i.InvoiceDate >= today && i.InvoiceDate < today.AddDays(1) && i.SalesRepEmployeeId != null)
            .Select(i => new { i.SalesRepEmployeeId, i.TotalAmount }).ToListAsync();
        var reps = cards.OrderBy(c => c.RepName).Select(c => new TvRep(c.RepName, todaySales.Where(s => s.SalesRepEmployeeId == c.RepEmployeeId).Sum(s => s.TotalAmount),
            c.LastSeenAt is { } seen && DateTime.UtcNow - seen.ToUniversalTime() < TimeSpan.FromMinutes(10))).ToList();

        var low = (await new StockAlertService(_db).LowAsync()).Take(6).Select(r => new TvLowStock(r.ItemName, r.Balance, r.MinQuantity ?? 0)).ToList();

        // الحضور: أعداد حسب القسم بلا أسماء
        var employees = await _db.Employees.AsNoTracking().Where(e => e.IsActive && !e.IsTemporary && e.EndOfServiceDate == null)
            .Select(e => new { e.Id, Department = e.Department != null ? e.Department.Name : "بلا قسم" }).ToListAsync();
        var attendance = await _db.AttendanceRecords.AsNoTracking().Where(a => a.AttendanceDate == today).Select(a => new { a.EmployeeId, a.Status }).ToListAsync();
        var byEmployee = attendance.GroupBy(a => a.EmployeeId).ToDictionary(g => g.Key, g => g.Last().Status);
        bool Present(int id) => byEmployee.TryGetValue(id, out var st) && st is AttendanceStatus.Present or AttendanceStatus.Late;
        var departments = employees.GroupBy(e => e.Department).OrderBy(g => g.Key)
            .Select(g => new TvDepartment(g.Key, g.Count(e => Present(e.Id)), g.Count())).ToList();
        var onLeave = await _db.EmployeeLeaves.AsNoTracking().Where(l => !l.IsVoided && l.FromDate <= today && l.ToDate >= today).Select(l => l.EmployeeId).Distinct().CountAsync();

        return new TvSummary(DateTime.UtcNow, string.IsNullOrWhiteSpace(factory) ? "المعمل" : factory, string.IsNullOrWhiteSpace(message) ? null : message.Trim(),
            sales.Where(s => s.Date == today).Sum(s => s.TotalAmount),
            Enumerable.Range(0, 10).Select(n => from10.AddDays(n)).Select(d => new TvDay(d.ToString("yyyy-MM-dd"), sales.Where(s => s.Date == d).Sum(s => s.TotalAmount))).ToList(),
            orders.Count(s => s == ProductionOrderStatus.Completed), orders.Count, produced,
            qc.Count(r => r == QCOverallResult.Passed), qc.Count, reps, low,
            attendance.Count(a => a.Status is AttendanceStatus.Present or AttendanceStatus.Late), attendance.Count(a => a.Status == AttendanceStatus.Late),
            attendance.Count(a => a.Status == AttendanceStatus.Absent), onLeave, departments);
    }
}
