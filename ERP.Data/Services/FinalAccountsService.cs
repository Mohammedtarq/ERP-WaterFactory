using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>هامش منتج في الشهر: المبيع بالقطعة، الإيراد، قيمته بسعر القائمة، الخصم، الكلفة، والهامش.</summary>
public class ProductMarginRow
{
    public int ItemId { get; init; }
    public string ItemName { get; init; } = "";
    public decimal PiecesSold { get; set; }
    public decimal Revenue { get; set; }
    public decimal ListValue { get; set; }
    public decimal Discount => ListValue - Revenue;
    public decimal Cost { get; set; }
    public decimal Margin => Revenue - Cost;
    public decimal MarginPercent => Revenue == 0 ? 0 : Math.Round(Margin / Revenue * 100, 1);
    public decimal CostPerPiece => PiecesSold == 0 ? 0 : Math.Round(Cost / PiecesSold, 2);
}

public record FinalAccountLine(string Section, string Label, decimal Amount, bool IsTotal = false);

/// <summary>الحسابات الختامية لشهر.</summary>
public class FinalAccountsReport
{
    public int Year { get; init; }
    public int Month { get; init; }
    public List<ProductMarginRow> Products { get; init; } = new();
    public List<FinalAccountLine> Lines { get; init; } = new();
    public decimal Revenue { get; init; }
    public decimal ListValue { get; init; }
    public decimal Discounts => ListValue - Revenue;
    public decimal LoadingRevenue { get; init; }
    public decimal MaterialsCost { get; init; }
    public decimal GrossProfit => Revenue + LoadingRevenue - MaterialsCost;
    public decimal OperatingExpenses { get; init; }
    public decimal Salaries { get; init; }
    public decimal RepFieldExpenses { get; init; }
    public decimal Losses { get; init; }
    public decimal OperatingProfit => GrossProfit - OperatingExpenses - Salaries - RepFieldExpenses - Losses;
    public decimal NonOperatingExpenses { get; init; }
    public decimal OtherIncome { get; init; }
    public decimal NetProfit => OperatingProfit - NonOperatingExpenses + OtherIncome;
    public decimal PiecesSold { get; init; }
    /// <summary>كلفة القنينة = (المواد + المصاريف التشغيلية والرواتب ومصاريف المندوبين) ÷ القطع المباعة. غير التشغيلي لا يدخل.</summary>
    public decimal CostPerPiece => PiecesSold == 0 ? 0 : Math.Round((MaterialsCost + OperatingExpenses + Salaries + RepFieldExpenses) / PiecesSold, 2);
    public decimal OverheadPerPiece => PiecesSold == 0 ? 0 : Math.Round((OperatingExpenses + Salaries + RepFieldExpenses) / PiecesSold, 2);
}

public class WorkingCapitalSnapshot
{
    public DateTime Date { get; init; }
    public decimal Capital { get; init; }
    public DateTime? CapitalEffectiveFrom { get; init; }
    public decimal NetAssets { get; init; }
    public decimal HomeBoxBalance { get; init; }
    /// <summary>صافي الموجودات داخل العمل (بلا صندوق المنزل).</summary>
    public decimal NetAssetsInBusiness => NetAssets - HomeBoxBalance;
    /// <summary>الفائض عن رأس المال التشغيلي = ربح متحقق قابل للإخراج.</summary>
    public decimal Surplus => NetAssetsInBusiness - Capital;
    public decimal CashInBusinessBoxes { get; init; }
    /// <summary>ما يمكن نقله نقدًا الآن: الفائض محدودًا بالنقد المتاح.</summary>
    public decimal TransferableNow => Math.Max(0, Math.Min(Surplus, CashInBusinessBoxes));
}

public class CostSimulationRow
{
    public int ItemId { get; init; }
    public string ItemName { get; init; } = "";
    public decimal SalePrice { get; init; }
    public decimal CurrentMaterialCost { get; init; }
    public decimal SimulatedMaterialCost { get; init; }
    public decimal OverheadPerPiece { get; init; }
    public decimal CurrentCost => CurrentMaterialCost + OverheadPerPiece;
    public decimal SimulatedCost => SimulatedMaterialCost + OverheadPerPiece;
    public decimal SimulatedMargin => SalePrice - SimulatedCost;
    public decimal CostChange => SimulatedCost - CurrentCost;
}

public class DailyCashBoxRow
{
    public int BoxId { get; init; }
    public string BoxName { get; init; } = "";
    public CashBoxType BoxType { get; init; }
    public decimal Opening { get; init; }
    public decimal In { get; init; }
    public decimal Out { get; init; }
    public decimal Closing => Opening + In - Out;
    public List<(string Label, decimal In, decimal Out)> ByType { get; init; } = new();
}

