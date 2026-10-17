using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <param name="Damaged">من الكمية: ما عاد تالفًا (بنفس الوحدة).</param>
public record CustomerReturnLineInput(int ItemId, int PackagingLevelId, decimal Quantity, decimal Damaged = 0);

/// <param name="WarehouseId">يعود إليه السليم: سيارة المندوب (والرد النقدي من محفظته) أو مخزن المنتج التام.</param>
public record CustomerReturnRequest(int CustomerId, int WarehouseId, DateTime Date, CustomerReturnSettlement Settlement, string Reason,
                                    IReadOnlyList<CustomerReturnLineInput> Lines, int UserId, int? RepRequestId = null);

/// <summary>سطر في قائمة المرتجعات.</summary>
public record CustomerReturnRow(int Id, string ReturnNumber, DateTime ReturnDate, string CustomerName, string WarehouseName, string? RepName,
                                CustomerReturnSettlement Settlement, decimal TotalAmount, string Reason, string Items)
{
    public string SettlementText => Settlement == CustomerReturnSettlement.Debt ? "خصم من الدين" : "رد نقدي";
}

/// <summary>
/// مرتجع الزبون: بضاعة اشتراها وأعادها، بسعر آخر بيع له (فلا تُرد قيمة أعلى مما دفع)، ولا يتجاوز ما اشتراه ناقص ما أعاده سابقًا.
/// السليم يعود إلى المخزن/السيارة بتشغيلته، والتالف لمخزن التالف. القيمة إما تُخصم من دينه (سند «مرتجع» يدخل كشفه وتوزيع
/// الدفعات) أو تُرد نقدًا من محفظة المندوب. القيد: مدين إيرادات المبيعات / دائن العملاء (أو عهدة المندوبين). كله أو لا شيء.
/// </summary>
public class CustomerReturnService
{
    public const string DebtRule = "CustomerReturnDebt";        // مدين إيرادات المبيعات / دائن العملاء
    public const string RepCashRule = "CustomerReturnRepCash";  // مدين إيرادات المبيعات / دائن عهدة المندوبين

    private readonly ProjectDbContext _db;
    public CustomerReturnService(ProjectDbContext db) => _db = db;

    private static readonly StockTransactionType[] SaleIssues = { StockTransactionType.SalesIssue, StockTransactionType.RepSale };

