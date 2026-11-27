using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

public record HomeSpendInput(HomeBoxEntryKind Kind, decimal Amount, DateTime Date, string? Title, string? Beneficiary, string? Notes);

/// <summary>سطر في سجل صندوق المنزل: كل ما دخل وخرج منه بنوعه.</summary>
public class HomeLogRow
{
    public int TxId { get; init; }
    public string TxNumber { get; init; } = "";
    public DateTime TxDate { get; init; }
    /// <summary>التصنيف: الخمس، مساعدات، شراء أصول، سحب شريك، قرض لصندوق عمل، سداد قرض، تحويل فائض، تسوية...</summary>
    public string Category { get; init; } = "";
    public decimal In { get; init; }
    public decimal Out { get; init; }
    public string? Party { get; init; }
    public string? Description { get; init; }
    public string CreatedBy { get; init; } = "";
    public bool IsVoided { get; init; }
    public int? JournalEntryId { get; init; }
    public int? HomeEntryId { get; init; }
    public HomeBoxEntryKind? EntryKind { get; init; }
}

public class HomeLogResult
{
    public List<HomeLogRow> Rows { get; init; } = new();
    public decimal Opening { get; init; }
    public decimal TotalIn { get; init; }
    public decimal TotalOut { get; init; }
    public decimal Closing { get; init; }
    public List<(string Category, decimal In, decimal Out)> ByCategory { get; init; } = new();
}

public class HomeLoanRow
{
    public int Id { get; init; }
    public string LoanNumber { get; init; } = "";
    public string BoxName { get; init; } = "";
    public DateTime LoanDate { get; init; }
    public decimal Amount { get; init; }
    public decimal Repaid { get; init; }
    public decimal Remaining => Amount - Repaid;
    public string Reason { get; init; } = "";
    public HomeLoanStatus Status { get; init; }
    public int DaysOpen { get; init; }
    public string StatusText => Status == HomeLoanStatus.Settled ? "مسدَّد" : Status == HomeLoanStatus.Voided ? "ملغى" : Remaining < Amount ? "مسدَّد جزئيًا" : "قائم";
}

/// <summary>
/// صندوق المنزل: ربح متحقق يُصرف خارج العمل. الخمس والمساعدات وشراء الأصول تُقيَّد بحسابات خاصة لا تدخل كلفة القنينة
/// ولا صافي ربح الشهر ولا رأس المال التشغيلي؛ وتُضاف إلى فائض المطابقة (توزيع أرباح لا خسارة).
/// القروض: صندوق عمل نفدت سيولته يقترض من المنزل (مناقلة + سجل دين)، ويسدّد عند الوفرة. لا أثر على الأرباح.
/// </summary>
public class HomeBoxService
{
    public const string DistributionRule = "HomeDistribution";    // مدين توزيعات المالك من صندوق المنزل / دائن الصندوق
    public const string AssetRule = "HomeAssetPurchase";          // مدين الأصول الثابتة / دائن الصندوق

    private readonly ProjectDbContext _db;
    public HomeBoxService(ProjectDbContext db) => _db = db;

    public static string KindText(HomeBoxEntryKind k) => k switch
    {
        HomeBoxEntryKind.Khums => "الخمس",
        HomeBoxEntryKind.Aid => "مساعدات وتبرعات",
        HomeBoxEntryKind.Asset => "شراء ماكنات وأصول",
        _ => "أخرى"
    };