/// <summary>
/// الحسابات الختامية الشهرية، والمطابقة مع رأس المال التشغيلي، ومحاكاة الكلفة، والتقرير اليومي للصناديق.
/// </summary>
public class FinalAccountsService
{
    private readonly ProjectDbContext _db;
    public FinalAccountsService(ProjectDbContext db) => _db = db;

    // ============================ الحسابات الختامية ============================

    public async Task<FinalAccountsReport> MonthAsync(int year, int month)
    {
        var from = new DateTime(year, month, 1);
        var to = from.AddMonths(1);

        // ---- المبيعات: فواتير مرحّلة غير مجانية بتاريخ الشهر ----
        var invoices = await _db.SalesInvoices.AsNoTracking()
            .Where(i => i.Status == DocumentStatus.Posted && !i.IsFreeSale && i.InvoiceDate >= from && i.InvoiceDate < to)
            .Select(i => new { i.Id, i.LoadingSuppliesAmount }).ToListAsync();
        var invoiceIds = invoices.Select(i => i.Id).ToList();
        var lines = await _db.SalesInvoiceLines.AsNoTracking().Where(l => invoiceIds.Contains(l.SalesInvoiceId))
            .Select(l => new { l.ItemId, l.Item.ItemName, l.Item.CostPrice, l.QuantityBaseUnits, l.QuantityInLevel, l.LineTotal, List = l.ListUnitPrice ?? l.UnitPrice })
            .ToListAsync();
        var issued = await _db.StockTransactions.AsNoTracking()
            .Where(t => t.ReferenceTable == "SalesInvoices" && t.ReferenceId != null && invoiceIds.Contains(t.ReferenceId.Value)
                        && (t.TransactionType == StockTransactionType.SalesIssue || t.TransactionType == StockTransactionType.RepSale))
            .GroupBy(t => t.ItemId)
            .Select(g => new { ItemId = g.Key, Pieces = -g.Sum(t => t.QuantityBaseUnits), Cost = -g.Sum(t => t.QuantityBaseUnits * (t.UnitCost ?? 0)) })
            .ToDictionaryAsync(x => x.ItemId);

        var products = lines.GroupBy(l => new { l.ItemId, l.ItemName, l.CostPrice }).Select(g =>
        {
            var pieces = g.Sum(l => l.QuantityBaseUnits);
            var costed = issued.GetValueOrDefault(g.Key.ItemId);
            // ما بيع بانتظار الإنتاج بلا حركة مخزنية بعد: بمتوسط الكلفة الحالي
            var uncosted = Math.Max(0, pieces - (costed?.Pieces ?? 0));
            return new ProductMarginRow
            {
                ItemId = g.Key.ItemId, ItemName = g.Key.ItemName, PiecesSold = pieces, Revenue = g.Sum(l => l.LineTotal),
                ListValue = Math.Round(g.Sum(l => l.List * l.QuantityInLevel), 2),
                Cost = Math.Round((costed?.Cost ?? 0) + uncosted * (g.Key.CostPrice ?? 0), 2)
            };
        }).ToList();

        // ---- مرتجعات الزبائن: تُطرح من الإيراد والكمية، والسليم العائد للمخزن يُطرح من الكلفة (التالف يبقى كلفة) ----
        var returns = await _db.CustomerReturnLines.AsNoTracking()
            .Where(l => l.CustomerReturn.ReturnDate >= from && l.CustomerReturn.ReturnDate < to)
            .GroupBy(l => new { l.ItemId, l.Item.ItemName })
            .Select(g => new { g.Key.ItemId, g.Key.ItemName, Pieces = g.Sum(l => l.QuantityBaseUnits), Value = g.Sum(l => l.LineTotal) }).ToListAsync();
        var returnedCost = await _db.StockTransactions.AsNoTracking()
            .Where(t => t.TransactionType == StockTransactionType.CustomerReturn && t.Warehouse.WarehouseType != WarehouseType.Damaged
                        && _db.CustomerReturns.Any(cr => cr.Id == t.ReferenceId && cr.ReturnDate >= from && cr.ReturnDate < to))
            .GroupBy(t => t.ItemId).Select(g => new { ItemId = g.Key, Cost = g.Sum(t => t.QuantityBaseUnits * (t.UnitCost ?? 0)) })
            .ToDictionaryAsync(x => x.ItemId, x => x.Cost);
        foreach (var ret in returns)
        {
            var row = products.FirstOrDefault(p => p.ItemId == ret.ItemId);
            if (row is null) products.Add(row = new ProductMarginRow { ItemId = ret.ItemId, ItemName = ret.ItemName });
            row.PiecesSold -= ret.Pieces;
            row.Revenue -= ret.Value;
            row.ListValue -= ret.Value;
            row.Cost = Math.Round(row.Cost - returnedCost.GetValueOrDefault(ret.ItemId), 2);
        }
        products = products.OrderByDescending(p => p.Revenue).ToList();

        // ---- المصروفات من شاشة المصروف ----
        var entries = await _db.FinanceEntries.AsNoTracking().Where(e => !e.IsVoided && e.EntryDate >= from && e.EntryDate < to)
            .GroupBy(e => new { e.Category.Name, e.Category.Kind })
            .Select(g => new { g.Key.Name, g.Key.Kind, Amount = g.Sum(e => e.Amount) }).ToListAsync();

        // ---- من الدفتر: مصاريف المندوبين الميدانية، وسندات الصرف العامة خارج شاشة المصروف ----
        async Task<decimal> LedgerAsync(string code, Func<IQueryable<JournalEntryLine>, IQueryable<JournalEntryLine>>? filter = null)
        {
            var q = _db.JournalEntryLines.Where(l => l.Account.AccountCode == code && l.JournalEntry.IsPosted
                                                     && l.JournalEntry.EntryDate >= from && l.JournalEntry.EntryDate < to);
            if (filter is not null) q = filter(q);
            return await q.SumAsync(l => (decimal?)(l.Debit - l.Credit)) ?? 0;
        }
        var repExpenses = await LedgerAsync("5103");
        var otherVouchers = await LedgerAsync("5101", q => q.Where(l => l.JournalEntry.SourceTable != "FinanceEntries"));

        // ---- الرواتب المعتمدة لهذا الشهر (بالدينار، كما في قيد الاعتماد) ----
        var salaries = await _db.PayrollRuns.AsNoTracking()
            .Where(r => r.Status == PayrollRunStatus.Approved && r.PeriodYear == year && r.PeriodMonth == month && r.JournalEntryId != null)
            .SelectMany(r => r.JournalEntry!.Lines).SumAsync(l => (decimal?)l.Debit) ?? 0;
        // أجور العمال الوقتيين المصروفة في الشهر
        salaries += await LedgerAsync("5102", q => q.Where(l => l.JournalEntry.SourceTable == "TempWorkerPayments"));

        // ---- الخسائر بالكلفة ----
        var losses = await new ProductionStockReports(_db).MonthlyLossesAsync(year, month);
        var lossByKind = losses.GroupBy(l => l.Kind).Select(g => (Kind: g.Key, Value: g.Sum(x => x.Value))).ToList();

        var operating = entries.Where(e => e.Kind == FinanceCategoryKind.Operating).Sum(e => e.Amount) + otherVouchers;
        var report = new FinalAccountsReport
        {
            Year = year, Month = month, Products = products,
            Revenue = products.Sum(p => p.Revenue), ListValue = products.Sum(p => p.ListValue),
            LoadingRevenue = invoices.Sum(i => i.LoadingSuppliesAmount), MaterialsCost = products.Sum(p => p.Cost),
            OperatingExpenses = operating, Salaries = salaries, RepFieldExpenses = repExpenses,
            Losses = lossByKind.Sum(l => l.Value),
            NonOperatingExpenses = entries.Where(e => e.Kind == FinanceCategoryKind.NonOperating).Sum(e => e.Amount),
            OtherIncome = entries.Where(e => e.Kind == FinanceCategoryKind.OtherIncome).Sum(e => e.Amount),
            PiecesSold = products.Sum(p => p.PiecesSold)
        };

        var r = report.Lines;
        r.Add(new("المبيعات", "المبيعات بسعر القائمة", report.ListValue));
        r.Add(new("المبيعات", "خصم الوكلاء والأسعار الخاصة", -report.Discounts));
        r.Add(new("المبيعات", "صافي المبيعات", report.Revenue, true));
        if (report.LoadingRevenue != 0) r.Add(new("المبيعات", "مستلزمات التحميل", report.LoadingRevenue));
        r.Add(new("الكلفة", "كلفة المواد للمباع", -report.MaterialsCost));
        r.Add(new("الكلفة", "مجمل الربح", report.GrossProfit, true));
        foreach (var e in entries.Where(e => e.Kind == FinanceCategoryKind.Operating).OrderByDescending(e => e.Amount))
            r.Add(new("مصاريف تشغيلية", e.Name, -e.Amount));
        if (otherVouchers != 0) r.Add(new("مصاريف تشغيلية", "سندات صرف عامة", -otherVouchers));
        if (salaries != 0) r.Add(new("مصاريف تشغيلية", "الرواتب والأجور", -salaries));
        if (repExpenses != 0) r.Add(new("مصاريف تشغيلية", "مصاريف المندوبين الميدانية", -repExpenses));
        foreach (var (kind, value) in lossByKind.OrderByDescending(l => l.Value))
            r.Add(new("الخسائر بالكلفة", kind, -value));
        r.Add(new("الخسائر بالكلفة", "الربح التشغيلي", report.OperatingProfit, true));
        foreach (var e in entries.Where(e => e.Kind == FinanceCategoryKind.NonOperating).OrderByDescending(e => e.Amount))
            r.Add(new("غير تشغيلي (لا يدخل كلفة القنينة)", e.Name, -e.Amount));
        foreach (var e in entries.Where(e => e.Kind == FinanceCategoryKind.OtherIncome).OrderByDescending(e => e.Amount))
            r.Add(new("إيرادات أخرى", e.Name, e.Amount));
        r.Add(new("النتيجة", "صافي ربح الشهر", report.NetProfit, true));
        return report;
    }

