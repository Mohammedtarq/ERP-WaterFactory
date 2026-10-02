using System.Collections.ObjectModel;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Shell;

/// <summary>
/// لوحة متخصصة أعلى "الرئيسية" في كل وحدة: مؤشرات اليوم ورسوم النشاط اليومي (آخر 14 يومًا) والترتيب.
/// تُحمَّل في الخلفية عند فتح الوحدة، ويمكن تحديثها.
/// </summary>
public class ModuleDashboardViewModel : SessionViewModel
{
    private readonly Func<ProjectDbContext, ModuleDashboardViewModel, Task> _loader;
    private string? _error;

    public ModuleDashboardViewModel(AppSession s, IDialogService d, string moduleCode, Func<ProjectDbContext, ModuleDashboardViewModel, Task> loader)
        : base(s, d, moduleCode)
    {
        _loader = loader;
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
    }

    public ObservableCollection<DashboardTile> Tiles { get; } = new();
    public ObservableCollection<ColumnChart> Columns { get; } = new();
    public ObservableCollection<RankChart> Ranks { get; } = new();
    public AsyncRelayCommand RefreshCommand { get; }
    public string? Error { get => _error; private set => SetProperty(ref _error, value); }

    /// <summary>آخر 14 يومًا حتى اليوم.</summary>
    public static IReadOnlyList<DateTime> Days(int count = 14) =>
        Enumerable.Range(0, count).Select(i => DateTime.Today.AddDays(i - count + 1)).ToList();

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            Tiles.Clear();
            Columns.Clear();
            Ranks.Clear();
            await using var db = Session.NewDb();
            await _loader(db, this);
            Error = null;
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            Error = "تعذّر تحميل لوحة القسم: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Tile(string title, string value, string glyph, string color, string hint = "") => Tiles.Add(new DashboardTile(title, value, glyph, color, hint));

    private static string Money(decimal v) => $"{v:N0} د.ع";
    private static (DateTime fromUtc, DateTime toUtc) UtcRange(DateTime fromDay, DateTime toDay) =>
        (fromDay.Date.ToUniversalTime(), toDay.Date.AddDays(1).ToUniversalTime());

    // ============================ المبيعات ============================
    public static async Task Sales(ProjectDbContext db, ModuleDashboardViewModel d)
    {
        var days = Days();
        var from = days[0];
        var monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var since = from < monthStart ? from : monthStart;
        var invoices = await db.SalesInvoices.AsNoTracking()
            .Where(i => i.Status == DocumentStatus.Posted && !i.IsFreeSale && i.InvoiceDate >= since)
            .Select(i => new { i.Id, i.InvoiceDate, i.TotalAmount, i.AmountPaidNow }).ToListAsync();
        var today = invoices.Where(i => i.InvoiceDate == DateTime.Today).ToList();
        var receivables = (await new SalesService(db).GetCustomerBalancesAsync()).Where(b => b.Balance > 0).ToList();

        d.Tile("مبيعات اليوم", Money(today.Sum(i => i.TotalAmount)), Icons.Sales, ModuleColors.Sales, $"{today.Count} فاتورة مرحّلة");
        d.Tile("المقبوض اليوم", Money(today.Sum(i => i.AmountPaidNow)), Icons.Currency, ModuleColors.Finance, "نقدًا وإلكترونيًا عند البيع");
        d.Tile("مبيعات الشهر", Money(invoices.Where(i => i.InvoiceDate >= monthStart).Sum(i => i.TotalAmount)), Icons.Calendar, "#0EA5E9",
               $"{invoices.Count(i => i.InvoiceDate >= monthStart)} فاتورة");
        d.Tile("ذمم العملاء", Money(receivables.Sum(b => b.Balance)), Icons.People, ModuleColors.Suppliers, $"{receivables.Count} عميل مدين");

        var byDay = invoices.GroupBy(i => i.InvoiceDate.Date).ToDictionary(g => g.Key, g => g.Sum(i => i.TotalAmount));
        d.Columns.Add(ColumnChart.Daily("المبيعات اليومية — آخر 14 يومًا", "د.ع", days,
            ("المبيعات", ChartPalette.Series1, day => byDay.GetValueOrDefault(day))));

        var monthIds = invoices.Where(i => i.InvoiceDate >= monthStart).Select(i => i.Id).ToList();
        var topItems = await db.SalesInvoiceLines.AsNoTracking().Where(l => monthIds.Contains(l.SalesInvoiceId))
            .GroupBy(l => l.Item.ItemName).Select(g => new { g.Key, Qty = g.Sum(l => l.QuantityBaseUnits) }).ToListAsync();
        d.Ranks.Add(RankChart.Of("أكثر الأصناف مبيعًا هذا الشهر", topItems.Select(x => (x.Key, x.Qty)), "قطعة"));
        d.Ranks.Add(RankChart.Of("أعلى أرصدة العملاء", receivables.Select(b => (b.Name, b.Balance)), "د.ع"));
    }

