using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

public record ReconciliationLineDto(string Section, string Description, decimal? Quantity, decimal? UnitValue, decimal Value);

public record PartnerSharePreview(int PartnerId, string PartnerName, decimal SharePercent, bool IsManager, decimal Amount);

/// <summary>نتيجة حساب المطابقة (قبل الحفظ أو بعده).</summary>
public class ReconciliationSnapshot
{
    public DateTime ReconDate { get; init; }
    public FinishedGoodsValuation Valuation { get; init; }
    public decimal RawMaterials { get; init; }
    public decimal WorkInProcess { get; init; }
    public decimal FinishedGoods { get; init; }
    public decimal CustomerDebts { get; init; }
    public decimal CashInBoxes { get; init; }
    public decimal CashWithReps { get; init; }
    public decimal EmployeeAdvances { get; init; }
    public decimal SupplierAdvances { get; init; }
    public decimal SupplierDebts { get; init; }
    public decimal CustomerDeposits { get; init; }
    public decimal TotalAssets => RawMaterials + WorkInProcess + FinishedGoods + CustomerDebts + CashInBoxes + CashWithReps + EmployeeAdvances + SupplierAdvances;
    public decimal TotalLiabilities => SupplierDebts + CustomerDeposits;
    public decimal NetAssets => TotalAssets - TotalLiabilities;
    public int? PreviousId { get; init; }
    public string? PreviousNumber { get; init; }
    public DateTime? PreviousDate { get; init; }
    public decimal? PreviousNetAssets { get; init; }
    public decimal PartnerWithdrawals { get; init; }
    public decimal OwnerDeposits { get; init; }
    public bool IsBaseline => PreviousId is null;
    /// <summary>الفائض = الصافي − صافي السابقة + سحوبات الشركاء − إيداعات المالك. صفر لمطابقة الأساس الأولى.</summary>
    public decimal Surplus => IsBaseline ? 0 : NetAssets - PreviousNetAssets!.Value + PartnerWithdrawals - OwnerDeposits;
    public List<ReconciliationLineDto> Lines { get; init; } = new();
    public List<PartnerSharePreview> Shares { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
}

public class PartnerBalanceRow
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public decimal SharePercent { get; init; }
    public bool IsManager { get; init; }
    public bool IsActive { get; init; }
    public string? Notes { get; init; }
    public decimal TotalShares { get; init; }
    public decimal TotalWithdrawals { get; init; }
    public decimal Balance { get; init; }
}

public class PartnerStatementRow
{
    public int Id { get; init; }
    public string TxNumber { get; init; } = "";
    public DateTime TxDate { get; init; }
    public PartnerTxKind Kind { get; init; }
    public string KindText => ReconciliationService.KindLabel(Kind);
    public decimal Credit { get; init; }
    public decimal Debit { get; init; }
    public decimal Balance { get; init; }
    public string? Reference { get; init; }
    public string? Notes { get; init; }
}

public class ReconciliationListRow
{
    public int Id { get; init; }
    public string ReconNumber { get; init; } = "";
    public DateTime ReconDate { get; init; }
    public FinishedGoodsValuation Valuation { get; init; }
    public decimal NetAssets { get; init; }
    public decimal Surplus { get; init; }
    public bool IsBaseline { get; init; }
    public string CreatedBy { get; init; } = "";
}

/// <summary>
/// المطابقة الدورية للموجودات وأرباح الشركاء:
/// الموجودات = المواد الأولية (بالكلفة) + تحت التصنيع على الماكينات (بالكلفة) + المنتج التام (بالكلفة من الوصفة أو بسعر البيع)
///           + ديون العملاء + النقد في الصناديق + النقد مع المندوبين + سلف ومسحوبات الموظفين غير المستقطعة + دفعات مقدمة للموردين؛
/// ناقص ديون الموردين وتأمينات العملاء (أمانات). الفائض عن المطابقة السابقة يوزَّع على الشركاء بنسبهم.
/// مخزن التالف لا يدخل في التقييم.
/// </summary>
public class ReconciliationService
{
    public const string ProfitShareRule = "PartnerProfitShare";      // مدين أرباح المطابقة / دائن جاري الشركاء
    public const string WithdrawalRule = "PartnerWithdrawal";        // مدين جاري الشركاء / دائن الصندوق
    public const string OpeningRule = "PartnerOpening";              // مدين رأس المال / دائن جاري الشركاء