    // ============================ رأس المال التشغيلي ============================

    public Task<List<WorkingCapitalSetting>> CapitalHistoryAsync() =>
        _db.WorkingCapitalSettings.AsNoTracking().Include(w => w.CreatedByUser).OrderByDescending(w => w.EffectiveFrom).ThenByDescending(w => w.Id).ToListAsync();

    public async Task<WorkingCapitalSetting?> CapitalAtAsync(DateTime date) =>
        await _db.WorkingCapitalSettings.AsNoTracking().Where(w => w.EffectiveFrom <= date.Date)
                 .OrderByDescending(w => w.EffectiveFrom).ThenByDescending(w => w.Id).FirstOrDefaultAsync();

    public async Task<FinanceOperationResult> SetCapitalAsync(DateTime effectiveFrom, decimal amount, string? notes, int userId)
    {
        if (!await SpecialPermission.HasAsync(_db, userId, SpecialPermission.FinalAccounts))
            return FinanceOperationResult.Fail("تحديد رأس المال التشغيلي لمن يملك صلاحية «الحسابات الختامية» فقط");
        if (amount < 0) return FinanceOperationResult.Fail("المبلغ لا يكون سالبًا");
        _db.WorkingCapitalSettings.Add(new WorkingCapitalSetting
        {
            EffectiveFrom = effectiveFrom.Date, Amount = amount, Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(), CreatedByUserId = userId
        });
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Insert", "WorkingCapitalSettings", null, $"رأس المال التشغيلي {amount:N0} اعتبارًا من {effectiveFrom:yyyy/MM/dd}");
        return FinanceOperationResult.Ok();
    }

