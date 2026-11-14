using ERP.Cloud.Contracts;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>
/// ملخص لوحة المدير على الهاتف (قراءة فقط): يُبنى في المعمل ويُرفع إلى الخادم السحابي كل دقيقة.
/// قصد الخصوصية: لا رواتب ولا سلف ولا فواتير تفصيلية ولا قاعدة بيانات — أرقام مجمّعة وأسماء قليلة فقط.
/// </summary>
public class ManagerSummaryService
{
    private readonly ProjectDbContext _db;
    public ManagerSummaryService(ProjectDbContext db) => _db = db;

    public const int Top = 8;

    public async Task<ManagerSummary> BuildAsync(DateTime? now = null)
    {
        var today = (now ?? DateTime.Today).Date;
        var from30 = today.AddDays(-29);
        var factory = await _db.CompanyProfiles.AsNoTracking().Select(c => c.NameAr).FirstOrDefaultAsync();

        // ---- المبيعات: المرحّلة غير الملغاة، بلا رصيد الافتتاح ولا المجاني ----
        var sales = (await _db.SalesInvoices.AsNoTracking()
                .Where(i => i.Status == DocumentStatus.Posted && i.VoidedAt == null && !i.IsOpeningBalance && !i.IsFreeSale && i.InvoiceDate >= from30)
                .Select(i => new { i.InvoiceDate, i.TotalAmount, i.AmountPaidNow, i.SalesRepEmployeeId }).ToListAsync())
            .Select(i => new { Date = i.InvoiceDate.Date, i.TotalAmount, i.AmountPaidNow, i.SalesRepEmployeeId }).ToList();
        var todaySales = sales.Where(i => i.Date == today).ToList();
        var cash = todaySales.Sum(i => Math.Min(i.AmountPaidNow, i.TotalAmount));
        var receipts = await _db.Vouchers.AsNoTracking()
            .Where(v => !v.IsVoided && v.VoucherType == VoucherType.Receipt && v.PartyType == VoucherPartyType.Customer
                        && v.PaymentMethod != PaymentMethod.Return && v.VoucherDate >= today && v.VoucherDate < today.AddDays(1))
            .SumAsync(v => (decimal?)v.Amount) ?? 0;
        var expenses = await _db.FinanceEntries.AsNoTracking()
            .Where(e => !e.IsVoided && e.EntryDate >= today && e.EntryDate < today.AddDays(1) && e.Category.Kind != FinanceCategoryKind.OtherIncome)
            .SumAsync(e => (decimal?)e.Amount) ?? 0;
        var todayTotals = new ManagerToday(todaySales.Sum(i => i.TotalAmount), cash, todaySales.Sum(i => i.TotalAmount) - cash, cash + receipts, expenses);

        var last30 = Enumerable.Range(0, 30).Select(n => from30.AddDays(n))
            .Select(d => new ManagerDay(d.ToString("yyyy-MM-dd"), sales.Where(i => i.Date == d).Sum(i => i.TotalAmount))).ToList();

        // ---- الصناديق ----
        var boxes = await _db.CashBoxes.AsNoTracking().Where(b => b.IsActive).Select(b => new { b.Id, b.Name, b.BoxType }).ToListAsync();
        var balances = await _db.CashBoxTransactions.AsNoTracking().Where(t => !t.IsVoided && t.CashBox.IsActive)
            .GroupBy(t => t.CashBoxId).Select(g => new { g.Key, Sum = g.Sum(t => t.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum);
        var boxRows = boxes.OrderBy(b => b.BoxType switch { CashBoxType.Main => 0, CashBoxType.Home => 2, CashBoxType.Cards => 3, _ => 1 }).ThenBy(b => b.Name)
            .Select(b => new ManagerBox(b.Name, BoxTypeText(b.BoxType), balances.GetValueOrDefault(b.Id))).ToList();

        // ---- المندوبون (لوحة السيارات نفسها) ----
        var cards = await new RepVanBoardService(_db).CardsAsync(today);
        var reps = cards.OrderByDescending(c => c.LastSeenAt).ThenBy(c => c.RepName).Select(c => new ManagerRep(
            c.RepName, todaySales.Where(i => i.SalesRepEmployeeId == c.RepEmployeeId).Sum(i => i.TotalAmount), c.ExpectedCash,
            c.LastSeenAt?.ToUniversalTime(), c.PendingRequests, c.StatusText)).ToList();

        // ---- المخازن ----
        var low = await new StockAlertService(_db).LowAsync();
        var lowRows = low.Take(Top).Select(r => new ManagerLowStock(r.WarehouseName, r.ItemName, r.Balance, r.MinQuantity ?? 0)).ToList();
        var finished = await _db.StockTransactions.AsNoTracking()
            .Where(t => t.Warehouse.IsActive && t.Warehouse.WarehouseType == WarehouseType.FinishedGoods)
            .GroupBy(t => new { t.ItemId, t.Item.ItemName }).Select(g => new { g.Key.ItemName, Qty = g.Sum(t => t.QuantityBaseUnits) })
            .Where(x => x.Qty > 0).ToListAsync();
        var stock = finished.OrderByDescending(x => x.Qty).Take(Top).Select(x => new ManagerStockItem(x.ItemName, x.Qty)).ToList();

        // ---- الذمم ----
        var owed = await _db.SalesInvoices.AsNoTracking().Where(i => i.Status == DocumentStatus.Posted && i.VoidedAt == null && i.AmountRemaining > 0)
            .GroupBy(i => new { i.CustomerId, i.Customer.Name }).Select(g => new { g.Key.Name, Amount = g.Sum(i => i.AmountRemaining) }).ToListAsync();
        var debtors = owed.OrderByDescending(x => x.Amount).Take(Top).Select(x => new ManagerDebtor(x.Name, x.Amount)).ToList();

        // ---- التنبيهات ----
        var alerts = new ManagerAlerts(
            await _db.RepRequests.CountAsync(r => r.Status == RepRequestStatus.Pending),
            await _db.PaymentRequests.CountAsync(p => p.Status == PaymentRequestStatus.Pending),
            low.Count);

        return new ManagerSummary(DateTime.UtcNow, string.IsNullOrWhiteSpace(factory) ? "المعمل" : factory, todayTotals, boxRows, last30, reps, lowRows, stock,
                                  debtors, owed.Sum(x => x.Amount), alerts, await HrAsync(today));
    }

    /// <summary>الحضور والإجازات والعلاوات فقط — لا رواتب ولا سلف.</summary>
    private async Task<ManagerHr> HrAsync(DateTime today)
    {
        var rows = await _db.AttendanceRecords.AsNoTracking().Where(a => a.AttendanceDate == today && a.Employee.IsActive)
            .Select(a => new { a.Status, a.Employee.FullName, Department = a.Employee.Department != null ? a.Employee.Department.Name : "" }).ToListAsync();
        var absent = rows.Where(r => r.Status == AttendanceStatus.Absent).OrderBy(r => r.FullName).Select(r => new ManagerPerson(r.FullName, r.Department)).ToList();
        var leaves = (await _db.EmployeeLeaves.AsNoTracking().Where(l => !l.IsVoided && l.FromDate <= today && l.ToDate >= today && l.Employee.IsActive)
                .Select(l => new { l.Employee.FullName, l.ToDate }).ToListAsync())
            .GroupBy(l => l.FullName).Select(g => new ManagerPerson(g.Key, $"حتى {g.Max(l => l.ToDate):dd/MM}")).OrderBy(p => p.Name).ToList();
        var raises = (await new AnnualRaiseService(_db).UpcomingAsync(today)).Count;
        return new ManagerHr(rows.Count(r => r.Status is AttendanceStatus.Present or AttendanceStatus.Late), absent.Count,
                             rows.Count(r => r.Status == AttendanceStatus.Late), leaves.Count, absent.Take(Top).ToList(), leaves.Take(Top).ToList(), raises);
    }

    private static string BoxTypeText(CashBoxType t) => t switch
    {
        CashBoxType.Main => "رئيسي", CashBoxType.User => "مستخدم", CashBoxType.Bank => "بنك", CashBoxType.Home => "المنزل", CashBoxType.Cards => "بطاقات", _ => ""
    };

    // ============================ حساب المدير ============================

    public Task<List<ManagerAccount>> AccountsAsync() => _db.ManagerAccounts.AsNoTracking().OrderBy(a => a.Username).ToListAsync();

    public async Task<FinanceOperationResult> SaveAccountAsync(string? username, string? password, int userId, string kind = ManagerKinds.Manager)
    {
        if (!ManagerKinds.IsValid(kind)) return FinanceOperationResult.Fail("نوع الحساب غير معروف");
        if (ManagerLogin.ValidateUsername(username) is string badName) return FinanceOperationResult.Fail(badName);
        var name = ManagerLogin.NormalizeName(username);
        var row = await _db.ManagerAccounts.FirstOrDefaultAsync(a => a.Username == name);
        if (row is null && ManagerLogin.ValidatePassword(password) is string bad) return FinanceOperationResult.Fail(bad);
        if (row is not null && !string.IsNullOrEmpty(password) && ManagerLogin.ValidatePassword(password) is string bad2) return FinanceOperationResult.Fail(bad2);
        if (row is null) _db.ManagerAccounts.Add(row = new ManagerAccount { Username = name });
        if (!string.IsNullOrEmpty(password))
        {
            row.Salt = ManagerLogin.NewSalt();
            row.PasswordHash = ManagerLogin.Hash(password, row.Salt);
            row.ChangedAt = DateTime.UtcNow;
        }
        row.IsActive = true;
        row.Kind = kind;
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Edit", "ManagerAccounts", row.Id, $"حساب {(kind == ManagerKinds.Tv ? "شاشة التلفاز" : "لوحة المدير")} «{name}» {(string.IsNullOrEmpty(password) ? "فُعّل" : "حُفظ بكلمة مرور جديدة")}");
        return FinanceOperationResult.Ok();
    }

    public async Task<FinanceOperationResult> SetActiveAsync(int id, bool active, int userId)
    {
        var row = await _db.ManagerAccounts.FirstOrDefaultAsync(a => a.Id == id);
        if (row is null) return FinanceOperationResult.Fail("الحساب غير موجود");
        row.IsActive = active;
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Edit", "ManagerAccounts", id, $"حساب لوحة المدير «{row.Username}» {(active ? "فُعّل" : "أُوقف")}");
        return FinanceOperationResult.Ok();
    }

    public async Task<FinanceOperationResult> DeleteAccountAsync(int id, int userId)
    {
        var row = await _db.ManagerAccounts.FirstOrDefaultAsync(a => a.Id == id);
        if (row is null) return FinanceOperationResult.Fail("الحساب غير موجود");
        _db.ManagerAccounts.Remove(row);
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Delete", "ManagerAccounts", id, $"حُذف حساب لوحة المدير «{row.Username}»");
        return FinanceOperationResult.Ok();
    }

    public async Task<List<AgentManagerAccount>> AgentAccountsAsync() =>
        (await AccountsAsync()).Select(a => new AgentManagerAccount(a.Username, a.Salt, a.PasswordHash, a.IsActive, ManagerKinds.IsValid(a.Kind) ? a.Kind : ManagerKinds.Manager)).ToList();
}