    public const string RawSection = "مواد أولية";
    public const string WipSection = "تحت التصنيع";
    public const string FgSection = "منتج تام";
    public const string CustomersSection = "ديون العملاء";
    public const string CashSection = "النقد في الصناديق";
    public const string RepsSection = "نقد مع المندوبين";
    public const string AdvancesSection = "سلف ومسحوبات الموظفين";
    public const string SupplierAdvancesSection = "دفعات مقدمة للموردين";
    public const string SupplierDebtsSection = "ديون الموردين";
    public const string DepositsSection = "تأمينات العملاء";

    private readonly ProjectDbContext _db;
    public ReconciliationService(ProjectDbContext db) => _db = db;

    public static string KindLabel(PartnerTxKind k) => k switch
    {
        PartnerTxKind.ProfitShare => "حصة أرباح",
        PartnerTxKind.Withdrawal => "سحب أرباح",
        _ => "رصيد افتتاحي"
    };

    // ============================ الحساب ============================

    /// <summary>كلفة الوحدة الأساسية لكل صنف مصنَّع من وصفته الفعّالة (مجموع كمية كل مكوّن × سعر كلفته).</summary>
    public async Task<Dictionary<int, decimal?>> ManufacturedUnitCostsAsync()
    {
        var boms = await _db.BillOfMaterials.AsNoTracking().Where(b => b.IsActive)
            .Select(b => new { b.FinishedItemId, Lines = b.Lines.Select(l => new { l.QuantityPerUnit, l.RawMaterialItem.CostPrice }).ToList() })
            .ToListAsync();
        var result = new Dictionary<int, decimal?>();
        foreach (var b in boms.GroupBy(b => b.FinishedItemId))
        {
            var lines = b.First().Lines;
            result[b.Key] = lines.Count == 0 || lines.Any(l => l.CostPrice is null) ? null : lines.Sum(l => l.QuantityPerUnit * l.CostPrice!.Value);
        }
        return result;
    }

