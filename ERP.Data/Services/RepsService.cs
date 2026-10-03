using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

public class WalletRow
{
    public DateTime TransactionDate { get; init; }
    public string Description { get; init; } = "";
    public decimal AmountIn { get; init; }
    public decimal AmountOut { get; init; }
    public decimal RunningBalance { get; set; }
    public string? EntryNumber { get; init; }
}

/// <summary>
/// المندوبون (كاش فان): تحميل وإرجاع البضاعة بين المخزن والسيارة، تالف السيارة،
/// ومحفظة المندوب (العهدة النقدية) بقيودها التلقائية، وتسوية تعارضات المزامنة.
/// النقد المقبوض من مبيعات الكاش فان يدخل المحفظة تلقائيًا من ترحيل الفاتورة (09_sales_logic.sql).
/// </summary>
public class RepsService
{
    public const string FieldExpenseRule = "RepFieldExpense";     // مدين مصروفات ميدانية / دائن عهدة المندوبين
    public const string CashHandoverRule = "RepCashHandover";     // مدين الصندوق / دائن عهدة المندوبين
    public const string DebtCollectionRule = "RepDebtCollection"; // مدين عهدة المندوبين / دائن العملاء

    private readonly ProjectDbContext _db;

    public RepsService(ProjectDbContext db)
    {
        _db = db;
    }

    private async Task<(Warehouse? van, string? error)> GetVanAsync(int vanWarehouseId)
    {
        var van = await _db.Warehouses.Include(w => w.OwnerEmployee).FirstOrDefaultAsync(w => w.Id == vanWarehouseId);
        if (van is null || van.WarehouseType != WarehouseType.RepVan) return (null, "المخزن المختار ليس كاش فان مندوب");
        if (van.OwnerEmployeeId is null) return (null, "الكاش فان غير مرتبط بمندوب");
        return (van, null);
    }

    // ============================ حركة البضاعة ============================

    /// <summary>
    /// نقل بين مخزنين بسطرين متقابلين لكل تشغيلة (صادر من المصدر، وارد للهدف) في معاملة واحدة.
    /// </summary>
    private async Task<FinanceOperationResult> MoveAsync(int fromId, int toId, IReadOnlyCollection<StockLineInput> lines,
        StockTransactionType outType, StockTransactionType inType, string reference, int userId)
    {
        if (lines.Count == 0) return FinanceOperationResult.Fail("أضف صنفًا واحدًا على الأقل");
        if (fromId == toId) return FinanceOperationResult.Fail("المخزن المصدر والهدف متطابقان");

        await using var tx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        foreach (var line in lines.GroupBy(l => (l.ItemId, l.BatchId)).Select(g => new StockLineInput(g.Key.ItemId, g.Key.BatchId, g.Sum(x => x.Quantity))))
        {
            var (alloc, error) = await LedgerHelper.AllocateAsync(_db, line.ItemId, fromId, line.BatchId, line.Quantity);
            if (error is not null) return FinanceOperationResult.Fail(error);
            foreach (var (batchId, qty) in alloc)
            {
                _db.StockTransactions.Add(new StockTransaction { ItemId = line.ItemId, WarehouseId = fromId, BatchId = batchId, QuantityBaseUnits = -qty,
                                                                 TransactionType = outType, ReferenceTable = reference, CreatedByUserId = userId });
                _db.StockTransactions.Add(new StockTransaction { ItemId = line.ItemId, WarehouseId = toId, BatchId = batchId, QuantityBaseUnits = qty,
                                                                 TransactionType = inType, ReferenceTable = reference, CreatedByUserId = userId });
            }
            await _db.SaveChangesAsync();   // الحجز التالي يرى ما خُصم للتو
        }
        if (tx is not null) await tx.CommitAsync();
        return FinanceOperationResult.Ok();
    }

    public async Task<FinanceOperationResult> LoadVanAsync(int vanWarehouseId, int fromWarehouseId, IReadOnlyCollection<StockLineInput> lines, int userId)
    {
        var (_, error) = await GetVanAsync(vanWarehouseId);
        if (error is not null) return FinanceOperationResult.Fail(error);
        var source = await _db.Warehouses.FindAsync(fromWarehouseId);
        if (source is null || !source.IsSellableStock || source.WarehouseType == WarehouseType.RepVan)
            return FinanceOperationResult.Fail("التحميل يكون من مخزن قابل للبيع (ليس كاش فان آخر)");
        return await MoveAsync(fromWarehouseId, vanWarehouseId, lines, StockTransactionType.Transfer, StockTransactionType.RepLoad, "VanLoad", userId);
    }