    // ============================ المخازن ============================
    public static async Task Warehouse(ProjectDbContext db, ModuleDashboardViewModel d)
    {
        var days = Days();
        var (f, t) = UtcRange(days[0], days[^1]);
        var moves = await db.StockTransactions.AsNoTracking().Where(x => x.TransactionDate >= f && x.TransactionDate < t)
            .Select(x => new { x.TransactionDate, x.QuantityBaseUnits, x.TransactionType }).ToListAsync();
        var byWarehouse = await db.StockTransactions.GroupBy(x => x.Warehouse.Name)
            .Select(g => new { g.Key, Qty = g.Sum(x => x.QuantityBaseUnits) }).ToListAsync();
        var low = await new InventoryService(db).GetLowStockAsync();
        var monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).ToUniversalTime();
        var damagedMonth = -(await db.StockTransactions.Where(x => x.TransactionDate >= monthStart && x.QuantityBaseUnits < 0 &&
                                                               (x.TransactionType == StockTransactionType.Damaged || x.TransactionType == StockTransactionType.RepDamaged))
                                                    .SumAsync(x => (decimal?)x.QuantityBaseUnits) ?? 0);
        var warehouses = await db.Warehouses.CountAsync(w => w.IsActive);

        d.Tile("المخازن الفعّالة", warehouses.ToString(), Icons.Store, ModuleColors.Warehouse, "لكل مخزن تبويب خاص");
        d.Tile("إجمالي المخزون", $"{byWarehouse.Sum(x => x.Qty):N0} قطعة", Icons.Stock, "#6366F1", "كل المخازن");
        d.Tile("تحت حد التنبيه", low.Count.ToString(), Icons.Alert, "#EF4444", "أصناف تحتاج تزويدًا");
        d.Tile("تالف هذا الشهر", $"{damagedMonth:N0} قطعة", Icons.Adjust, "#F59E0B", "من كل المخازن");