    public async Task<ReconciliationSnapshot> ComputeAsync(DateTime reconDate, FinishedGoodsValuation valuation)
    {
        var lines = new List<ReconciliationLineDto>();
        var warnings = new List<string>();

        // ---- المخزون: كل المخازن عدا التالف، وتحت التصنيع منفصل ----
        var stock = (await _db.StockTransactions.AsNoTracking()
                .Where(t => t.Warehouse.WarehouseType != WarehouseType.Damaged)
                .GroupBy(t => new { t.ItemId, t.Warehouse.WarehouseType })
                .Select(g => new { g.Key.ItemId, g.Key.WarehouseType, Qty = g.Sum(t => t.QuantityBaseUnits) })
                .ToListAsync())
            .GroupBy(x => new { x.ItemId, Wip = x.WarehouseType == WarehouseType.WorkInProcess })
            .Select(g => new { g.Key.ItemId, g.Key.Wip, Qty = g.Sum(x => x.Qty) })
            .Where(x => x.Qty != 0).ToList();
        var items = await _db.Items.AsNoTracking().ToDictionaryAsync(i => i.Id);
        var bomCosts = await ManufacturedUnitCostsAsync();
        bool IsFinished(Item i) => i.SourcingMethod == SourcingMethod.Manufactured || (i.SourcingMethod == SourcingMethod.Both && bomCosts.ContainsKey(i.Id));

        decimal raw = 0, wip = 0, fg = 0;
        foreach (var s in stock.OrderBy(s => items[s.ItemId].ItemName))
        {
            var item = items[s.ItemId];
            var label = $"{item.ItemName} ({item.ItemCode})";
            decimal? unit;
            string section;
            if (!s.Wip && IsFinished(item))
            {
                section = FgSection;
                unit = valuation == FinishedGoodsValuation.SalePrice ? item.SalePrice : bomCosts.GetValueOrDefault(item.Id) ?? item.CostPrice;
                if (unit is null or 0)
                    warnings.Add(valuation == FinishedGoodsValuation.SalePrice
                        ? $"{label}: لا يوجد سعر بيع — قُيّم بصفر"
                        : $"{label}: تعذّر حساب الكلفة (وصفة ناقصة أو مكوّن بلا سعر كلفة) — قُيّم بصفر");
            }
            else
            {
                section = s.Wip ? WipSection : RawSection;
                unit = item.CostPrice ?? (IsFinished(item) ? bomCosts.GetValueOrDefault(item.Id) : null);
                if (unit is null or 0) warnings.Add($"{label}: لا يوجد سعر كلفة — قُيّم بصفر. أدخله من المخازن ← الأصناف");
            }
            if (s.Qty < 0) warnings.Add($"{label}: رصيد سالب ({s.Qty:N0}) في {section} — راجع الحركات");
            var value = Math.Round(s.Qty * (unit ?? 0), 2);
            lines.Add(new ReconciliationLineDto(section, label, s.Qty, unit ?? 0, value));
            switch (section)
            {
                case RawSection: raw += value; break;
                case WipSection: wip += value; break;
                default: fg += value; break;
            }
        }

        // ---- ديون العملاء (الصافي: ما عليهم ناقص ما لهم) ----
        var customers = (await new SalesService(_db).GetCustomerBalancesAsync()).Where(c => c.Balance != 0).ToList();
        var customerDebts = customers.Sum(c => c.Balance);
        lines.Add(new ReconciliationLineDto(CustomersSection, $"عليهم دين: {customers.Count(c => c.Balance > 0)} عميل", null, null, customers.Where(c => c.Balance > 0).Sum(c => c.Balance)));
        if (customers.Any(c => c.Balance < 0))
            lines.Add(new ReconciliationLineDto(CustomersSection, $"لهم رصيد دائن: {customers.Count(c => c.Balance < 0)} عميل", null, null, customers.Where(c => c.Balance < 0).Sum(c => c.Balance)));

        // ---- النقد في الصناديق ----
        var cash = new CashBoxService(_db);
        decimal boxes = 0;
        foreach (var b in await _db.CashBoxes.AsNoTracking().Where(b => b.IsActive).OrderBy(b => b.Name).ToListAsync())
        {
            var bal = await cash.GetBalanceAsync(b.Id);
            if (bal == 0) continue;
            boxes += bal;
            lines.Add(new ReconciliationLineDto(CashSection, b.Name, null, null, bal));
        }

        // ---- النقد مع المندوبين (المحافظ) ----
        var repsSvc = new RepsService(_db);
        decimal reps = 0;
        foreach (var r in await _db.Employees.AsNoTracking().Where(e => e.IsSalesRep).OrderBy(e => e.FullName).ToListAsync())
        {
            var bal = await repsSvc.GetWalletBalanceAsync(r.Id);
            if (bal == 0) continue;
            reps += bal;
            lines.Add(new ReconciliationLineDto(RepsSection, r.FullName, null, null, bal));
        }

        // ---- سلف ومسحوبات الموظفين التي لم تُستقطع بعد (العقوبات ليست نقدًا) ----
        var advances = (await new EmployeeDeductionService(_db).GetListAsync(openOnly: true)).Where(d => d.Kind != EmployeeDeductionKind.Penalty).ToList();
        var employeeAdvances = advances.Sum(a => a.Remaining);
        if (employeeAdvances != 0)
            lines.Add(new ReconciliationLineDto(AdvancesSection, $"متبقٍّ على {advances.Select(a => a.EmployeeId).Distinct().Count()} موظف", null, null, employeeAdvances));

        // ---- الموردون: من الدفتر (الدفعات المقدمة 1302 أصل، والذمم 2101 التزام) ----
        var supplierAdvances = await AccountBalanceAsync("1302", debitNature: true);
        if (supplierAdvances != 0) lines.Add(new ReconciliationLineDto(SupplierAdvancesSection, "دفعات مقدمة لم تُسوَّ بعد", null, null, supplierAdvances));
        var supplierDebts = await AccountBalanceAsync("2101", debitNature: false);
        if (supplierDebts != 0) lines.Add(new ReconciliationLineDto(SupplierDebtsSection, "ذمم الموردين المستحقة", null, null, supplierDebts));

        // ---- تأمينات العملاء (أمانات تُرد) ----
        var deposits = (await new CustomerDepositService(_db).GetBalancesAsync()).Where(d => d.Balance != 0).ToList();
        var customerDeposits = deposits.Sum(d => d.Balance);
        if (customerDeposits != 0) lines.Add(new ReconciliationLineDto(DepositsSection, $"تأمينات {deposits.Count} عميل", null, null, customerDeposits));

        // ---- المقارنة بالسابقة ----
        var prev = await _db.AssetReconciliations.AsNoTracking().OrderByDescending(r => r.ReconDate).ThenByDescending(r => r.Id).FirstOrDefaultAsync();
        decimal partnerWithdrawals = 0, ownerDeposits = 0;
        if (prev is not null)
        {
            partnerWithdrawals = -(await _db.PartnerTransactions.Where(t => t.Kind == PartnerTxKind.Withdrawal && t.CreatedAt > prev.CreatedAt)
                                                             .SumAsync(t => (decimal?)t.Amount) ?? 0);
            ownerDeposits = await _db.CashBoxTransactions.Where(t => t.TxType == CashBoxTxType.Deposit && !t.IsVoided && t.CreatedAt > prev.CreatedAt)
                                                       .SumAsync(t => (decimal?)t.Amount) ?? 0;
        }

        var snapshot = new ReconciliationSnapshot
        {
            ReconDate = reconDate.Date, Valuation = valuation, RawMaterials = raw, WorkInProcess = wip, FinishedGoods = fg,
            CustomerDebts = customerDebts, CashInBoxes = boxes, CashWithReps = reps, EmployeeAdvances = employeeAdvances,
            SupplierAdvances = supplierAdvances, SupplierDebts = supplierDebts, CustomerDeposits = customerDeposits,
            PreviousId = prev?.Id, PreviousNumber = prev?.ReconNumber, PreviousDate = prev?.ReconDate, PreviousNetAssets = prev?.NetAssets,
            PartnerWithdrawals = partnerWithdrawals, OwnerDeposits = ownerDeposits, Lines = lines, Warnings = warnings
        };
        snapshot.Shares.AddRange(await PreviewSharesAsync(snapshot.Surplus));
        return snapshot;
    }