    public async Task<FinanceOperationResult> ReturnFromVanAsync(int vanWarehouseId, int toWarehouseId, IReadOnlyCollection<StockLineInput> lines, int userId)
    {
        var (_, error) = await GetVanAsync(vanWarehouseId);
        if (error is not null) return FinanceOperationResult.Fail(error);
        return await MoveAsync(vanWarehouseId, toWarehouseId, lines, StockTransactionType.RepReturn, StockTransactionType.ReturnToWarehouse, "VanReturn", userId);
    }

    public async Task<FinanceOperationResult> RecordVanDamageAsync(int vanWarehouseId, StockLineInput line, string? notes, int userId)
    {
        var (_, error) = await GetVanAsync(vanWarehouseId);
        if (error is not null) return FinanceOperationResult.Fail(error);
        var (alloc, allocError) = await LedgerHelper.AllocateAsync(_db, line.ItemId, vanWarehouseId, line.BatchId, line.Quantity);
        if (allocError is not null) return FinanceOperationResult.Fail(allocError);
        foreach (var (batchId, qty) in alloc)
            _db.StockTransactions.Add(new StockTransaction { ItemId = line.ItemId, WarehouseId = vanWarehouseId, BatchId = batchId, QuantityBaseUnits = -qty,
                                                             TransactionType = StockTransactionType.RepDamaged, DamageReason = DamageReason.Transit,
                                                             FreeIssueRecipient = notes, ReferenceTable = "VanDamage", CreatedByUserId = userId });
        await _db.SaveChangesAsync();
        return FinanceOperationResult.Ok();
    }

    // ============================ المحفظة ============================

    public async Task<decimal> GetWalletBalanceAsync(int repEmployeeId) =>
        await _db.RepWalletTransactions.Where(w => w.EmployeeId == repEmployeeId).SumAsync(w => (decimal?)(w.AmountIn - w.AmountOut)) ?? 0;

    public async Task<List<WalletRow>> GetWalletStatementAsync(int repEmployeeId)
    {
        var rows = await _db.RepWalletTransactions.AsNoTracking().Where(w => w.EmployeeId == repEmployeeId)
            .OrderBy(w => w.TransactionDate).ThenBy(w => w.Id)
            .Select(w => new WalletRow
            {
                TransactionDate = w.TransactionDate, Description = w.Description, AmountIn = w.AmountIn, AmountOut = w.AmountOut,
                EntryNumber = w.JournalEntry != null ? w.JournalEntry.EntryNumber : null
            }).ToListAsync();
        decimal running = 0;
        foreach (var r in rows) r.RunningBalance = running += r.AmountIn - r.AmountOut;
        return rows;
    }

    private async Task<string?> ValidateRepAsync(int repEmployeeId, decimal amount)
    {
        if (amount <= 0) return "المبلغ يجب أن يكون أكبر من صفر";
        if (!await _db.Employees.AnyAsync(e => e.Id == repEmployeeId && e.IsSalesRep)) return "الموظف المختار ليس مندوب مبيعات";
        return null;
    }

    /// <summary>خروج نقد من المحفظة (مصروف ميداني أو تسليم للخزينة) — لا يُسمح بتجاوز الرصيد.</summary>
    private async Task<FinanceOperationResult> WalletOutAsync(int repId, decimal amount, DateTime date, string description, string rule, int userId, int? vehicleId = null)
    {
        var error = await ValidateRepAsync(repId, amount);
        if (error is not null) return FinanceOperationResult.Fail(error);
        var balance = await GetWalletBalanceAsync(repId);
        if (amount > balance) return FinanceOperationResult.Fail($"المبلغ أكبر من رصيد المحفظة ({balance:N0} د.ع)");

        await using var tx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        var (entry, jeError) = await LedgerHelper.PostJournalAsync(_db, rule, amount, date, JournalEntryType.AutoVoucher, description, userId,
                                                                   "RepWalletTransactions", null, "RW");
        if (jeError is not null) return FinanceOperationResult.Fail(jeError);
        var walletTx = new RepWalletTransaction { EmployeeId = repId, TransactionDate = date, Description = description,
                                                  AmountOut = amount, JournalEntry = entry, ReferenceTable = "Wallet", VehicleId = vehicleId };
        _db.RepWalletTransactions.Add(walletTx);
        await _db.SaveChangesAsync();
        // النقد المسلَّم يدخل صندوق المستخدم المستلم (أو الافتراضي)
        if (rule == CashHandoverRule)
        {
            var repName = await _db.Employees.Where(e => e.Id == repId).Select(e => e.FullName).FirstAsync();
            await new CashBoxService(_db).RecordAutoAsync(userId, CashBoxTxType.RepHandover, amount, date, "RepWalletTransactions", walletTx.Id,
                                                          repName, $"تسليم نقد من المندوب {repName}", entry!.Id);
        }
        if (tx is not null) await tx.CommitAsync();
        return FinanceOperationResult.Ok();
    }