    /// <summary>الحسابات والقواعد الجديدة إن لم تكن موجودة (للمشاريع القائمة قبل هذه الميزة).</summary>
    public async Task<string?> EnsureSetupAsync()
    {
        async Task<ChartOfAccount> AccountAsync(string code, string name, AccountType type)
        {
            var a = await _db.ChartOfAccounts.FirstOrDefaultAsync(x => x.AccountCode == code);
            if (a is null)
            {
                a = new ChartOfAccount { AccountCode = code, AccountName = name, AccountType = type };
                _db.ChartOfAccounts.Add(a);
                await _db.SaveChangesAsync();
            }
            return a;
        }
        var cash = await _db.ChartOfAccounts.Where(a => a.AccountCode == "1101").Select(a => (int?)a.Id).FirstOrDefaultAsync();
        if (cash is null) return "حساب الصندوق (1101) غير موجود في الدليل المحاسبي";
        var distributions = await AccountAsync("3105", "توزيعات المالك من صندوق المنزل (الخمس والمساعدات)", AccountType.Equity);
        var assets = await AccountAsync("1401", "أصول ثابتة (ماكنات ومعدات) من أرباح مستثمرة", AccountType.Asset);
        var diffs = await AccountAsync("5105", "فروقات جرد الصناديق", AccountType.Expense);
        var wanted = new[]
        {
            (DistributionRule, distributions.Id, cash.Value), (AssetRule, assets.Id, cash.Value),
            (CashBoxService.AdjustGainRule, cash.Value, diffs.Id), (CashBoxService.AdjustLossRule, diffs.Id, cash.Value)
        };
        foreach (var (type, debit, credit) in wanted)
            if (!await _db.AccountMappingRules.AnyAsync(r => r.TransactionType == type))
                _db.AccountMappingRules.Add(new AccountMappingRule { TransactionType = type, DebitAccountId = debit, CreditAccountId = credit });
        await _db.SaveChangesAsync();
        return null;
    }

    public async Task<bool> CanUseAsync(int userId) =>
        await new CashBoxService(_db).IsAdminAsync(userId) || await SpecialPermission.HasAsync(_db, userId, SpecialPermission.HomeBox);

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private async Task<string?> LockErrorAsync(DateTime date) =>
        await new PeriodLockService(_db).GetLockedThroughAsync() is DateTime locked && date.Date <= locked.Date
            ? $"الفترة مقفلة حتى {locked:yyyy/MM/dd}" : null;

    // ============================ الصرف خارج العمل ============================

    public async Task<(FinanceOperationResult result, HomeBoxEntry? entry)> SpendAsync(HomeSpendInput input, int userId)
    {
        if (!await CanUseAsync(userId)) return (FinanceOperationResult.Fail("الصرف من صندوق المنزل لمن يملك «صندوق المنزل وقروضه»"), null);
        if (input.Amount <= 0) return (FinanceOperationResult.Fail("المبلغ يجب أن يكون أكبر من صفر"), null);
        if (input.Kind is HomeBoxEntryKind.Other or HomeBoxEntryKind.Asset or HomeBoxEntryKind.Aid && string.IsNullOrWhiteSpace(input.Title) && string.IsNullOrWhiteSpace(input.Beneficiary))
            return (FinanceOperationResult.Fail("اكتب البيان أو الجهة المستفيدة (مثل اسم الماكنة أو المستفيد)"), null);
        if (await LockErrorAsync(input.Date) is { } lockErr) return (FinanceOperationResult.Fail(lockErr), null);
        var setup = await EnsureSetupAsync();
        if (setup is not null) return (FinanceOperationResult.Fail(setup), null);

        var cash = new CashBoxService(_db);
        var home = await new FinalAccountsService(_db).HomeBoxAsync();
        var balance = await cash.GetBalanceAsync(home.Id);
        if (balance < input.Amount) return (FinanceOperationResult.Fail($"رصيد صندوق المنزل غير كافٍ: المتاح {balance:N0} د.ع"), null);

        await using var tx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        var year = input.Date.Year;
        var count = await _db.HomeBoxEntries.CountAsync(e => e.EntryDate.Year == year);
        var label = KindText(input.Kind);
        var detail = Clean(input.Title) ?? Clean(input.Beneficiary);
        var text = $"صندوق المنزل — {label}" + (detail is null ? "" : $": {detail}");
        var entry = new HomeBoxEntry
        {
            EntryNumber = $"HB-{year}-{count + 1:D5}", EntryDate = input.Date.Date, Kind = input.Kind, Amount = input.Amount,
            Title = Clean(input.Title), Beneficiary = Clean(input.Beneficiary), Notes = Clean(input.Notes), CashBoxId = home.Id, CreatedByUserId = userId
        };
        _db.HomeBoxEntries.Add(entry);
        await _db.SaveChangesAsync();

        var (je, error) = await LedgerHelper.PostJournalAsync(_db, input.Kind == HomeBoxEntryKind.Asset ? AssetRule : DistributionRule, input.Amount,
                                                              input.Date, JournalEntryType.AutoVoucher, text, userId, "HomeBoxEntries", entry.Id, "HB");
        if (error is not null)
        {
            if (tx is not null) await tx.RollbackAsync();
            _db.ChangeTracker.Clear();
            return (FinanceOperationResult.Fail(error), null);
        }
        await _db.SaveChangesAsync();
        var cashTx = new CashBoxTransaction
        {
            TxNumber = await cash.NextNumberAsync(input.Date), CashBoxId = home.Id, TxDate = input.Date.Date, TxType = CashBoxTxType.HomeSpend,
            Amount = -input.Amount, PartyName = entry.Beneficiary, Description = text, ReferenceTable = "HomeBoxEntries", ReferenceId = entry.Id,
            JournalEntryId = je!.Id, CreatedByUserId = userId
        };
        _db.CashBoxTransactions.Add(cashTx);
        await _db.SaveChangesAsync();
        (entry.JournalEntryId, entry.CashBoxTxId) = (je.Id, cashTx.Id);
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Insert", "HomeBoxEntries", entry.Id, $"{entry.EntryNumber}: {text} — {input.Amount:N0} د.ع");
        if (tx is not null) await tx.CommitAsync();
        return (FinanceOperationResult.Ok(), entry);
    }