    /// <summary>توزيع مبلغ على الشركاء الفعّالين بنسبهم (بالدينار الصحيح، والفرق الناتج عن التقريب على المدير أو الأكبر نسبة).</summary>
    public async Task<List<PartnerSharePreview>> PreviewSharesAsync(decimal amount)
    {
        var partners = await _db.Partners.AsNoTracking().Where(p => p.IsActive && p.SharePercent > 0).OrderByDescending(p => p.IsManager).ThenByDescending(p => p.SharePercent).ToListAsync();
        var shares = partners.Select(p => new PartnerSharePreview(p.Id, p.Name, p.SharePercent, p.IsManager, Math.Round(amount * p.SharePercent / 100m, 0))).ToList();
        if (shares.Count > 0 && partners.Sum(p => p.SharePercent) == 100m)
        {
            var diff = Math.Round(amount, 0) - shares.Sum(s => s.Amount);
            if (diff != 0) shares[0] = shares[0] with { Amount = shares[0].Amount + diff };
        }
        return shares;
    }

    private async Task<decimal> AccountBalanceAsync(string code, bool debitNature)
    {
        var net = await _db.JournalEntryLines.Where(l => l.Account.AccountCode == code && l.JournalEntry.IsPosted)
                           .SumAsync(l => (decimal?)(l.Debit - l.Credit)) ?? 0;
        return debitNature ? net : -net;
    }

    // ============================ الاعتماد ============================