    public async Task<(FinanceOperationResult result, CustomerReturn? created)> CreateAsync(CustomerReturnRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Reason)) return (FinanceOperationResult.Fail("اكتب سبب المرتجع"), null);
        var lines = r.Lines.Where(l => l.Quantity > 0).ToList();
        if (lines.Count == 0) return (FinanceOperationResult.Fail("أضف صنفًا واحدًا على الأقل بكمية أكبر من صفر"), null);
        if (lines.Any(l => l.Damaged < 0 || l.Damaged > l.Quantity)) return (FinanceOperationResult.Fail("التالف جزء من الكمية المرتجعة (بين صفر والكمية)"), null);
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == r.CustomerId);
        if (customer is null) return (FinanceOperationResult.Fail("اختر الزبون"), null);
        var warehouse = await _db.Warehouses.AsNoTracking().FirstOrDefaultAsync(w => w.Id == r.WarehouseId && w.IsActive);
        if (warehouse is null || warehouse.WarehouseType is not (WarehouseType.RepVan or WarehouseType.FinishedGoods or WarehouseType.Main or WarehouseType.Sub))
            return (FinanceOperationResult.Fail("اختر سيارة المندوب أو مخزن المنتج التام"), null);
        var repId = warehouse.WarehouseType == WarehouseType.RepVan ? warehouse.OwnerEmployeeId : null;
        if (r.Settlement == CustomerReturnSettlement.Cash && repId is null)
            return (FinanceOperationResult.Fail("الرد النقدي يكون من محفظة المندوب: اختر سيارته، أو اختر «خصم من الدين»"), null);
        if (await new PeriodLockService(_db).GetLockedThroughAsync() is DateTime locked && r.Date.Date <= locked)
            return (FinanceOperationResult.Fail($"الفترة حتى {locked:yyyy-MM-dd} مقفلة. لا يُسجَّل مرتجع بتاريخ داخلها إلا بعد فتحها من المدير."), null);

        // ---- السعر والتشغيلة والسقف لكل سطر ----
        var levelIds = lines.Select(l => l.PackagingLevelId).Distinct().ToList();
        var levels = await _db.ItemPackagingLevels.AsNoTracking().Where(l => levelIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id);
        if (lines.Any(l => !levels.TryGetValue(l.PackagingLevelId, out var lv) || lv.ItemId != l.ItemId))
            return (FinanceOperationResult.Fail("وحدة التعبئة لا تخص الصنف"), null);
        var planned = new List<(CustomerReturnLineInput input, ItemPackagingLevel level, decimal pieces, decimal damagedPieces, decimal price, int? batchId, decimal cost)>();
        foreach (var g in lines.GroupBy(l => l.ItemId))
        {
            var item = await _db.Items.AsNoTracking().FirstAsync(i => i.Id == g.Key);
            var sold = await _db.SalesInvoiceLines.Where(l => l.ItemId == g.Key && l.SalesInvoice.CustomerId == customer.Id
                                                              && l.SalesInvoice.Status == DocumentStatus.Posted && !l.SalesInvoice.IsFreeSale)
                                                  .SumAsync(l => (decimal?)l.QuantityBaseUnits) ?? 0;
            var returned = await _db.CustomerReturnLines.Where(l => l.ItemId == g.Key && l.CustomerReturn.CustomerId == customer.Id)
                                                        .SumAsync(l => (decimal?)l.QuantityBaseUnits) ?? 0;
            var asked = g.Sum(l => l.Quantity * levels[l.PackagingLevelId].EquivalentBaseUnits);
            if (asked > sold - returned)
                return (FinanceOperationResult.Fail($"«{item.ItemName}»: المرتجع {asked:N0} قطعة أكثر مما اشتراه الزبون ولم يُرجعه ({Math.Max(0, sold - returned):N0} قطعة)"), null);
            var lastBatch = await _db.StockTransactions.AsNoTracking()
                .Where(t => t.ItemId == g.Key && t.ReferenceTable == "SalesInvoices" && SaleIssues.Contains(t.TransactionType) && t.BatchId != null
                            && _db.SalesInvoices.Any(i => i.Id == t.ReferenceId && i.CustomerId == customer.Id))
                .OrderByDescending(t => t.Id).Select(t => t.BatchId).FirstOrDefaultAsync();
            foreach (var l in g)
            {
                var level = levels[l.PackagingLevelId];
                // سعر آخر بيع لهذا الزبون بالوحدة نفسها، وإلا بسعر قطعته الأخير، وإلا سعر القائمة
                var price = await _db.SalesInvoiceLines.AsNoTracking()
                    .Where(x => x.ItemId == g.Key && x.PackagingLevelId == level.Id && x.SalesInvoice.CustomerId == customer.Id
                                && x.SalesInvoice.Status == DocumentStatus.Posted && !x.SalesInvoice.IsFreeSale)
                    .OrderByDescending(x => x.Id).Select(x => (decimal?)x.UnitPrice).FirstOrDefaultAsync();
                if (price is null)
                {
                    var perPiece = await _db.SalesInvoiceLines.AsNoTracking()
                        .Where(x => x.ItemId == g.Key && x.SalesInvoice.CustomerId == customer.Id && x.SalesInvoice.Status == DocumentStatus.Posted
                                    && !x.SalesInvoice.IsFreeSale && x.QuantityBaseUnits > 0)
                        .OrderByDescending(x => x.Id).Select(x => (decimal?)(x.LineTotal / x.QuantityBaseUnits)).FirstOrDefaultAsync();
                    price = Math.Round((perPiece ?? item.SalePrice) * level.EquivalentBaseUnits, 2);
                }
                planned.Add((l, level, l.Quantity * level.EquivalentBaseUnits, l.Damaged * level.EquivalentBaseUnits, price.Value, lastBatch, item.CostPrice ?? 0));
            }
        }
        var total = Math.Round(planned.Sum(p => p.input.Quantity * p.price), 2);
        if (total <= 0) return (FinanceOperationResult.Fail("قيمة المرتجع صفر — راجع الأسعار"), null);
        Warehouse? damagedWh = null;
        if (planned.Any(p => p.damagedPieces > 0))
        {
            damagedWh = await _db.Warehouses.AsNoTracking().Where(w => w.IsActive && w.WarehouseType == WarehouseType.Damaged).OrderBy(w => w.Id).FirstOrDefaultAsync();
            if (damagedWh is null) return (FinanceOperationResult.Fail("لا يوجد مخزن تالف لاستلام التالف — عرّفه من المخازن"), null);
        }
        var reps = new RepsService(_db);
        if (r.Settlement == CustomerReturnSettlement.Cash && await reps.GetWalletBalanceAsync(repId!.Value) is var wallet && wallet < total)
            return (FinanceOperationResult.Fail($"الرد النقدي ({total:N0}) أكبر من النقد مع المندوب ({wallet:N0} د.ع) — اختر «خصم من الدين»"), null);

        await using var tx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        var count = await _db.CustomerReturns.CountAsync(x => x.ReturnDate.Year == r.Date.Year);
        var ret = new CustomerReturn
        {
            ReturnNumber = $"CR-{r.Date.Year}-{count + 1:D5}", CustomerId = customer.Id, WarehouseId = warehouse.Id, RepEmployeeId = repId,
            ReturnDate = r.Date.Date, Settlement = r.Settlement, TotalAmount = total, Reason = r.Reason.Trim(), RepRequestId = r.RepRequestId,
            CreatedByUserId = r.UserId
        };
        foreach (var p in planned)
            ret.Lines.Add(new CustomerReturnLine
            {
                ItemId = p.input.ItemId, PackagingLevelId = p.level.Id, QuantityInLevel = p.input.Quantity, DamagedInLevel = p.input.Damaged,
                QuantityBaseUnits = p.pieces, UnitPrice = p.price, LineTotal = Math.Round(p.input.Quantity * p.price, 2), BatchId = p.batchId
            });
        _db.CustomerReturns.Add(ret);
        await _db.SaveChangesAsync();

        var description = $"مرتجع {ret.ReturnNumber} من {customer.Name}: {ret.Reason}";
        var (entry, jeError) = await LedgerHelper.PostJournalAsync(_db, r.Settlement == CustomerReturnSettlement.Debt ? DebtRule : RepCashRule, total, r.Date,
                                                                   JournalEntryType.AutoVoucher, description, r.UserId, "CustomerReturns", ret.Id, "CR");
        if (jeError is not null) return (FinanceOperationResult.Fail(jeError), null);
        ret.JournalEntry = entry!;

        if (r.Settlement == CustomerReturnSettlement.Debt)
        {
            // سند «مرتجع»: يدخل كشف الزبون وتوزيع الدفعات على فواتيره (بلا حركة صندوق)
            var voucher = new Voucher
            {
                VoucherNumber = $"RV-{await _db.Vouchers.CountAsync() + 1:D5}", VoucherType = VoucherType.Receipt, PartyType = VoucherPartyType.Customer,
                PartyId = customer.Id, Amount = total, PaymentMethod = PaymentMethod.Return, VoucherDate = r.Date.Date,
                Notes = $"مرتجع بضاعة {ret.ReturnNumber}", JournalEntry = entry, CreatedByUserId = r.UserId
            };
            _db.Vouchers.Add(voucher);
            await _db.SaveChangesAsync();
            ret.VoucherId = voucher.Id;
        }
        else
            _db.RepWalletTransactions.Add(new RepWalletTransaction
            {
                EmployeeId = repId!.Value, TransactionDate = r.Date, Description = $"رد نقدي لمرتجع {ret.ReturnNumber} — {customer.Name}", AmountOut = total,
                JournalEntry = entry, ReferenceTable = "CustomerReturns", ReferenceId = ret.Id
            });

        var stamp = r.Date.Date == DateTime.Today ? DateTime.UtcNow : DateTime.SpecifyKind(r.Date.Date.AddHours(12), DateTimeKind.Utc);
        foreach (var p in planned)
        {
            var good = p.pieces - p.damagedPieces;
            if (good > 0)
                _db.StockTransactions.Add(new StockTransaction
                {
                    ItemId = p.input.ItemId, WarehouseId = warehouse.Id, BatchId = p.batchId, QuantityBaseUnits = good, UnitCost = p.cost,
                    TransactionType = StockTransactionType.CustomerReturn, ReferenceTable = "CustomerReturns", ReferenceId = ret.Id,
                    TransactionDate = stamp, CreatedByUserId = r.UserId
                });
            if (p.damagedPieces > 0)
                _db.StockTransactions.Add(new StockTransaction
                {
                    ItemId = p.input.ItemId, WarehouseId = damagedWh!.Id, BatchId = p.batchId, QuantityBaseUnits = p.damagedPieces, UnitCost = p.cost,
                    TransactionType = StockTransactionType.CustomerReturn, ReferenceTable = "CustomerReturns", ReferenceId = ret.Id,
                    TransactionDate = stamp, CreatedByUserId = r.UserId
                });
        }
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(r.UserId, "Post", "CustomerReturns", ret.Id,
            $"{ret.ReturnNumber} — {customer.Name} — {total:N0} د.ع — {(r.Settlement == CustomerReturnSettlement.Debt ? "خصم من الدين" : "رد نقدي")}");
        if (r.Settlement == CustomerReturnSettlement.Debt) await new CustomerAccountService(_db).SyncAsync(customer.Id);
        if (tx is not null) await tx.CommitAsync();
        return (FinanceOperationResult.Ok(), ret);
    }

    public async Task<List<CustomerReturnRow>> ListAsync(DateTime from, DateTime to, int? customerId = null) =>
        (await _db.CustomerReturns.AsNoTracking()
            .Where(x => x.ReturnDate >= from.Date && x.ReturnDate <= to.Date && (customerId == null || x.CustomerId == customerId))
            .OrderByDescending(x => x.Id)
            .Select(x => new
            {
                x.Id, x.ReturnNumber, x.ReturnDate, Customer = x.Customer.Name, Warehouse = x.Warehouse.Name,
                Rep = x.RepEmployee != null ? x.RepEmployee.FullName : null, x.Settlement, x.TotalAmount, x.Reason,
                Lines = x.Lines.Select(l => new { l.QuantityInLevel, l.DamagedInLevel, l.PackagingLevel.LevelName, l.Item.ItemName }).ToList()
            }).ToListAsync())
        .Select(x => new CustomerReturnRow(x.Id, x.ReturnNumber, x.ReturnDate, x.Customer, x.Warehouse, x.Rep, x.Settlement, x.TotalAmount, x.Reason,
            string.Join("، ", x.Lines.Select(l => $"{l.QuantityInLevel:#,0.##} {l.LevelName} {l.ItemName}{(l.DamagedInLevel > 0 ? $" (تالف {l.DamagedInLevel:#,0.##})" : "")}"))))
        .ToList();
}
