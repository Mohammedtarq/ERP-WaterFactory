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

    private long _loadedVersion = -1;

    /// <summary>عند العودة للرئيسية: تُحدَّث اللوحة فقط إن حُفظت عمليات منذ آخر تحميل.</summary>
    public Task RefreshIfChangedAsync() => _loadedVersion == Session.DataVersion ? Task.CompletedTask : LoadAsync();

    private Task? _loading;

    /// <summary>طلبات تحديث متزامنة تشترك في تحميل واحد.</summary>
    public Task LoadAsync()
    {
        if (_loading is { IsCompleted: false }) return _loading;
        return _loading = LoadCoreAsync();
    }

    private async Task LoadCoreAsync()
    {
        _loadedVersion = Session.DataVersion;
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
            .Select(i => new { i.Id, i.InvoiceDate, i.TotalAmount, i.AmountPaidNow, i.PaymentMethod }).ToListAsync();
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

        var countByDay = invoices.GroupBy(i => i.InvoiceDate.Date).ToDictionary(g => g.Key, g => (decimal)g.Count());
        d.Columns.Add(ColumnChart.Daily("عدد الفواتير اليومية — آخر 14 يومًا", "فاتورة", days,
            ("الفواتير", ChartPalette.Series3, day => countByDay.GetValueOrDefault(day))));
        // طريقة الدفع مخزّنة نصًا — التجميع في الذاكرة
        d.Ranks.Add(RankChart.Of("مبيعات الشهر حسب طريقة الدفع", invoices.Where(i => i.InvoiceDate >= monthStart).GroupBy(i => i.PaymentMethod)
                                                                    .Select(g => (ArabicLabels.Of(g.Key), g.Sum(i => i.TotalAmount))), "د.ع", color: ChartPalette.Series2));
        var open = await db.SalesInvoices.AsNoTracking().Where(i => i.Status == DocumentStatus.Posted && !i.IsFreeSale && i.AmountRemaining > 0)
            .OrderBy(i => i.InvoiceDate).Take(7).Select(i => new { i.InvoiceNumber, Customer = i.Customer.Name, i.AmountRemaining }).ToListAsync();
        d.Ranks.Add(RankChart.Of("أقدم الفواتير غير المسددة (المتبقي)", open.Select(i => ($"{i.InvoiceNumber} — {i.Customer}", i.AmountRemaining)), "د.ع"));
    }

    // ============================ المخازن ============================
    public static async Task Warehouse(ProjectDbContext db, ModuleDashboardViewModel d)
    {
        var days = Days();
        var (f, t) = UtcRange(days[0], days[^1]);
        var moves = await db.StockTransactions.AsNoTracking().Where(x => x.TransactionDate >= f && x.TransactionDate < t)
            .Select(x => new { x.TransactionDate, x.QuantityBaseUnits, x.TransactionType, x.ItemId }).ToListAsync();
        var byWarehouse = await db.StockTransactions.GroupBy(x => x.Warehouse.Name)
            .Select(g => new { g.Key, Qty = g.Sum(x => x.QuantityBaseUnits) }).ToListAsync();
        var low = await new InventoryService(db).GetLowStockAsync();
        var monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).ToUniversalTime();
        var damagedMonth = -(await db.StockTransactions.Where(x => x.TransactionDate >= monthStart && x.QuantityBaseUnits < 0 &&
                                                               (x.TransactionType == StockTransactionType.Damaged || x.TransactionType == StockTransactionType.RepDamaged))
                                                    .SumAsync(x => (decimal?)x.QuantityBaseUnits) ?? 0);
        var warehouses = await db.Warehouses.CountAsync(w => w.IsActive && w.WarehouseType != WarehouseType.WorkInProcess);

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

        var damagedDay = moves.Where(m => m.QuantityBaseUnits < 0 && (m.TransactionType == StockTransactionType.Damaged || m.TransactionType == StockTransactionType.RepDamaged))
            .GroupBy(m => m.TransactionDate.ToLocalTime().Date).ToDictionary(g => g.Key, g => -g.Sum(m => m.QuantityBaseUnits));
        d.Columns.Add(ColumnChart.Daily("التالف اليومي — آخر 14 يومًا", "قطعة", days,
            ("التالف", ChartPalette.Series2, day => damagedDay.GetValueOrDefault(day))));
        var outByItem = real.Where(m => m.QuantityBaseUnits < 0).GroupBy(m => m.ItemId).ToDictionary(g => g.Key, g => -g.Sum(m => m.QuantityBaseUnits));
        var ids = outByItem.Keys.ToList();
        var names = await db.Items.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.ItemName);
        d.Ranks.Add(RankChart.Of("أكثر الأصناف صرفًا — آخر 14 يومًا", outByItem.Select(x => (names.GetValueOrDefault(x.Key, ""), x.Value)), "قطعة"));
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

        var entries = await db.JournalEntries.AsNoTracking().Where(j => j.EntryDate >= first && j.EntryDate <= last).Select(j => j.EntryDate).ToListAsync();
        var entriesByDay = entries.GroupBy(e => e.Date).ToDictionary(g => g.Key, g => (decimal)g.Count());
        d.Columns.Add(ColumnChart.Daily("عدد القيود اليومية — آخر 14 يومًا", "قيد", days,
            ("القيود", ChartPalette.Series3, day => entriesByDay.GetValueOrDefault(day))));
        d.Ranks.Add(RankChart.Of("المقبوضات حسب المصدر — آخر 14 يومًا", real.Where(x => x.Amount > 0).GroupBy(x => x.TxType)
                                                                         .Select(g => (ArabicLabels.Of(g.Key), g.Sum(x => x.Amount))), "د.ع", color: ChartPalette.Series2));
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
        // الحالة مخزّنة نصًا — التصفية في WHERE مدعومة، والتجميع في الذاكرة
        var openBySupplier = await db.PurchaseOrders.AsNoTracking()
            .Where(p => p.Status == PurchaseOrderStatus.Draft || p.Status == PurchaseOrderStatus.Sent || p.Status == PurchaseOrderStatus.PartiallyReceived)
            .Select(p => p.Supplier.Name).ToListAsync();
        DateTime firstDay = days[0];
        var receiptDates = await db.GoodsReceipts.AsNoTracking().Where(r => r.ReceiptDate >= firstDay).Select(r => r.ReceiptDate).ToListAsync();
        var receiptsByDay = receiptDates.GroupBy(r => r.Date).ToDictionary(g => g.Key, g => (decimal)g.Count());
        d.Columns.Add(ColumnChart.Daily("عدد مرات الاستلام اليومية — آخر 14 يومًا", "استلام", days,
            ("الاستلامات", ChartPalette.Series3, day => receiptsByDay.GetValueOrDefault(day))));
        d.Ranks.Add(RankChart.Of("أوامر الشراء المفتوحة حسب المورد", openBySupplier.GroupBy(n => n).Select(g => (g.Key, (decimal)g.Count())), "أمر", color: ChartPalette.Series2));
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

        var monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var lateMonth = await db.AttendanceRecords.AsNoTracking().Where(a => a.AttendanceDate >= monthStart && a.Status == AttendanceStatus.Late)
            .Select(a => a.Employee.FullName).ToListAsync();
        d.Ranks.Add(RankChart.Of("الأكثر تأخرًا هذا الشهر", lateMonth.GroupBy(n => n).Select(g => (g.Key, (decimal)g.Count())), "مرة", color: ChartPalette.Series2));
        var absent = att.Where(a => a.Status == AttendanceStatus.Absent).GroupBy(a => a.AttendanceDate.Date).ToDictionary(g => g.Key, g => (decimal)g.Count());
        d.Columns.Add(ColumnChart.Daily("الغياب اليومي — آخر 14 يومًا", "موظف", days,
            ("غائب", ChartPalette.Series2, day => absent.GetValueOrDefault(day))));
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

        // مستندات المندوبين: الحمولة المسندة والمرتجعة يوميًا، والتلف الميداني لكل مندوب
        DateTime first = days[0], last = days[^1];
        var repDocs = await db.StockDocumentLines.AsNoTracking()
            .Where(l => l.StockDocument.RepEmployeeId != null && l.StockDocument.DocumentDate >= (first < monthStart ? first : monthStart) && l.StockDocument.DocumentDate <= last)
            .Select(l => new { l.StockDocument.DocumentDate, l.StockDocument.DocumentType, l.QuantityBaseUnits, l.IsDamaged, Rep = l.StockDocument.RepEmployee!.FullName })
            .ToListAsync();
        var loads = repDocs.Where(x => x.DocumentType == StockDocumentType.RepLoad).GroupBy(x => x.DocumentDate.Date).ToDictionary(g => g.Key, g => g.Sum(x => x.QuantityBaseUnits));
        var returns = repDocs.Where(x => x.DocumentType == StockDocumentType.RepReturn).GroupBy(x => x.DocumentDate.Date).ToDictionary(g => g.Key, g => g.Sum(x => x.QuantityBaseUnits));
        d.Columns.Add(ColumnChart.Daily("الحمولة المسندة والمرتجعة يوميًا — آخر 14 يومًا", "قطعة", days,
            ("إسناد", ChartPalette.Series1, day => loads.GetValueOrDefault(day)),
            ("إرجاع", ChartPalette.Series2, day => returns.GetValueOrDefault(day))));
        d.Ranks.Add(RankChart.Of("التلف الميداني هذا الشهر حسب المندوب", repDocs.Where(x => x.IsDamaged && x.DocumentDate >= monthStart).GroupBy(x => x.Rep)
                                                                                .Select(g => (g.Key, g.Sum(x => x.QuantityBaseUnits))), "قطعة", color: ChartPalette.Series2));
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

        var packedMonth = await db.PackingOrders.AsNoTracking().Where(p => p.PackingDate >= monthStart)
            .Select(p => new { Item = p.PackagingLevel.Item.ItemName, Pieces = p.UnitsPackaged * p.PackagingLevel.EquivalentBaseUnits }).ToListAsync();
        d.Ranks.Add(RankChart.Of("المعبّأ هذا الشهر حسب المنتج", packedMonth.GroupBy(p => p.Item).Select(g => (g.Key, g.Sum(p => p.Pieces))), "قطعة"));
        var wip = await db.Machines.AsNoTracking()
            .Select(m => new { m.Name, Qty = db.StockTransactions.Where(t => t.WarehouseId == m.WipWarehouseId).Sum(t => (decimal?)t.QuantityBaseUnits) ?? 0 }).ToListAsync();
        d.Ranks.Add(RankChart.Of("المتبقي تحت التصنيع حسب الماكينة", wip.Select(m => (m.Name, m.Qty)), "قطعة", color: ChartPalette.Series2));

        await Lab(db, d, days);
    }

    /// <summary>
    /// لوحة المختبر (ضمن "الإنتاج والمختبر"): عدد الفحوصات، نسبة النجاح والرسوب، النتائج اليومية،
    /// والاختبارات الأكثر رسوبًا — لكل دفعة إنتاج عبر رقمها.
    /// </summary>
    public static async Task Lab(ProjectDbContext db, ModuleDashboardViewModel d, IReadOnlyList<DateTime> days)
    {
        var monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).ToUniversalTime();
        var (f, _) = UtcRange(days[0], days[^1]);
        var since = f < monthStart ? f : monthStart;
        var results = await db.QCBatchResults.AsNoTracking().Where(q => q.TestDate >= since)
            .Select(q => new { q.TestDate, q.OverallResult }).ToListAsync();
        var month = results.Where(r => r.TestDate >= monthStart).ToList();
        var passed = month.Count(r => r.OverallResult == QCOverallResult.Passed);
        var rate = month.Count == 0 ? (decimal?)null : Math.Round(100m * passed / month.Count, 1);
        d.Tile("المختبر — فحوصات الشهر", month.Count.ToString(), Icons.Star, "#8B5CF6", "دفعات فُحصت");
        d.Tile("المختبر — نسبة النجاح", rate is null ? "—" : $"{rate:0.#}%", Icons.Star, ModuleColors.Sales,
               rate is null ? "لا فحوصات هذا الشهر" : $"الرسوب {100 - rate:0.#}%");

        var passDay = results.Where(r => r.OverallResult == QCOverallResult.Passed).GroupBy(r => r.TestDate.ToLocalTime().Date).ToDictionary(g => g.Key, g => (decimal)g.Count());
        var failDay = results.Where(r => r.OverallResult == QCOverallResult.Rejected).GroupBy(r => r.TestDate.ToLocalTime().Date).ToDictionary(g => g.Key, g => (decimal)g.Count());
        d.Columns.Add(ColumnChart.Daily("المختبر — نتائج الفحص اليومية (ناجحة / مرفوضة)", "دفعة", days,
            ("ناجحة", ChartPalette.Series1, day => passDay.GetValueOrDefault(day)),
            ("مرفوضة", ChartPalette.Series2, day => failDay.GetValueOrDefault(day))));

        // النتيجة مخزّنة نصًا — التصفية في WHERE، والتجميع في الذاكرة
        var failures = await db.QCTestResultLines.AsNoTracking()
            .Where(l => l.QCBatchResult.TestDate >= monthStart && l.Result == QCLineResult.Fail)
            .Select(l => l.QualityTest.TestName).ToListAsync();
        d.Ranks.Add(RankChart.Of("المختبر — الاختبارات الأكثر رسوبًا هذا الشهر", failures.GroupBy(n => n).Select(g => (g.Key, (decimal)g.Count())), "مرة", color: ChartPalette.Series2));
    }
}