    /// <summary>
    /// يحفظ المطابقة كلقطة (يعيد الحساب لحظة الحفظ). أول مطابقة "أساس" بلا توزيع. ما بعدها يوزّع الفائض
    /// (أو الخسارة) على الشركاء الفعّالين — يلزم أن مجموع نسبهم 100% — بقيد واحد على جاري الشركاء.
    /// </summary>
    public async Task<(FinanceOperationResult result, AssetReconciliation? recon)> PostAsync(DateTime reconDate, FinishedGoodsValuation valuation, string? notes, int userId)
    {
        if (!await new CashBoxService(_db).IsAdminAsync(userId)) return (FinanceOperationResult.Fail("اعتماد المطابقة وتوزيع الأرباح للأدمن فقط"), null);
        var last = await _db.AssetReconciliations.AsNoTracking().OrderByDescending(r => r.ReconDate).Select(r => (DateTime?)r.ReconDate).FirstOrDefaultAsync();
        if (last is { } l && reconDate.Date < l) return (FinanceOperationResult.Fail($"تاريخ المطابقة قبل آخر مطابقة ({l:yyyy/MM/dd})"), null);

        var s = await ComputeAsync(reconDate, valuation);
        if (!s.IsBaseline && s.Surplus != 0)
        {
            if (s.Shares.Count == 0) return (FinanceOperationResult.Fail("لا يوجد شركاء فعّالون بنسب — أضفهم من شاشة الشركاء أولًا"), null);
            var total = await _db.Partners.Where(p => p.IsActive).SumAsync(p => p.SharePercent);
            if (total != 100m) return (FinanceOperationResult.Fail($"مجموع نسب الشركاء الفعّالين {total:0.##}% — يجب أن يكون 100%"), null);
        }

        await using var tx = await _db.Database.BeginTransactionAsync();
        var n = (await _db.Database.SqlQueryRaw<int>("SELECT NEXT VALUE FOR seq_Reconciliation AS [Value]").ToListAsync())[0];
        var recon = new AssetReconciliation
        {
            ReconNumber = $"REC-{reconDate.Year}-{n:D4}", ReconDate = s.ReconDate, FinishedGoodsValuation = valuation,
            RawMaterialsValue = s.RawMaterials, WorkInProcessValue = s.WorkInProcess, FinishedGoodsValue = s.FinishedGoods,
            CustomerDebts = s.CustomerDebts, CashInBoxes = s.CashInBoxes, CashWithReps = s.CashWithReps, EmployeeAdvances = s.EmployeeAdvances,
            SupplierAdvances = s.SupplierAdvances, SupplierDebts = s.SupplierDebts, CustomerDeposits = s.CustomerDeposits, NetAssets = s.NetAssets,
            PreviousReconciliationId = s.PreviousId, PreviousNetAssets = s.PreviousNetAssets, PartnerWithdrawalsSincePrevious = s.PartnerWithdrawals,
            OwnerDepositsSincePrevious = s.OwnerDeposits, Surplus = s.Surplus, IsBaseline = s.IsBaseline, Notes = Clean(notes), CreatedByUserId = userId
        };
        foreach (var line in s.Lines)
            recon.Lines.Add(new AssetReconciliationLine { Section = line.Section, Description = Trim(line.Description, 250), Quantity = line.Quantity, UnitValue = line.UnitValue, Value = line.Value });
        _db.AssetReconciliations.Add(recon);
        await _db.SaveChangesAsync();

        if (!s.IsBaseline && s.Surplus != 0)
        {
            var text = $"توزيع {(s.Surplus > 0 ? "فائض" : "خسارة")} المطابقة {recon.ReconNumber} على الشركاء";
            var (entry, error) = await PostSignedAsync(ProfitShareRule, s.Surplus, s.ReconDate, text, userId, "AssetReconciliations", recon.Id, "RC");
            if (error is not null) return (FinanceOperationResult.Fail(error), null);
            await _db.SaveChangesAsync();
            recon.JournalEntryId = entry!.Id;
            foreach (var share in s.Shares.Where(x => x.Amount != 0))
                _db.PartnerTransactions.Add(new PartnerTransaction
                {
                    TxNumber = await NextTxNumberAsync(s.ReconDate), PartnerId = share.PartnerId, TxDate = s.ReconDate, Kind = PartnerTxKind.ProfitShare,
                    Amount = share.Amount, SharePercent = share.SharePercent, ReconciliationId = recon.Id, JournalEntryId = entry.Id,
                    Notes = $"{share.SharePercent:0.##}% من {(s.Surplus > 0 ? "فائض" : "خسارة")} {s.Surplus:N0}", CreatedByUserId = userId
                });
            await _db.SaveChangesAsync();
        }
        await tx.CommitAsync();
        return (FinanceOperationResult.Ok(), recon);
    }