    /// <summary>إلغاء صرف بقيد عكسي، وتبقى الحركة ظاهرة مشطوبة في السجل.</summary>
    public async Task<FinanceOperationResult> VoidEntryAsync(int entryId, string reason, int userId)
    {
        if (!await CanUseAsync(userId)) return FinanceOperationResult.Fail("إلغاء الصرف لمن يملك «صندوق المنزل وقروضه»");
        if (string.IsNullOrWhiteSpace(reason)) return FinanceOperationResult.Fail("اكتب سبب الإلغاء");
        var e = await _db.HomeBoxEntries.FirstOrDefaultAsync(x => x.Id == entryId);
        if (e is null) return FinanceOperationResult.Fail("السجل غير موجود");
        if (e.IsVoided) return FinanceOperationResult.Fail("ملغى مسبقًا");
        if (await LockErrorAsync(e.EntryDate) is { } lockErr) return FinanceOperationResult.Fail(lockErr);

        await using var tx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        if (e.JournalEntryId is int jeId)
        {
            var lines = await _db.JournalEntryLines.AsNoTracking().Where(l => l.JournalEntryId == jeId).ToListAsync();
            var count = await _db.JournalEntries.CountAsync();
            var rev = new JournalEntry
            {
                EntryNumber = $"HBV-{count + 1:D5}", EntryDate = DateTime.Today, EntryType = JournalEntryType.AutoVoucher,
                Description = $"إلغاء {e.EntryNumber} — {reason.Trim()}", CreatedByUserId = userId, IsPosted = true, SourceTable = "HomeBoxEntries", SourceId = e.Id
            };
            foreach (var l in lines)
                rev.Lines.Add(new JournalEntryLine { AccountId = l.AccountId, Debit = l.Credit, Credit = l.Debit, Description = "عكس: " + l.Description });
            _db.JournalEntries.Add(rev);
        }
        if (e.CashBoxTxId is int txId && await _db.CashBoxTransactions.FirstOrDefaultAsync(t => t.Id == txId) is { } ct)
            (ct.IsVoided, ct.VoidReason, ct.ModifiedByUserId, ct.ModifiedAt) = (true, reason.Trim(), userId, DateTime.UtcNow);
        (e.IsVoided, e.VoidReason) = (true, reason.Trim());
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Void", "HomeBoxEntries", e.Id, $"إلغاء {e.EntryNumber} ({e.Amount:N0} د.ع) — {reason.Trim()}");
        if (tx is not null) await tx.CommitAsync();
        return FinanceOperationResult.Ok();
    }

    // ============================ السجل ============================

