using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

public enum AlertLevel { Info, Warning, Danger }

/// <summary>
/// تنبيه في جرس الشريط الجانبي: عنوان وتفصيل وعدد، والشاشة التي تفتح عند الضغط (الوحدة + اسم نوع الشاشة).
/// </summary>
public record AlertItem(string Key, string Title, string Detail, int Count, AlertLevel Level, string ModuleCode, string Section)
{
    public string Color => Level switch { AlertLevel.Danger => "#DC2626", AlertLevel.Warning => "#D97706", _ => "#2563EB" };
}

/// <summary>
/// التنبيهات حسب صلاحيات المستخدم: كل واحد يرى ما يخص عمله فقط —
/// أمين المخزن: طلبات التحميل للتجهيز، والسيارات التي لم تُسلّم مرتجعها، والأصناف تحت حد التنبيه؛
/// المشتريات: أوامر بانتظار الاستلام؛ المعتمِد: طلبات المندوبين (ومنها المرتجع)؛ الإنتاج: ما بيع قبل إنتاجه.
/// </summary>
public class AlertsService
{
    private readonly ProjectDbContext _db;
    public AlertsService(ProjectDbContext db) => _db = db;

    private static string Ago(DateTime localDate)
    {
        var days = (DateTime.Today - localDate.Date).Days;
        return days <= 0 ? "اليوم" : days == 1 ? "منذ أمس" : $"منذ {days} يوم";
    }

    private static string Names(IEnumerable<string> names, int total)
    {
        var shown = names.Take(3).ToList();
        return string.Join("، ", shown) + (total > shown.Count ? $" و{total - shown.Count} غيرها" : "");
    }