    /// <summary>قيد بقاعدة ربط؛ المبلغ السالب يعكس طرفي القيد (خسارة، أو رصيد افتتاحي مدين).</summary>
    private async Task<(JournalEntry? entry, string? error)> PostSignedAsync(string rule, decimal signedAmount, DateTime date, string text, int userId,
                                                                           string sourceTable, int? sourceId, string prefix)
    {
        var (entry, error) = await LedgerHelper.PostJournalAsync(_db, rule, Math.Abs(signedAmount), date, JournalEntryType.AutoVoucher, text, userId, sourceTable, sourceId, prefix);
        if (entry is not null && signedAmount < 0)
            foreach (var line in entry.Lines) (line.Debit, line.Credit) = (line.Credit, line.Debit);
        return (entry, error);
    }

    // ============================ الشركاء ============================

    public async Task<List<PartnerBalanceRow>> GetPartnersAsync()
    {
        var partners = await _db.Partners.AsNoTracking().OrderByDescending(p => p.IsManager).ThenByDescending(p => p.SharePercent).ThenBy(p => p.Name).ToListAsync();
        var sums = await _db.PartnerTransactions.AsNoTracking().GroupBy(t => t.PartnerId)
            .Select(g => new
            {
                g.Key,
                Credits = g.Where(t => t.Kind != PartnerTxKind.Withdrawal).Sum(t => t.Amount),
                Withdrawals = g.Where(t => t.Kind == PartnerTxKind.Withdrawal).Sum(t => -t.Amount)
            }).ToDictionaryAsync(x => x.Key);
        return partners.Select(p =>
        {
            var x = sums.GetValueOrDefault(p.Id);
            var credits = x?.Credits ?? 0;
            var withdrawals = x?.Withdrawals ?? 0;
            return new PartnerBalanceRow
            {
                Id = p.Id, Name = p.Name, SharePercent = p.SharePercent, IsManager = p.IsManager, IsActive = p.IsActive, Notes = p.Notes,
                TotalShares = credits, TotalWithdrawals = withdrawals, Balance = credits - withdrawals
            };
        }).ToList();
    }

    public async Task<decimal> GetPartnerBalanceAsync(int partnerId) =>
        await _db.PartnerTransactions.Where(t => t.PartnerId == partnerId).SumAsync(t => (decimal?)t.Amount) ?? 0;

    /// <summary>إضافة أو تعديل شريك (للأدمن). الاسم فريد، والنسبة بين 0 و100، ومجموع نسب الفعّالين لا يتجاوز 100%، ومدير واحد فقط.</summary>
    public async Task<(FinanceOperationResult result, Partner? partner)> SavePartnerAsync(int? id, string name, decimal sharePercent, bool isManager, bool isActive, string? notes, int userId)
    {
        if (!await new CashBoxService(_db).IsAdminAsync(userId)) return (FinanceOperationResult.Fail("إدارة الشركاء ونسبهم للأدمن فقط"), null);
        if (string.IsNullOrWhiteSpace(name)) return (FinanceOperationResult.Fail("اكتب اسم الشريك"), null);
        if (sharePercent is < 0 or > 100) return (FinanceOperationResult.Fail("النسبة بين 0 و100"), null);
        name = name.Trim();
        if (await _db.Partners.AnyAsync(p => p.Name == name && p.Id != id)) return (FinanceOperationResult.Fail("يوجد شريك بنفس الاسم"), null);
        var others = await _db.Partners.Where(p => p.Id != id && p.IsActive).SumAsync(p => (decimal?)p.SharePercent) ?? 0;
        if (isActive && others + sharePercent > 100m)
            return (FinanceOperationResult.Fail($"مجموع النسب يصبح {others + sharePercent:0.##}% — المتاح {100m - others:0.##}% فقط"), null);

        var p = id is int pid ? await _db.Partners.FirstOrDefaultAsync(x => x.Id == pid) : null;
        if (id is not null && p is null) return (FinanceOperationResult.Fail("الشريك غير موجود"), null);
        if (p is null) _db.Partners.Add(p = new Partner());
        if (isManager)
            foreach (var m in await _db.Partners.Where(x => x.IsManager && x.Id != id).ToListAsync()) m.IsManager = false;
        p.Name = name;
        p.SharePercent = sharePercent;
        p.IsManager = isManager;
        p.IsActive = isActive;
        p.Notes = Clean(notes);
        await _db.SaveChangesAsync();
        return (FinanceOperationResult.Ok(), p);
    }