        // المناقلة بين مخزنين لها طرفان؛ تُستبعد لتعكس الأعمدة الوارد والصادر الفعليين للمصنع
        var real = moves.Where(m => m.TransactionType != StockTransactionType.Transfer && m.TransactionType != StockTransactionType.RepLoad
                                    && m.TransactionType != StockTransactionType.RepReturn).ToList();
        var inDay = real.Where(m => m.QuantityBaseUnits > 0).GroupBy(m => m.TransactionDate.ToLocalTime().Date).ToDictionary(g => g.Key, g => g.Sum(m => m.QuantityBaseUnits));
        var outDay = real.Where(m => m.QuantityBaseUnits < 0).GroupBy(m => m.TransactionDate.ToLocalTime().Date).ToDictionary(g => g.Key, g => -g.Sum(m => m.QuantityBaseUnits));
        d.Columns.Add(ColumnChart.Daily("الوارد والصادر اليومي — آخر 14 يومًا", "قطعة", days,
            ("وارد", ChartPalette.Series1, day => inDay.GetValueOrDefault(day)),
            ("صادر", ChartPalette.Series2, day => outDay.GetValueOrDefault(day))));
        d.Ranks.Add(RankChart.Of("المخزون حسب المخزن", byWarehouse.Select(x => (x.Key, x.Qty)), "قطعة"));
        d.Ranks.Add(RankChart.Of("النقص عن حد التنبيه", low.Select(x => (x.item.ItemName, x.item.MinStockAlertLevel!.Value - x.balance)), "قطعة", color: ChartPalette.Series2));
    }

    // ============================ المالية ============================
    public static async Task Finance(ProjectDbContext db, ModuleDashboardViewModel d)
    {
        var days = Days();
        var boxes = await db.CashBoxes.AsNoTracking().Where(b => b.IsActive).Select(b => new { b.Id, b.Name }).ToListAsync();
        var balances = await db.CashBoxTransactions.Where(x => !x.IsVoided).GroupBy(x => x.CashBoxId)
            .Select(g => new { g.Key, Sum = g.Sum(x => x.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum);
        DateTime first = days[0], last = days[^1];
        var tx = await db.CashBoxTransactions.AsNoTracking().Where(x => !x.IsVoided && x.TxDate >= first && x.TxDate <= last)
            .Select(x => new { x.TxDate, x.Amount, x.TxType }).ToListAsync();
        // المناقلات الداخلية بين الصناديق لا تُعد مقبوضات ولا مدفوعات
        var real = tx.Where(x => x.TxType != CashBoxTxType.TransferIn && x.TxType != CashBoxTxType.TransferOut).ToList();
        var todayIn = real.Where(x => x.TxDate == DateTime.Today && x.Amount > 0).Sum(x => x.Amount);
        var todayOut = -real.Where(x => x.TxDate == DateTime.Today && x.Amount < 0).Sum(x => x.Amount);
        var entriesToday = await db.JournalEntries.CountAsync(j => j.EntryDate == DateTime.Today);

        d.Tile("رصيد الصناديق", Money(boxes.Sum(b => balances.GetValueOrDefault(b.Id))), Icons.Currency, "#0EA5E9", $"{boxes.Count} صندوق");
        d.Tile("مقبوضات اليوم", Money(todayIn), Icons.Receive, ModuleColors.Sales, "داخل الصناديق");
        d.Tile("مدفوعات اليوم", Money(todayOut), Icons.Voucher, "#EF4444", "خارج الصناديق");
        d.Tile("قيود اليوم", entriesToday.ToString(), Icons.Journal, ModuleColors.Finance, "يدوية وتلقائية");

        var inDay = real.Where(x => x.Amount > 0).GroupBy(x => x.TxDate.Date).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        var outDay = real.Where(x => x.Amount < 0).GroupBy(x => x.TxDate.Date).ToDictionary(g => g.Key, g => -g.Sum(x => x.Amount));
        d.Columns.Add(ColumnChart.Daily("المقبوضات والمدفوعات اليومية — آخر 14 يومًا", "د.ع", days,
            ("مقبوضات", ChartPalette.Series1, day => inDay.GetValueOrDefault(day)),
            ("مدفوعات", ChartPalette.Series2, day => outDay.GetValueOrDefault(day))));
        d.Ranks.Add(RankChart.Of("أرصدة الصناديق", boxes.Select(b => (b.Name, balances.GetValueOrDefault(b.Id))), "د.ع"));
    }

    // ============================ الموردون ============================
    public static async Task Suppliers(ProjectDbContext db, ModuleDashboardViewModel d)
    {
        var days = Days();
        var monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var receipts = await db.GoodsReceiptLines.AsNoTracking()
            .Where(l => l.GoodsReceipt.ReceiptDate >= (days[0] < monthStart ? days[0] : monthStart))
            .Select(l => new { l.GoodsReceipt.ReceiptDate, Supplier = l.GoodsReceipt.Supplier.Name, Value = l.QuantityReceived * l.UnitCost }).ToListAsync();
        var openOrders = await db.PurchaseOrders.CountAsync(p => p.Status == PurchaseOrderStatus.Draft || p.Status == PurchaseOrderStatus.Sent
                                                                  || p.Status == PurchaseOrderStatus.PartiallyReceived);
        d.Tile("أوامر شراء مفتوحة", openOrders.ToString(), Icons.Order, ModuleColors.Suppliers, "بانتظار الاستلام");
        d.Tile("مشتريات الشهر", Money(receipts.Where(r => r.ReceiptDate >= monthStart).Sum(r => r.Value)), Icons.Receive, "#6366F1", "بضاعة مستلمة");
        d.Tile("الموردون الفعّالون", (await db.Suppliers.CountAsync(s => s.IsActive)).ToString(), Icons.Suppliers, "#0EA5E9", "");

        var byDay = receipts.GroupBy(r => r.ReceiptDate.Date).ToDictionary(g => g.Key, g => g.Sum(r => r.Value));
        d.Columns.Add(ColumnChart.Daily("قيمة البضاعة المستلمة يوميًا — آخر 14 يومًا", "د.ع", days,
            ("المستلم", ChartPalette.Series1, day => byDay.GetValueOrDefault(day))));
        d.Ranks.Add(RankChart.Of("أكبر الموردين هذا الشهر", receipts.Where(r => r.ReceiptDate >= monthStart).GroupBy(r => r.Supplier)
                                                                  .Select(g => (g.Key, g.Sum(r => r.Value))), "د.ع"));
    }

    // ============================ الموارد البشرية ============================
    public static async Task Hr(ProjectDbContext db, ModuleDashboardViewModel d)
    {
        var days = Days();
        var employees = await db.Employees.CountAsync(e => e.IsActive);
        DateTime first = days[0], last = days[^1];
        var att = await db.AttendanceRecords.AsNoTracking().Where(a => a.AttendanceDate >= first && a.AttendanceDate <= last)
            .Select(a => new { a.AttendanceDate, a.Status }).ToListAsync();
        var today = att.Where(a => a.AttendanceDate.Date == DateTime.Today).ToList();
        d.Tile("الموظفون", employees.ToString(), Icons.People, ModuleColors.HR, "فعّالون");
        d.Tile("حاضرون اليوم", today.Count(a => a.Status == AttendanceStatus.Present).ToString(), Icons.Clock, ModuleColors.Sales, "في الموعد");
        d.Tile("متأخرون اليوم", today.Count(a => a.Status == AttendanceStatus.Late).ToString(), Icons.Alert, "#F59E0B", "");
        d.Tile("غائبون اليوم", today.Count(a => a.Status == AttendanceStatus.Absent).ToString(), Icons.Lock, "#EF4444",
               today.Count == 0 ? "لم يُسجَّل حضور اليوم" : "");

        var present = att.Where(a => a.Status is AttendanceStatus.Present or AttendanceStatus.Late).GroupBy(a => a.AttendanceDate.Date).ToDictionary(g => g.Key, g => (decimal)g.Count());
        var late = att.Where(a => a.Status == AttendanceStatus.Late).GroupBy(a => a.AttendanceDate.Date).ToDictionary(g => g.Key, g => (decimal)g.Count());
        d.Columns.Add(ColumnChart.Daily("الحضور اليومي — آخر 14 يومًا", "موظف", days,
            ("حاضر", ChartPalette.Series1, day => present.GetValueOrDefault(day)),
            ("متأخر", ChartPalette.Series2, day => late.GetValueOrDefault(day))));

        var runs = await db.PayrollRuns.AsNoTracking().OrderByDescending(r => r.PeriodYear).ThenByDescending(r => r.PeriodMonth).Take(6)
            .Select(r => new { r.PeriodYear, r.PeriodMonth, Net = r.Lines.Where(l => l.Currency == "IQD").Sum(l => (decimal?)l.NetSalary) ?? 0 }).ToListAsync();
        d.Ranks.Add(RankChart.Of("صافي الرواتب (دينار) لآخر الأشهر", runs.Select(r => ($"{r.PeriodMonth:00}/{r.PeriodYear}", r.Net)), "د.ع", 6));
    }

    // ============================ المندوبون ============================
    public static async Task Reps(ProjectDbContext db, ModuleDashboardViewModel d)
    {
        var days = Days();
        var monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var sales = await db.SalesInvoices.AsNoTracking()
            .Where(i => i.Status == DocumentStatus.Posted && i.SalesRepEmployeeId != null && i.InvoiceDate >= (days[0] < monthStart ? days[0] : monthStart))
            .Select(i => new { i.InvoiceDate, Rep = i.SalesRepEmployee!.FullName, i.TotalAmount }).ToListAsync();
        var wallets = await db.RepWalletTransactions.GroupBy(w => w.Employee.FullName)
            .Select(g => new { g.Key, Balance = g.Sum(w => w.AmountIn - w.AmountOut) }).ToListAsync();
        var vans = await db.Warehouses.CountAsync(w => w.IsActive && w.WarehouseType == WarehouseType.RepVan);
        var pending = await db.SyncConflicts.CountAsync(c => c.Status == SyncConflictStatus.Pending);

        d.Tile("مبيعات المندوبين اليوم", Money(sales.Where(s => s.InvoiceDate == DateTime.Today).Sum(s => s.TotalAmount)), Icons.Sales, ModuleColors.Reps, "");
        d.Tile("أرصدة المحافظ", Money(wallets.Sum(w => w.Balance)), Icons.Currency, "#0EA5E9", "نقد لم يُسلَّم بعد");
        d.Tile("سيارات الكاش فان", vans.ToString(), Icons.Truck, "#6366F1", "");
        d.Tile("تعارضات معلّقة", pending.ToString(), Icons.Alert, pending > 0 ? "#EF4444" : "#94A3B8", "بانتظار التسوية");

        var byDay = sales.GroupBy(s => s.InvoiceDate.Date).ToDictionary(g => g.Key, g => g.Sum(s => s.TotalAmount));
        d.Columns.Add(ColumnChart.Daily("مبيعات المندوبين اليومية — آخر 14 يومًا", "د.ع", days,
            ("المبيعات", ChartPalette.Series1, day => byDay.GetValueOrDefault(day))));
        d.Ranks.Add(RankChart.Of("مبيعات كل مندوب هذا الشهر", sales.Where(s => s.InvoiceDate >= monthStart).GroupBy(s => s.Rep)
                                                                    .Select(g => (g.Key, g.Sum(s => s.TotalAmount))), "د.ع"));
        d.Ranks.Add(RankChart.Of("أرصدة محافظ المندوبين", wallets.Select(w => (w.Key, w.Balance)), "د.ع", color: ChartPalette.Series2));
    }

    // ============================ الإنتاج ============================
    public static async Task Production(ProjectDbContext db, ModuleDashboardViewModel d)
    {
        var days = Days();
        var (f, t) = UtcRange(days[0], days[^1]);
        var packed = await db.PackingOrders.AsNoTracking().Where(p => p.PackingDate >= f && p.PackingDate < t)
            .Select(p => new { p.PackingDate, Pieces = p.UnitsPackaged * p.PackagingLevel.EquivalentBaseUnits }).ToListAsync();
        var orders = await db.ProductionOrders.AsNoTracking().Select(o => o.Status).ToListAsync();
        var monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).ToUniversalTime();
        var qc = await db.QCBatchResults.AsNoTracking().Where(q => q.TestDate >= monthStart).Select(q => q.OverallResult).ToListAsync();

        var todayPieces = packed.Where(p => p.PackingDate.ToLocalTime().Date == DateTime.Today).Sum(p => p.Pieces);
        d.Tile("قيد التشغيل", orders.Count(s => s == ProductionOrderStatus.InProgress).ToString(), Icons.Factory, ModuleColors.Production, "أوامر جارية");
        d.Tile("معبّأ اليوم", $"{todayPieces:N0} قطعة", Icons.Item, ModuleColors.Sales, "دخل مخزن المنتج التام");
        d.Tile("فحوصات ناجحة (الشهر)", qc.Count(r => r == QCOverallResult.Passed).ToString(), Icons.Star, "#0EA5E9", "");
        d.Tile("دفعات مرفوضة (الشهر)", qc.Count(r => r == QCOverallResult.Rejected).ToString(), Icons.Alert, "#EF4444", "");

        var byDay = packed.GroupBy(p => p.PackingDate.ToLocalTime().Date).ToDictionary(g => g.Key, g => g.Sum(p => p.Pieces));
        d.Columns.Add(ColumnChart.Daily("الإنتاج المعبّأ يوميًا — آخر 14 يومًا", "قطعة", days,
            ("المعبّأ", ChartPalette.Series1, day => byDay.GetValueOrDefault(day))));
        d.Ranks.Add(RankChart.Of("أوامر الإنتاج حسب المرحلة", orders.GroupBy(s => s).Select(g => (ArabicLabels.Of(g.Key), (decimal)g.Count())), "أمر"));
    }
}