    /// <summary>سجل صندوق المنزل في فترة، بالفلترة بالنوع والنص والمبلغ؛ الأرصدة الافتتاحية والختامية تخص الفترة كاملة.</summary>
    public async Task<HomeLogResult> LogAsync(DateTime from, DateTime to, string? category = null, string? text = null, decimal? minAmount = null, decimal? maxAmount = null)
    {
        var home = await _db.CashBoxes.AsNoTracking().Where(b => b.BoxType == CashBoxType.Home).OrderBy(b => b.Id).FirstOrDefaultAsync();
        if (home is null) return new HomeLogResult();
        var cash = new CashBoxService(_db);
        var (opening, tx) = await cash.GetStatementAsync(home.Id, from, to, includeVoided: true);
        var entries = await _db.HomeBoxEntries.AsNoTracking().Where(e => e.CashBoxId == home.Id && e.EntryDate >= from.Date && e.EntryDate <= to.Date)
            .ToDictionaryAsync(e => e.CashBoxTxId ?? 0, e => e);
        var txMeta = await _db.CashBoxTransactions.AsNoTracking().Where(t => t.CashBoxId == home.Id && t.TxDate >= from.Date && t.TxDate <= to.Date)
            .Select(t => new { t.Id, t.JournalEntryId, t.TransferGroup }).ToDictionaryAsync(t => t.Id);
        var loanOut = (await _db.HomeLoans.AsNoTracking().Where(l => l.TransferGroup != null).Select(l => l.TransferGroup!.Value).ToListAsync()).ToHashSet();
        var loanIn = (await _db.HomeLoanRepayments.AsNoTracking().Where(l => l.TransferGroup != null).Select(l => l.TransferGroup!.Value).ToListAsync()).ToHashSet();

        var rows = new List<HomeLogRow>();
        foreach (var r in tx)
        {
            entries.TryGetValue(r.Id, out var he);
            var meta = txMeta.GetValueOrDefault(r.Id);
            var group = meta?.TransferGroup;
            var cat = he is not null ? KindText(he.Kind)
                : r.TxType == CashBoxTxType.TransferOut && group is Guid g1 && loanOut.Contains(g1) ? "قرض لصندوق عمل"
                : r.TxType == CashBoxTxType.TransferIn && group is Guid g2 && loanIn.Contains(g2) ? "سداد قرض"
                : r.TxType == CashBoxTxType.TransferIn ? "تحويل فائض / مناقلة واردة"
                : r.TxType == CashBoxTxType.TransferOut ? "مناقلة صادرة"
                : r.TypeLabel;
            rows.Add(new HomeLogRow
            {
                TxId = r.Id, TxNumber = r.TxNumber, TxDate = r.TxDate, Category = cat, In = r.In, Out = r.Out,
                Party = r.PartyName ?? r.CounterBox, Description = r.Description, CreatedBy = r.CreatedBy, IsVoided = r.IsVoided,
                JournalEntryId = meta?.JournalEntryId, HomeEntryId = he?.Id, EntryKind = he?.Kind
            });
        }
        IEnumerable<HomeLogRow> q = rows;
        if (!string.IsNullOrWhiteSpace(category)) q = q.Where(r => r.Category == category);
        if (!string.IsNullOrWhiteSpace(text))
        {
            var t = text.Trim();
            q = q.Where(r => (r.Party?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false) || (r.Description?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
                             || r.Category.Contains(t) || r.TxNumber.Contains(t, StringComparison.OrdinalIgnoreCase));
        }
        if (minAmount is decimal lo) q = q.Where(r => Math.Max(r.In, r.Out) >= lo);
        if (maxAmount is decimal hi) q = q.Where(r => Math.Max(r.In, r.Out) <= hi);
        var shown = q.ToList();
        var live = shown.Where(r => !r.IsVoided).ToList();
        return new HomeLogResult
        {
            Rows = shown, Opening = opening, TotalIn = live.Sum(r => r.In), TotalOut = live.Sum(r => r.Out), Closing = opening + rows.Where(r => !r.IsVoided).Sum(r => r.In - r.Out),
            ByCategory = live.GroupBy(r => r.Category).Select(g => (g.Key, g.Sum(r => r.In), g.Sum(r => r.Out))).OrderByDescending(x => x.Item3).ToList()
        };
    }

    /// <summary>أسماء التصنيفات لقائمة الفلترة.</summary>
    public static readonly string[] Categories =
    {
        "الخمس", "مساعدات وتبرعات", "شراء ماكنات وأصول", "أخرى", "سحب أرباح شريك", "قرض لصندوق عمل", "سداد قرض", "تحويل فائض / مناقلة واردة"
    };

    // ============================ القروض ============================

    private async Task<string> NextLoanNumberAsync(DateTime date)
    {
        var count = await _db.HomeLoans.CountAsync(l => l.LoanDate.Year == date.Year);
        return $"HL-{date.Year}-{count + 1:D5}";
    }

    public async Task<decimal> RepaidAsync(int loanId) =>
        await _db.HomeLoanRepayments.Where(r => r.LoanId == loanId).SumAsync(r => (decimal?)r.Amount) ?? 0;

    /// <summary>مجموع ما عند صناديق العمل من قروض المنزل غير المسدَّدة (يُستبعد من فائض رأس المال: يبقى ملكًا للمنزل).</summary>
    public async Task<decimal> OutstandingTotalAsync()
    {
        var open = await _db.HomeLoans.AsNoTracking().Where(l => l.Status == HomeLoanStatus.Open).Select(l => new { l.Id, l.Amount }).ToListAsync();
        if (open.Count == 0) return 0;
        var ids = open.Select(o => o.Id).ToList();
        var repaid = await _db.HomeLoanRepayments.AsNoTracking().Where(r => ids.Contains(r.LoanId)).GroupBy(r => r.LoanId)
            .Select(g => new { g.Key, Sum = g.Sum(r => r.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum);
        return open.Sum(o => o.Amount - repaid.GetValueOrDefault(o.Id));
    }

    public async Task<(FinanceOperationResult result, HomeLoan? loan)> LendAsync(int toBoxId, decimal amount, DateTime date, string? reason, int userId)
    {
        if (!await CanUseAsync(userId)) return (FinanceOperationResult.Fail("الإقراض من صندوق المنزل لمن يملك «صندوق المنزل وقروضه»"), null);
        if (amount <= 0) return (FinanceOperationResult.Fail("المبلغ يجب أن يكون أكبر من صفر"), null);
        if (string.IsNullOrWhiteSpace(reason)) return (FinanceOperationResult.Fail("اكتب سبب الاقتراض"), null);
        var to = await _db.CashBoxes.AsNoTracking().FirstOrDefaultAsync(b => b.Id == toBoxId);
        if (to is null || !to.IsActive) return (FinanceOperationResult.Fail("صندوق العمل غير موجود أو موقوف"), null);
        if (to.BoxType is CashBoxType.Home or CashBoxType.Bank or CashBoxType.Cards)
            return (FinanceOperationResult.Fail("يُقرَض صندوق عمل نقدي (الرئيسي أو صندوق مستخدم)"), null);
        if (await LockErrorAsync(date) is { } lockErr) return (FinanceOperationResult.Fail(lockErr), null);

        var home = await new FinalAccountsService(_db).HomeBoxAsync();
        await using var tx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        var text = $"قرض من صندوق المنزل إلى {to.Name} — {reason.Trim()}";
        var (res, outTx) = await new CashBoxService(_db).TransferAsync(home.Id, toBoxId, amount, date, text, userId, skipOwnerCheck: true);
        if (!res.Success) { if (tx is not null) await tx.RollbackAsync(); return (res, null); }
        var loan = new HomeLoan
        {
            LoanNumber = await NextLoanNumberAsync(date), ToCashBoxId = toBoxId, HomeCashBoxId = home.Id, Amount = amount, LoanDate = date.Date,
            Reason = reason.Trim(), Status = HomeLoanStatus.Open, TransferGroup = outTx!.TransferGroup, CreatedByUserId = userId
        };
        _db.HomeLoans.Add(loan);
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Insert", "HomeLoans", loan.Id, $"{loan.LoanNumber}: {text} — {amount:N0} د.ع");
        if (tx is not null) await tx.CommitAsync();
        return (FinanceOperationResult.Ok(), loan);
    }

    /// <summary>سداد (كامل أو جزئي) من صندوق عمل إلى صندوق المنزل. لصاحب الصندوق أو من يملك صلاحية المنزل.</summary>
    public async Task<FinanceOperationResult> RepayAsync(int loanId, decimal amount, int fromBoxId, DateTime date, string? notes, int userId)
    {
        if (amount <= 0) return FinanceOperationResult.Fail("المبلغ يجب أن يكون أكبر من صفر");
        var loan = await _db.HomeLoans.FirstOrDefaultAsync(l => l.Id == loanId);
        if (loan is null || loan.Status == HomeLoanStatus.Voided) return FinanceOperationResult.Fail("القرض غير موجود");
        if (loan.Status == HomeLoanStatus.Settled) return FinanceOperationResult.Fail("القرض مسدَّد بالكامل");
        var from = await _db.CashBoxes.AsNoTracking().FirstOrDefaultAsync(b => b.Id == fromBoxId);
        if (from is null || !from.IsActive) return FinanceOperationResult.Fail("صندوق السداد غير موجود أو موقوف");
        if (from.BoxType == CashBoxType.Home) return FinanceOperationResult.Fail("السداد من صندوق عمل لا من المنزل نفسه");
        if (!await CanUseAsync(userId) && from.OwnerUserId != userId) return FinanceOperationResult.Fail("السداد لمن يملك «صندوق المنزل وقروضه» أو لصاحب الصندوق");
        var remaining = loan.Amount - await RepaidAsync(loanId);
        if (amount > remaining) return FinanceOperationResult.Fail($"المتبقي من القرض {remaining:N0} د.ع فقط");
        if (await LockErrorAsync(date) is { } lockErr) return FinanceOperationResult.Fail(lockErr);

        await using var tx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        var text = $"سداد قرض {loan.LoanNumber} إلى صندوق المنزل" + (string.IsNullOrWhiteSpace(notes) ? "" : $" — {notes.Trim()}");
        var (res, outTx) = await new CashBoxService(_db).TransferAsync(fromBoxId, loan.HomeCashBoxId, amount, date, text, userId, skipOwnerCheck: true);
        if (!res.Success) { if (tx is not null) await tx.RollbackAsync(); return res; }
        _db.HomeLoanRepayments.Add(new HomeLoanRepayment
        {
            LoanId = loanId, RepayDate = date.Date, Amount = amount, FromCashBoxId = fromBoxId, TransferGroup = outTx!.TransferGroup,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(), CreatedByUserId = userId
        });
        if (amount == remaining) (loan.Status, loan.SettledAt) = (HomeLoanStatus.Settled, DateTime.UtcNow);
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Insert", "HomeLoanRepayments", loanId, $"{text} — {amount:N0} د.ع");
        if (tx is not null) await tx.CommitAsync();
        return FinanceOperationResult.Ok();
    }

    public async Task<List<HomeLoanRow>> LoansAsync(bool openOnly = false)
    {
        var loans = await _db.HomeLoans.AsNoTracking().Where(l => l.Status != HomeLoanStatus.Voided && (!openOnly || l.Status == HomeLoanStatus.Open))
            .OrderBy(l => l.Status).ThenByDescending(l => l.LoanDate).ThenByDescending(l => l.Id)
            .Select(l => new { l.Id, l.LoanNumber, Box = l.ToCashBox.Name, l.LoanDate, l.Amount, l.Reason, l.Status }).ToListAsync();
        var ids = loans.Select(l => l.Id).ToList();
        var repaid = await _db.HomeLoanRepayments.AsNoTracking().Where(r => ids.Contains(r.LoanId)).GroupBy(r => r.LoanId)
            .Select(g => new { g.Key, Sum = g.Sum(r => r.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum);
        return loans.Select(l => new HomeLoanRow
        {
            Id = l.Id, LoanNumber = l.LoanNumber, BoxName = l.Box, LoanDate = l.LoanDate, Amount = l.Amount, Repaid = repaid.GetValueOrDefault(l.Id),
            Reason = l.Reason, Status = l.Status, DaysOpen = l.Status == HomeLoanStatus.Open ? Math.Max(0, (DateTime.Today - l.LoanDate).Days) : 0
        }).ToList();
    }
}