    /// <summary>سحب أرباح نقدًا من الصندوق: لا يتجاوز رصيد الشريك ولا رصيد الصندوق.</summary>
    public async Task<(FinanceOperationResult result, PartnerTransaction? tx)> WithdrawAsync(int partnerId, decimal amount, DateTime date, string? notes, int userId, int? cashBoxId = null)
    {
        var cash = new CashBoxService(_db);
        if (!await cash.IsAdminAsync(userId)) return (FinanceOperationResult.Fail("سحب أرباح الشركاء للأدمن فقط"), null);
        if (amount <= 0) return (FinanceOperationResult.Fail("المبلغ يجب أن يكون أكبر من صفر"), null);
        var partner = await _db.Partners.AsNoTracking().FirstOrDefaultAsync(p => p.Id == partnerId);
        if (partner is null) return (FinanceOperationResult.Fail("اختر الشريك"), null);
        var balance = await GetPartnerBalanceAsync(partnerId);
        if (amount > balance) return (FinanceOperationResult.Fail($"رصيد أرباح {partner.Name} {balance:N0} د.ع فقط"), null);
        var boxId = cashBoxId ?? await _db.CashBoxes.Where(b => b.IsActive && (b.OwnerUserId == userId || b.IsDefault || b.BoxType == CashBoxType.Main))
                                        .OrderBy(b => b.OwnerUserId == userId ? 0 : b.IsDefault ? 1 : 2).ThenBy(b => b.Id).Select(b => (int?)b.Id).FirstOrDefaultAsync();
        if (boxId is null) return (FinanceOperationResult.Fail("لا يوجد صندوق مفعّل للسحب منه"), null);
        var boxBalance = await cash.GetBalanceAsync(boxId.Value);
        if (boxBalance < amount) return (FinanceOperationResult.Fail($"رصيد الصندوق غير كافٍ: المتاح {boxBalance:N0} د.ع"), null);

        await using var dbTx = await _db.Database.BeginTransactionAsync();
        var number = await NextTxNumberAsync(date);
        var text = $"سحب أرباح {number} — {partner.Name}" + (string.IsNullOrWhiteSpace(notes) ? "" : $" ({notes.Trim()})");
        var (entry, error) = await LedgerHelper.PostJournalAsync(_db, WithdrawalRule, amount, date, JournalEntryType.AutoVoucher, text, userId, "PartnerTransactions", null, "PW");
        if (error is not null) return (FinanceOperationResult.Fail(error), null);
        await _db.SaveChangesAsync();
        var t = new PartnerTransaction
        {
            TxNumber = number, PartnerId = partnerId, TxDate = date.Date, Kind = PartnerTxKind.Withdrawal, Amount = -amount,
            Notes = Clean(notes), JournalEntryId = entry!.Id, CreatedByUserId = userId
        };
        _db.PartnerTransactions.Add(t);
        await _db.SaveChangesAsync();
        entry.SourceId = t.Id;
        await _db.SaveChangesAsync();
        await cash.RecordAutoAsync(userId, CashBoxTxType.PartnerWithdrawal, -amount, date, "PartnerTransactions", t.Id, partner.Name, text, entry.Id, boxId);
        await dbTx.CommitAsync();
        return (FinanceOperationResult.Ok(), t);
    }