    public async Task<List<AlertItem>> ForAsync(UserPermissions p)
    {
        var alerts = new List<AlertItem>();
        var warehouse = p.CanView(ModuleCode.Warehouse);
        var reps = p.CanView(ModuleCode.Reps);

        // 1) طلبات تحميل بانتظار التجهيز (أمين المخزن)
        if (warehouse || reps)
        {
            var pending = await _db.RepLoadOrders.AsNoTracking().Where(o => o.Status == RepLoadOrderStatus.Pending)
                .OrderBy(o => o.LoadDate).Select(o => new { o.OrderNumber, o.LoadDate, Rep = o.RepEmployee.FullName }).ToListAsync();
            if (pending.Count > 0)
            {
                var oldest = pending[0];
                alerts.Add(new("load-orders", $"طلبات تحميل بانتظار التجهيز: {pending.Count}",
                    $"{Names(pending.Select(o => o.Rep).Distinct(), pending.Select(o => o.Rep).Distinct().Count())} — أقدمها {oldest.OrderNumber} {Ago(oldest.LoadDate)}",
                    pending.Count, oldest.LoadDate.Date < DateTime.Today ? AlertLevel.Danger : AlertLevel.Warning,
                    warehouse ? ModuleCode.Warehouse : ModuleCode.Reps, warehouse ? "PrepareLoadOrdersSectionViewModel" : "LoadOrdersSectionViewModel"));
            }

            // 2) سيارات حُمّلت قبل اليوم ولم تُسوَّ: مرتجعها لم يُستلم بعد
            var vans = await _db.Warehouses.AsNoTracking().Where(w => w.IsActive && w.WarehouseType == WarehouseType.RepVan && w.OwnerEmployeeId != null)
                .Select(w => new { w.Id, RepId = w.OwnerEmployeeId!.Value, Rep = w.OwnerEmployee!.FullName }).ToListAsync();
            var waiting = new List<(string rep, DateTime since)>();
            foreach (var v in vans)
            {
                var lastLoad = await _db.RepLoadOrders.Where(o => o.VanWarehouseId == v.Id && o.Status == RepLoadOrderStatus.Prepared && o.LoadDate < DateTime.Today)
                                                      .MaxAsync(o => (DateTime?)o.LoadDate);
                if (lastLoad is null) continue;
                var settled = await _db.RepSettlements.AnyAsync(s => s.RepEmployeeId == v.RepId && s.SettlementDate >= lastLoad);
                if (settled) continue;
                var pieces = await _db.StockTransactions.Where(t => t.WarehouseId == v.Id).SumAsync(t => (decimal?)t.QuantityBaseUnits) ?? 0;
                if (pieces > 0) waiting.Add((v.Rep, lastLoad.Value));
            }
            if (waiting.Count > 0)
                alerts.Add(new("van-returns", $"مرتجعات بانتظار الاستلام: {waiting.Count} سيارة",
                    $"{Names(waiting.OrderBy(w => w.since).Select(w => w.rep), waiting.Count)} — حُمّلت ولم تُسوَّ (تسوية اليوم تستلم المرتجع)",
                    waiting.Count, waiting.Any(w => (DateTime.Today - w.since.Date).Days > 1) ? AlertLevel.Danger : AlertLevel.Warning,
                    reps ? ModuleCode.Reps : ModuleCode.Warehouse, reps ? "RepSettlementSectionViewModel" : "WarehouseRepVansSectionViewModel"));
        }

        // 3) طلبات المندوبين بانتظار الاعتماد (المرتجع والمصروف)
        if (p.Has(SpecialPermission.RepApproval))
        {
            var kinds = await _db.RepRequests.AsNoTracking().Where(r => r.Status == RepRequestStatus.Pending)
                .GroupBy(r => r.Kind).Select(g => new { g.Key, Count = g.Count(), Oldest = g.Min(r => r.ReceivedAt) }).ToListAsync();
            var total = kinds.Sum(k => k.Count);
            if (total > 0)
            {
                var parts = kinds.OrderByDescending(k => k.Count).Select(k => $"{RepAppService.KindText(k.Key)} {k.Count}");
                var oldest = kinds.Min(k => k.Oldest).ToLocalTime();
                alerts.Add(new("rep-requests", $"طلبات مندوبين بانتظار الاعتماد: {total}", $"{string.Join("، ", parts)} — أقدمها {Ago(oldest)}",
                    total, oldest.Date < DateTime.Today ? AlertLevel.Danger : AlertLevel.Warning, ModuleCode.Reps, "RepRequestsSectionViewModel"));
            }
        }

        // 4) أوامر شراء بانتظار الاستلام
        if (p.CanView(ModuleCode.Suppliers))
        {
            var orders = await _db.PurchaseOrders.AsNoTracking()
                .Where(o => o.Status == PurchaseOrderStatus.Sent || o.Status == PurchaseOrderStatus.PartiallyReceived)
                .OrderBy(o => o.OrderDate).Select(o => new { o.PONumber, o.OrderDate, Supplier = o.Supplier.Name, o.Status }).ToListAsync();
            if (orders.Count > 0)
                alerts.Add(new("purchase-receipt", $"أوامر شراء بانتظار الاستلام: {orders.Count}",
                    $"{Names(orders.Select(o => o.Supplier).Distinct(), orders.Select(o => o.Supplier).Distinct().Count())}"
                    + (orders.Any(o => o.Status == PurchaseOrderStatus.PartiallyReceived) ? $" — منها {orders.Count(o => o.Status == PurchaseOrderStatus.PartiallyReceived)} مستلم جزئيًا" : ""),
                    orders.Count, AlertLevel.Info, ModuleCode.Suppliers, "GoodsReceiptSectionViewModel"));
        }

        // 5) نقص المواد: أصناف عند حد التنبيه أو تحته
        if (warehouse)
        {
            var low = await new InventoryService(_db).GetLowStockAsync();
            if (low.Count > 0)
                alerts.Add(new("low-stock", $"نقص مواد: {low.Count} صنف عند حد التنبيه أو تحته",
                    Names(low.Select(x => $"{x.item.ItemName} ({x.balance:#,0.##} من {x.item.MinStockAlertLevel:#,0.##})"), low.Count),
                    low.Count, low.Any(x => x.balance <= 0) ? AlertLevel.Danger : AlertLevel.Warning, ModuleCode.Warehouse, "StockAlertsSectionViewModel"));
        }

        // 6) ما بيع قبل تسجيل إنتاجه (نقص في المنتج التام)
        if (p.CanView(ModuleCode.Production))
        {
            var open = await new PendingProductionService(_db).OpenAsync();
            if (open.Count > 0)
                alerts.Add(new("production-shortages", $"بانتظار الإنتاج: {open.Count} سطر",
                    $"{Names(open.Select(o => o.ItemName).Distinct(), open.Select(o => o.ItemName).Distinct().Count())} — بيع قبل تسجيل إنتاجه",
                    open.Count, AlertLevel.Warning, ModuleCode.Production, "PendingProductionSectionViewModel"));
        }

        // 7) تعارضات مزامنة المندوبين (أرصدة سالبة)
        if (reps)
        {
            var conflicts = await _db.SyncConflicts.CountAsync(c => c.Status == SyncConflictStatus.Pending);
            if (conflicts > 0)
                alerts.Add(new("sync-conflicts", $"تعارضات مزامنة: {conflicts}", "أرصدة سالبة من عمل المندوبين تحتاج تسوية",
                    conflicts, AlertLevel.Danger, ModuleCode.Reps, "SyncConflictsSectionViewModel"));
        }

        return alerts.OrderByDescending(a => a.Level).ToList();
    }
}