    public Task<FinanceOperationResult> RecordFieldExpenseAsync(int repId, decimal amount, string description, DateTime date, int userId, int? vehicleId = null) =>
        WalletOutAsync(repId, amount, date, $"مصروف ميداني: {description}", FieldExpenseRule, userId, vehicleId);

    public Task<FinanceOperationResult> RecordCashHandoverAsync(int repId, decimal amount, DateTime date, int userId) =>
        WalletOutAsync(repId, amount, date, "تسليم نقد للخزينة", CashHandoverRule, userId);

    /// <summary>
    /// تحصيل دين عميل بواسطة المندوب: سند قبض على العميل (يظهر في كشف حسابه ويخفض مديونيته)
    /// + دخول المبلغ لمحفظة المندوب + قيد (مدين عهدة المندوبين / دائن العملاء).
    /// </summary>
    public async Task<FinanceOperationResult> RecordDebtCollectionAsync(int repId, int customerId, decimal amount, DateTime date, int userId)
    {
        var error = await ValidateRepAsync(repId, amount);
        if (error is not null) return FinanceOperationResult.Fail(error);
        var customer = await _db.Customers.FindAsync(customerId);
        if (customer is null) return FinanceOperationResult.Fail("العميل غير موجود");

        await using var tx = await _db.Database.BeginTransactionAsync();
        var description = $"تحصيل من العميل {customer.Name}";
        var (entry, jeError) = await LedgerHelper.PostJournalAsync(_db, DebtCollectionRule, amount, date, JournalEntryType.AutoVoucher, description, userId,
                                                                   "Vouchers", null, "RW");
        if (jeError is not null) return FinanceOperationResult.Fail(jeError);

        var voucherCount = await _db.Vouchers.CountAsync();
        var voucher = new Voucher
        {
            VoucherNumber = $"RV-{voucherCount + 1:D5}", VoucherType = VoucherType.Receipt, PartyType = VoucherPartyType.Customer,
            PartyId = customerId, Amount = amount, PaymentMethod = PaymentMethod.Cash, VoucherDate = date.Date,
            Notes = $"تحصيل بواسطة المندوب", JournalEntry = entry, CreatedByUserId = userId
        };
        _db.Vouchers.Add(voucher);
        await _db.SaveChangesAsync();
        _db.RepWalletTransactions.Add(new RepWalletTransaction { EmployeeId = repId, TransactionDate = date, Description = description, AmountIn = amount,
                                                                 JournalEntryId = entry!.Id, ReferenceTable = "Vouchers", ReferenceId = voucher.Id });
        await _db.SaveChangesAsync();
        await new CustomerAccountService(_db).SyncAsync(customerId);
        await tx.CommitAsync();
        return FinanceOperationResult.Ok();
    }

    // ============================ تعارضات المزامنة ============================

    /// <summary>
    /// تسوية تعارض (رصيد سالب ناتج عن عمل المندوب دون اتصال): اختياريًا تُضاف حركة تسوية تعيد
    /// رصيد الصنف في السيارة إلى صفر، ثم يُغلق التعارض مع الملاحظات واسم من سوّاه.
    /// </summary>
    public async Task<FinanceOperationResult> ResolveConflictAsync(int conflictId, string notes, bool zeroNegativeBalance, int userId)
    {
        if (string.IsNullOrWhiteSpace(notes)) return FinanceOperationResult.Fail("اكتب سبب/طريقة التسوية");
        var c = await _db.SyncConflicts.Include(x => x.StockTransaction).FirstOrDefaultAsync(x => x.Id == conflictId);
        if (c is null) return FinanceOperationResult.Fail("التعارض غير موجود");
        if (c.Status == SyncConflictStatus.Resolved) return FinanceOperationResult.Fail("هذا التعارض مسوّى مسبقًا");

        if (zeroNegativeBalance)
        {
            var wh = c.StockTransaction.WarehouseId;
            var balance = await LedgerHelper.BalanceAsync(_db, c.ItemId, wh, c.BatchId);
            if (balance < 0)
                _db.StockTransactions.Add(new StockTransaction { ItemId = c.ItemId, WarehouseId = wh, BatchId = c.BatchId, QuantityBaseUnits = -balance,
                                                                 TransactionType = StockTransactionType.SyncConflictAdjustment, ReferenceTable = "SyncConflicts",
                                                                 ReferenceId = c.Id, FreeIssueRecipient = notes, CreatedByUserId = userId });
        }
        c.Status = SyncConflictStatus.Resolved;
        c.ResolutionNotes = notes.Trim();
        c.ResolvedByUserId = userId;
        await _db.SaveChangesAsync();
        return FinanceOperationResult.Ok();
    }
}