    /// <summary>رصيد افتتاحي لشريك (موجب = أرباح مستحقة له، سالب = عليه) — للنقل من نظام سابق، بلا صندوق.</summary>
    public async Task<(FinanceOperationResult result, PartnerTransaction? tx)> OpeningAsync(int partnerId, decimal signedAmount, DateTime date, string? notes, int userId)
    {
        if (!await new CashBoxService(_db).IsAdminAsync(userId)) return (FinanceOperationResult.Fail("الأرصدة الافتتاحية للشركاء للأدمن فقط"), null);
        if (signedAmount == 0) return (FinanceOperationResult.Fail("المبلغ لا يمكن أن يكون صفرًا"), null);
        var partner = await _db.Partners.AsNoTracking().FirstOrDefaultAsync(p => p.Id == partnerId);
        if (partner is null) return (FinanceOperationResult.Fail("اختر الشريك"), null);
        await using var dbTx = await _db.Database.BeginTransactionAsync();
        var number = await NextTxNumberAsync(date);
        var (entry, error) = await PostSignedAsync(OpeningRule, signedAmount, date, $"رصيد افتتاحي {number} — {partner.Name}", userId, "PartnerTransactions", null, "PO");
        if (error is not null) return (FinanceOperationResult.Fail(error), null);
        await _db.SaveChangesAsync();
        var t = new PartnerTransaction
        {
            TxNumber = number, PartnerId = partnerId, TxDate = date.Date, Kind = PartnerTxKind.Opening, Amount = signedAmount,
            Notes = Clean(notes), JournalEntryId = entry!.Id, CreatedByUserId = userId
        };
        _db.PartnerTransactions.Add(t);
        await _db.SaveChangesAsync();
        entry.SourceId = t.Id;
        await _db.SaveChangesAsync();
        await dbTx.CommitAsync();
        return (FinanceOperationResult.Ok(), t);
    }

    public async Task<List<PartnerStatementRow>> GetPartnerStatementAsync(int partnerId)
    {
        var raw = await _db.PartnerTransactions.AsNoTracking().Where(t => t.PartnerId == partnerId)
            .OrderBy(t => t.TxDate).ThenBy(t => t.Id)
            .Select(t => new { t.Id, t.TxNumber, t.TxDate, t.Kind, t.Amount, Recon = t.Reconciliation != null ? t.Reconciliation.ReconNumber : null, t.Notes })
            .ToListAsync();
        decimal balance = 0;
        return raw.Select(t =>
        {
            balance += t.Amount;
            return new PartnerStatementRow
            {
                Id = t.Id, TxNumber = t.TxNumber, TxDate = t.TxDate, Kind = t.Kind,
                Credit = t.Amount > 0 ? t.Amount : 0, Debit = t.Amount < 0 ? -t.Amount : 0, Balance = balance, Reference = t.Recon, Notes = t.Notes
            };
        }).ToList();
    }

    public async Task<List<ReconciliationListRow>> GetReconciliationsAsync() =>
        await _db.AssetReconciliations.AsNoTracking().OrderByDescending(r => r.ReconDate).ThenByDescending(r => r.Id)
            .Select(r => new ReconciliationListRow
            {
                Id = r.Id, ReconNumber = r.ReconNumber, ReconDate = r.ReconDate, Valuation = r.FinishedGoodsValuation, NetAssets = r.NetAssets,
                Surplus = r.Surplus, IsBaseline = r.IsBaseline, CreatedBy = r.CreatedByUser.Username
            }).ToListAsync();

    public Task<AssetReconciliation?> GetAsync(int id) =>
        _db.AssetReconciliations.AsNoTracking().Include(r => r.Lines).Include(r => r.CreatedByUser).Include(r => r.PreviousReconciliation)
           .FirstOrDefaultAsync(r => r.Id == id);

    public Task<List<PartnerTransaction>> GetSharesAsync(int reconciliationId) =>
        _db.PartnerTransactions.AsNoTracking().Include(t => t.Partner).Where(t => t.ReconciliationId == reconciliationId).OrderByDescending(t => t.Amount).ToListAsync();

    private async Task<string> NextTxNumberAsync(DateTime date)
    {
        var n = (await _db.Database.SqlQueryRaw<int>("SELECT NEXT VALUE FOR seq_PartnerTx AS [Value]").ToListAsync())[0];
        return $"PT-{date.Year}-{n:D5}";
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];
}