    /// <summary>صافي الموجودات (بالكلفة) مقابل رأس المال التشغيلي: الفائض ربح متحقق يُنقل لصندوق المنزل.</summary>
    public async Task<WorkingCapitalSnapshot> WorkingCapitalAsync(DateTime date)
    {
        var snapshot = await new ReconciliationService(_db).ComputeAsync(date, FinishedGoodsValuation.Cost);
        var capital = await CapitalAtAsync(date);
        var cash = new CashBoxService(_db);
        decimal home = 0, business = 0;
        foreach (var b in await _db.CashBoxes.AsNoTracking().Where(b => b.IsActive).ToListAsync())
        {
            var bal = await cash.GetBalanceAsync(b.Id);
            if (b.BoxType == CashBoxType.Home) home += bal;
            else if (b.BoxType != CashBoxType.Bank) business += bal;
        }
        return new WorkingCapitalSnapshot
        {
            Date = date.Date, Capital = capital?.Amount ?? 0, CapitalEffectiveFrom = capital?.EffectiveFrom,
            NetAssets = snapshot.NetAssets, HomeBoxBalance = home, CashInBusinessBoxes = business
        };
    }

    /// <summary>صندوق المنزل (يُنشأ عند أول استعمال).</summary>
    public async Task<CashBox> HomeBoxAsync()
    {
        var box = await _db.CashBoxes.FirstOrDefaultAsync(b => b.BoxType == CashBoxType.Home && b.IsActive);
        if (box is not null) return box;
        box = new CashBox { Name = "صندوق المنزل", BoxType = CashBoxType.Home, Notes = "الفائض المتحقق عن رأس المال التشغيلي" };
        _db.CashBoxes.Add(box);
        await _db.SaveChangesAsync();
        return box;
    }

    /// <summary>نقل الفائض من صندوق العمل إلى صندوق المنزل (لا يتجاوز الفائض المحسوب).</summary>
    public async Task<FinanceOperationResult> MoveSurplusToHomeAsync(int fromBoxId, decimal amount, DateTime date, int userId)
    {
        if (!await SpecialPermission.HasAsync(_db, userId, SpecialPermission.FinalAccounts))
            return FinanceOperationResult.Fail("نقل الفائض لمن يملك صلاحية «الحسابات الختامية» فقط");
        if (amount <= 0) return FinanceOperationResult.Fail("المبلغ يجب أن يكون أكبر من صفر");
        var wc = await WorkingCapitalAsync(date);
        if (amount > wc.Surplus)
            return FinanceOperationResult.Fail($"المبلغ أكبر من الفائض عن رأس المال التشغيلي ({wc.Surplus:N0} د.ع)");
        var home = await HomeBoxAsync();
        if (home.Id == fromBoxId) return FinanceOperationResult.Fail("اختر صندوق العمل المصدر");
        var (result, _) = await new CashBoxService(_db).TransferAsync(fromBoxId, home.Id, amount, date, "نقل الفائض المتحقق إلى صندوق المنزل", userId);
        return result;
    }

    // ============================ محاكاة الكلفة ============================

    /// <summary>
    /// «لو ارتفعت الأسعار»: كلفة القطعة لكل منتج مصنَّع من وصفته بالأسعار المقترحة للمواد (بدل متوسط الكلفة الحالي)،
    /// مضافًا إليها نصيب القطعة من المصاريف التشغيلية لآخر شهر كامل. لا يغيّر أي سعر فعلي.
    /// </summary>
    public async Task<List<CostSimulationRow>> SimulateAsync(IReadOnlyDictionary<int, decimal> proposedCosts, decimal? overheadPerPiece = null)
    {
        if (overheadPerPiece is null)
        {
            var last = DateTime.Today.AddMonths(-1);
            overheadPerPiece = (await MonthAsync(last.Year, last.Month)).OverheadPerPiece;
        }
        var boms = await _db.BillOfMaterials.AsNoTracking().Where(b => b.IsActive)
            .Select(b => new
            {
                b.FinishedItemId, b.FinishedItem.ItemName, b.FinishedItem.SalePrice,
                Lines = b.Lines.Select(l => new { l.RawMaterialItemId, l.QuantityPerUnit, l.RawMaterialItem.CostPrice }).ToList()
            }).ToListAsync();
        return boms.GroupBy(b => b.FinishedItemId).Select(g => g.First()).Select(b => new CostSimulationRow
        {
            ItemId = b.FinishedItemId, ItemName = b.ItemName, SalePrice = b.SalePrice, OverheadPerPiece = overheadPerPiece.Value,
            CurrentMaterialCost = Math.Round(b.Lines.Sum(l => l.QuantityPerUnit * (l.CostPrice ?? 0)), 2),
            SimulatedMaterialCost = Math.Round(b.Lines.Sum(l => l.QuantityPerUnit *
                (proposedCosts.TryGetValue(l.RawMaterialItemId, out var p) ? p : l.CostPrice ?? 0)), 2)
        }).OrderBy(r => r.ItemName).ToList();
    }

    // ============================ التقرير اليومي للصناديق ============================

    public async Task<List<DailyCashBoxRow>> DailyCashAsync(DateTime date, IReadOnlyCollection<int>? boxIds = null)
    {
        var cash = new CashBoxService(_db);
        var boxes = await _db.CashBoxes.AsNoTracking().Where(b => b.IsActive && (boxIds == null || boxIds.Contains(b.Id))).OrderBy(b => b.Name).ToListAsync();
        var rows = new List<DailyCashBoxRow>();
        foreach (var b in boxes)
        {
            var (opening, tx) = await cash.GetStatementAsync(b.Id, date, date, includeVoided: false);
            rows.Add(new DailyCashBoxRow
            {
                BoxId = b.Id, BoxName = b.Name, BoxType = b.BoxType, Opening = opening, In = tx.Sum(t => t.In), Out = tx.Sum(t => t.Out),
                ByType = tx.GroupBy(t => t.TypeLabel).Select(g => (g.Key, g.Sum(t => t.In), g.Sum(t => t.Out))).OrderBy(x => x.Key).ToList()
            });
        }
        return rows;
    }
}
