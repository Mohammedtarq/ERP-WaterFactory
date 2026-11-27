using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

public record CustodyRow(int Id, string Number, int EmployeeId, string Employee, string? JobTitle, string Purpose, CustodyStatus Status, DateTime IssuedAt,
                         decimal Issued, decimal Spent, decimal Returned, decimal TargetAmount, int AgeDays, bool IsLate, bool IsLow, bool IsMine = false)
{
    public decimal Balance => Issued - Spent - Returned;
    public string StatusText => Status switch { CustodyStatus.Active => IsLate ? "متأخرة" : "فعّالة", CustodyStatus.Settled => "مسوّاة", _ => "ملغاة" };
}

public record CustodyTxRow(int Id, DateTime Date, CustodyTxKind Kind, string KindText, string? Description, string? Category, decimal Amount, decimal Balance,
                           string? ReceiptNumber, bool HasReceiptPhoto, bool IsVoided, string? VoidReason, bool IsModified, string User);

public record CustodyDetail(CustodyRow Header, List<CustodyTxRow> Rows, List<(string Category, decimal Amount)> ByCategory, string IssuedBy, string? CashBox);

public record CustodyExpenseInput(int CustodyId, decimal Amount, int CategoryId, string? Description, string? ReceiptNumber, DateTime Date,
                                  bool HasReceiptPhoto = false, Guid? ClientId = null);

/// <summary>
/// العهد النقدية (71_custody.sql): صرف من صندوق (مدين «عهد الموظفين» / دائن الصندوق)، مصروف بباب (مدين الباب / دائن العهد)،
/// إعادة المتبقي (مدين الصندوق / دائن العهد). التجديد يعبّئ نفس العهدة، والتعديل والإلغاء بسبب وقيد عكسي وسجل تدقيق، وتبقى الحركة ظاهرة.
/// الصرف والتعديل والإلغاء بصلاحية «إدارة العهد»؛ وصاحب العهدة يسجّل مصاريفه بنفسه (من هاتفه) على عهدته فقط وضمن رصيدها.
/// </summary>
public class CustodyService
{
    public const string IssueRule = "CustodyIssue";            // مدين عهد الموظفين / دائن الصندوق
    public const string ReturnRule = "CustodyReturn";          // مدين الصندوق / دائن عهد الموظفين
    public const string ExpenseRule = "CustodyExpense";        // مدين مصروفات عمومية / دائن عهد الموظفين
    public const string NonOperatingRule = "CustodyExpenseNonOp"; // مدين مصروفات غير تشغيلية / دائن عهد الموظفين
    public const string AccountCode = "1105";

    private readonly ProjectDbContext _db;
    public CustodyService(ProjectDbContext db) => _db = db;

    // ============================ الإعدادات ============================

    public async Task<RepAppSetting> SettingsAsync() => await _db.RepAppSettings.AsNoTracking().FirstOrDefaultAsync() ?? new RepAppSetting();

    /// <summary>تنبيهات العهد: أيام بلا تسوية، نسبة الرصيد المنخفض، ومبلغ لا يُقبل بعده مصروف بلا وصل دون تنبيه.</summary>
    public async Task<FinanceOperationResult> SaveSettingsAsync(int alertDays, int lowPercent, decimal receiptAbove, int userId)
    {
        if (!await CanManageAsync(userId)) return FinanceOperationResult.Fail("لا تملك صلاحية إدارة العهد");
        if (alertDays is < 1 or > 90) return FinanceOperationResult.Fail("أيام التنبيه بين 1 و90");
        if (lowPercent is < 1 or > 90) return FinanceOperationResult.Fail("نسبة الرصيد المنخفض بين 1 و90");
        if (receiptAbove < 0) return FinanceOperationResult.Fail("مبلغ الوصل لا يكون سالبًا");
        var st = await _db.RepAppSettings.FirstOrDefaultAsync();
        if (st is null) _db.RepAppSettings.Add(st = new RepAppSetting());
        (st.CustodyAlertDays, st.CustodyLowPercent, st.CustodyReceiptAbove) = (alertDays, lowPercent, receiptAbove);
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Edit", "RepAppSettings", 1, $"تنبيهات العهد: {alertDays} يوم، رصيد منخفض {lowPercent}%، وصل فوق {receiptAbove:N0}");
        return FinanceOperationResult.Ok();
    }

    // ============================ التهيئة ============================

    /// <summary>حساب «عهد الموظفين» وقواعده إن لم تكن موجودة (للمشاريع القائمة قبل هذه الميزة).</summary>
    public async Task<string?> EnsureSetupAsync()
    {
        var account = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.AccountCode == AccountCode);
        if (account is null)
        {
            account = new ChartOfAccount { AccountCode = AccountCode, AccountName = "عهد الموظفين", AccountType = AccountType.Asset };
            _db.ChartOfAccounts.Add(account);
            await _db.SaveChangesAsync();
        }
        async Task<int?> IdOf(string code) => await _db.ChartOfAccounts.Where(a => a.AccountCode == code).Select(a => (int?)a.Id).FirstOrDefaultAsync();
        var cash = await IdOf("1101");
        var general = await IdOf("5101");
        var nonOp = await IdOf("5104") ?? general;
        if (cash is null || general is null) return "حسابا الصندوق (1101) والمصروفات (5101) غير موجودين في الدليل المحاسبي";
        var wanted = new[]
        {
            (IssueRule, account.Id, cash.Value), (ReturnRule, cash.Value, account.Id),
            (ExpenseRule, general.Value, account.Id), (NonOperatingRule, nonOp!.Value, account.Id)
        };
        foreach (var (type, debit, credit) in wanted)
            if (!await _db.AccountMappingRules.AnyAsync(r => r.TransactionType == type))
                _db.AccountMappingRules.Add(new AccountMappingRule { TransactionType = type, DebitAccountId = debit, CreditAccountId = credit });
        await _db.SaveChangesAsync();
        return null;
    }

    private async Task<bool> CanManageAsync(int userId) =>
        await SpecialPermission.HasAsync(_db, userId, SpecialPermission.Custody);

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ============================ الأرصدة والقوائم ============================

    public async Task<decimal> BalanceAsync(int custodyId)
    {
        var rows = await _db.CustodyTransactions.AsNoTracking().Where(t => t.CustodyId == custodyId && !t.IsVoided)
            .GroupBy(t => t.Kind).Select(g => new { g.Key, Sum = g.Sum(x => x.Amount) }).ToListAsync();
        return rows.Where(r => r.Key == CustodyTxKind.Issue).Sum(r => r.Sum) - rows.Where(r => r.Key != CustodyTxKind.Issue).Sum(r => r.Sum);
    }

    public async Task<List<CustodyRow>> ListAsync(CustodyStatus? status = null, int? employeeId = null, DateTime? from = null, DateTime? to = null)
    {
        var settings = await _db.RepAppSettings.AsNoTracking().FirstOrDefaultAsync() ?? new RepAppSetting();
        var q = _db.Custodies.AsNoTracking().Where(c => status == null || c.Status == status);
        if (employeeId is int e) q = q.Where(c => c.EmployeeId == e);
        if (from is DateTime f) q = q.Where(c => c.IssuedAt >= f.Date);
        if (to is DateTime t) q = q.Where(c => c.IssuedAt <= t.Date);
        var list = await q.OrderByDescending(c => c.Id).Select(c => new
        {
            c.Id, c.CustodyNumber, c.EmployeeId, Employee = c.Employee.FullName, c.Employee.JobTitle, c.Purpose, c.Status, c.IssuedAt, c.TargetAmount,
            Issued = c.Transactions.Where(x => !x.IsVoided && x.Kind == CustodyTxKind.Issue).Sum(x => (decimal?)x.Amount) ?? 0,
            Spent = c.Transactions.Where(x => !x.IsVoided && x.Kind == CustodyTxKind.Expense).Sum(x => (decimal?)x.Amount) ?? 0,
            Returned = c.Transactions.Where(x => !x.IsVoided && x.Kind == CustodyTxKind.Return).Sum(x => (decimal?)x.Amount) ?? 0,
            FirstIssue = c.Transactions.Where(x => !x.IsVoided && x.Kind == CustodyTxKind.Issue).OrderBy(x => x.Id).Select(x => (decimal?)x.Amount).FirstOrDefault()
        }).ToListAsync();
        return list.Select(c =>
        {
            var age = (int)(DateTime.Today - c.IssuedAt.Date).TotalDays;
            var balance = c.Issued - c.Spent - c.Returned;
            var active = c.Status == CustodyStatus.Active;
            return new CustodyRow(c.Id, c.CustodyNumber, c.EmployeeId, c.Employee, c.JobTitle, c.Purpose, c.Status, c.IssuedAt, c.Issued, c.Spent, c.Returned,
                                  c.TargetAmount ?? c.FirstIssue ?? c.Issued, age, active && balance > 0 && age >= settings.CustodyAlertDays,
                                  active && c.Issued > 0 && balance > 0 && balance * 100 < c.Issued * settings.CustodyLowPercent);
        }).ToList();
    }

    public async Task<CustodyDetail?> DetailAsync(int custodyId)
    {
        var header = (await ListAsync()).FirstOrDefault(c => c.Id == custodyId);
        if (header is null) return null;
        var raw = await _db.CustodyTransactions.AsNoTracking().Where(t => t.CustodyId == custodyId).OrderBy(t => t.TxDate).ThenBy(t => t.Id)
            .Select(t => new
            {
                t.Id, t.TxDate, t.Kind, t.Description, Category = t.Category != null ? t.Category.Name : null, t.Amount, t.ReceiptNumber, t.HasReceiptPhoto,
                t.IsVoided, t.VoidReason, Modified = t.ModifiedAt != null && !t.IsVoided, User = t.CreatedByUser.Username
            }).ToListAsync();
        var running = 0m;
        var rows = new List<CustodyTxRow>();
        foreach (var t in raw)
        {
            if (!t.IsVoided) running += t.Kind == CustodyTxKind.Issue ? t.Amount : -t.Amount;
            rows.Add(new CustodyTxRow(t.Id, t.TxDate, t.Kind, KindText(t.Kind), t.Description, t.Category, t.Amount, running, t.ReceiptNumber, t.HasReceiptPhoto,
                                      t.IsVoided, t.VoidReason, t.Modified, t.User));
        }
        var byCategory = rows.Where(r => !r.IsVoided && r.Kind == CustodyTxKind.Expense)
            .GroupBy(r => r.Category ?? "بلا باب").Select(g => (g.Key, g.Sum(x => x.Amount))).OrderByDescending(g => g.Item2).ToList();
        var info = await _db.Custodies.AsNoTracking().Where(c => c.Id == custodyId).Select(c => new { By = c.CreatedByUser.Username, Box = c.CashBox != null ? c.CashBox.Name : null }).FirstAsync();
        return new CustodyDetail(header, rows, byCategory, info.By, info.Box);
    }

    public static string KindText(CustodyTxKind k) => k switch { CustodyTxKind.Issue => "صرف عهدة", CustodyTxKind.Expense => "مصروف", _ => "إعادة للخزينة" };

    /// <summary>العهد الفعّالة لموظف (رصيدها مفتوح): تعرضها شاشة الهاتف ويختار منها مصروفه.</summary>
    public async Task<List<CustodyRow>> ActiveOfAsync(int employeeId) => await ListAsync(CustodyStatus.Active, employeeId);

    // ============================ الصرف والتجديد ============================

    private async Task<int?> ResolveBoxAsync(int userId, int? cashBoxId) =>
        cashBoxId ?? await _db.CashBoxes.AsNoTracking().Where(b => b.IsActive && (b.OwnerUserId == userId || b.IsDefault || b.BoxType == CashBoxType.Main))
            .OrderBy(b => b.OwnerUserId == userId ? 0 : b.IsDefault ? 1 : 2).ThenBy(b => b.Id).Select(b => (int?)b.Id).FirstOrDefaultAsync();

    /// <summary>صرف عهدة جديدة لموظف من صندوق. المبلغ يُخصم من رصيد الصندوق فلا يجعله سالبًا.</summary>
    public async Task<(FinanceOperationResult result, int? custodyId)> IssueAsync(int employeeId, decimal amount, string purpose, DateTime date, int userId,
                                                                                    int? cashBoxId = null, decimal? targetAmount = null)
    {
        if (!await CanManageAsync(userId)) return (FinanceOperationResult.Fail("لا تملك صلاحية إدارة العهد"), null);
        if (amount <= 0) return (FinanceOperationResult.Fail("المبلغ يجب أن يكون أكبر من صفر"), null);
        if (string.IsNullOrWhiteSpace(purpose)) return (FinanceOperationResult.Fail("اكتب الغرض من العهدة"), null);
        var emp = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId && e.IsActive);
        if (emp is null) return (FinanceOperationResult.Fail("اختر الموظف"), null);
        if (await EnsureSetupAsync() is string setup) return (FinanceOperationResult.Fail(setup), null);

        var count = await _db.Custodies.CountAsync();
        var custody = new Custody
        {
            CustodyNumber = $"H-{count + 1:D4}", EmployeeId = employeeId, Purpose = purpose.Trim(), Status = CustodyStatus.Active,
            IssuedAt = date.Date, TargetAmount = targetAmount ?? amount, CreatedByUserId = userId
        };
        var own = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            custody.CashBoxId = await ResolveBoxAsync(userId, cashBoxId);
            _db.Custodies.Add(custody);
            await _db.SaveChangesAsync();
            var r = await PostIssueAsync(custody, amount, date, userId, $"عهدة {custody.CustodyNumber}: {custody.Purpose}", null);
            if (!r.Success) { if (own is not null) await own.RollbackAsync(); _db.ChangeTracker.Clear(); return (r, null); }
            if (own is not null) await own.CommitAsync();
            return (FinanceOperationResult.Ok(), custody.Id);
        }
        catch (DbUpdateException ex) when (FinanceService.BusinessError(ex) is string msg)
        {
            if (own is not null) await own.RollbackAsync();
            _db.ChangeTracker.Clear();
            return (FinanceOperationResult.Fail(msg), null);
        }
        finally { if (own is not null) await own.DisposeAsync(); }
    }

    /// <summary>تعبئة عهدة قائمة بمبلغ (تجديد). تعود العهدة المسوّاة فعّالة.</summary>
    public async Task<FinanceOperationResult> TopUpAsync(int custodyId, decimal amount, DateTime date, int userId, int? cashBoxId = null, string? note = null)
    {
        if (!await CanManageAsync(userId)) return FinanceOperationResult.Fail("لا تملك صلاحية إدارة العهد");
        if (amount <= 0) return FinanceOperationResult.Fail("المبلغ يجب أن يكون أكبر من صفر");
        var c = await _db.Custodies.FirstOrDefaultAsync(x => x.Id == custodyId);
        if (c is null) return FinanceOperationResult.Fail("العهدة غير موجودة");
        if (c.Status == CustodyStatus.Voided) return FinanceOperationResult.Fail("العهدة ملغاة");
        if (await EnsureSetupAsync() is string setup) return FinanceOperationResult.Fail(setup);
        var own = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            c.CashBoxId = await ResolveBoxAsync(userId, cashBoxId) ?? c.CashBoxId;
            var r = await PostIssueAsync(c, amount, date, userId, $"تجديد عهدة {c.CustodyNumber}" + (Clean(note) is string n ? $" — {n}" : ""), Clean(note));
            if (!r.Success) { if (own is not null) await own.RollbackAsync(); _db.ChangeTracker.Clear(); return r; }
            if (c.Status == CustodyStatus.Settled) (c.Status, c.ClosedAt, c.ClosedByUserId) = (CustodyStatus.Active, null, null);
            await _db.SaveChangesAsync();
            await new AuditService(_db).LogAsync(userId, "Edit", "Custodies", c.Id, $"تجديد {c.CustodyNumber} بمبلغ {amount:N0}");
            if (own is not null) await own.CommitAsync();
            return FinanceOperationResult.Ok();
        }
        catch (DbUpdateException ex) when (FinanceService.BusinessError(ex) is string msg)
        {
            if (own is not null) await own.RollbackAsync();
            _db.ChangeTracker.Clear();
            return FinanceOperationResult.Fail(msg);
        }
        finally { if (own is not null) await own.DisposeAsync(); }
    }

    /// <summary>تجديد إلى المبلغ المعتاد: يُصرف الفرق بين المبلغ المعتاد والرصيد الحالي فقط.</summary>
    public async Task<FinanceOperationResult> RenewToTargetAsync(int custodyId, DateTime date, int userId, int? cashBoxId = null)
    {
        var row = (await ListAsync()).FirstOrDefault(c => c.Id == custodyId);
        if (row is null) return FinanceOperationResult.Fail("العهدة غير موجودة");
        var diff = row.TargetAmount - row.Balance;
        if (diff <= 0) return FinanceOperationResult.Fail($"رصيد العهدة ({row.Balance:N0}) لم ينقص عن المبلغ المعتاد ({row.TargetAmount:N0})");
        return await TopUpAsync(custodyId, diff, date, userId, cashBoxId, "تجديد إلى المبلغ المعتاد");
    }

    private async Task<FinanceOperationResult> PostIssueAsync(Custody c, decimal amount, DateTime date, int userId, string text, string? note)
    {
        if (c.CashBoxId is int boxId)
        {
            var available = await new CashBoxService(_db).GetBalanceAsync(boxId);
            if (amount > available) return FinanceOperationResult.Fail($"رصيد الصندوق ({available:N0} د.ع) لا يكفي لصرف {amount:N0} د.ع");
        }
        var tx = new CustodyTransaction { CustodyId = c.Id, TxDate = date.Date, Kind = CustodyTxKind.Issue, Amount = amount, Description = note ?? c.Purpose, CreatedByUserId = userId };
        _db.CustodyTransactions.Add(tx);
        await _db.SaveChangesAsync();
        var (je, error) = await LedgerHelper.PostJournalAsync(_db, IssueRule, amount, date, JournalEntryType.AutoVoucher, text, userId, "CustodyTransactions", tx.Id, "CU");
        if (error is not null) return FinanceOperationResult.Fail(error);
        await _db.SaveChangesAsync();
        tx.JournalEntryId = je!.Id;
        await _db.SaveChangesAsync();
        await new CashBoxService(_db).RecordAutoAsync(userId, CashBoxTxType.CustodyIssue, -amount, date, "CustodyTransactions", tx.Id,
                                                      await _db.Employees.Where(e => e.Id == c.EmployeeId).Select(e => e.FullName).FirstAsync(), text, je.Id, c.CashBoxId);
        tx.CashBoxTxId = await _db.CashBoxTransactions.Where(t => t.ReferenceTable == "CustodyTransactions" && t.ReferenceId == tx.Id).Select(t => (int?)t.Id).FirstOrDefaultAsync();
        await _db.SaveChangesAsync();
        return FinanceOperationResult.Ok();
    }

    // ============================ المصروف ============================

    /// <summary>
    /// مصروف من العهدة. من المكتب بصلاحية «إدارة العهد»؛ ومن الهاتف يمرّر <paramref name="ownerEmployeeId"/> فيقتصر على عهدة صاحبها.
    /// لا يتجاوز رصيد العهدة، ويظهر في مصاريف الباب (قيد: مدين الباب / دائن عهد الموظفين).
    /// </summary>
    public async Task<(FinanceOperationResult result, bool duplicate)> AddExpenseAsync(CustodyExpenseInput input, int userId, int? ownerEmployeeId = null)
    {
        if (input.ClientId is Guid cid && await _db.CustodyTransactions.AnyAsync(t => t.ClientId == cid)) return (FinanceOperationResult.Ok(), true);
        if (input.Amount <= 0) return (FinanceOperationResult.Fail("المبلغ يجب أن يكون أكبر من صفر"), false);
        var c = await _db.Custodies.Include(x => x.Employee).FirstOrDefaultAsync(x => x.Id == input.CustodyId);
        if (c is null) return (FinanceOperationResult.Fail("العهدة غير موجودة"), false);
        if (ownerEmployeeId is int owner ? c.EmployeeId != owner : !await CanManageAsync(userId))
            return (FinanceOperationResult.Fail(ownerEmployeeId is null ? "لا تملك صلاحية إدارة العهد" : "هذه العهدة ليست لك"), false);
        if (c.Status != CustodyStatus.Active) return (FinanceOperationResult.Fail("العهدة غير فعّالة"), false);
        var category = await _db.FinanceCategories.AsNoTracking().FirstOrDefaultAsync(x => x.Id == input.CategoryId);
        if (category is null || !category.IsActive || category.Kind == FinanceCategoryKind.OtherIncome) return (FinanceOperationResult.Fail("اختر بابًا للمصروف"), false);
        var balance = await BalanceAsync(c.Id);
        if (input.Amount > balance) return (FinanceOperationResult.Fail($"المبلغ أكبر من رصيد العهدة ({balance:N0} د.ع)"), false);
        if (await EnsureSetupAsync() is string setup) return (FinanceOperationResult.Fail(setup), false);

        var own = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            var year = input.Date.Year;
            var n = await _db.FinanceEntries.CountAsync(e => e.EntryDate.Year == year);
            var entry = new FinanceEntry
            {
                EntryNumber = $"EX-{year}-{n + 1:D5}", EntryDate = input.Date.Date, CategoryId = category.Id, Amount = input.Amount, PartyName = c.Employee.FullName,
                ReceiptNumber = Clean(input.ReceiptNumber), Notes = $"من عهدة {c.CustodyNumber}" + (Clean(input.Description) is string d ? $": {d}" : ""), CreatedByUserId = userId
            };
            _db.FinanceEntries.Add(entry);
            await _db.SaveChangesAsync();
            var text = $"مصروف {entry.EntryNumber} من عهدة {c.CustodyNumber}: {category.Name} — {c.Employee.FullName}";
            var rule = category.Kind == FinanceCategoryKind.NonOperating ? NonOperatingRule : ExpenseRule;
            var (je, error) = await LedgerHelper.PostJournalAsync(_db, rule, input.Amount, input.Date, JournalEntryType.AutoVoucher, text, userId, "FinanceEntries", entry.Id, "EX");
            if (error is not null) { if (own is not null) await own.RollbackAsync(); _db.ChangeTracker.Clear(); return (FinanceOperationResult.Fail(error), false); }
            if (category.AccountId is int accountId) je!.Lines.First(l => l.Debit > 0).AccountId = accountId;
            await _db.SaveChangesAsync();
            entry.JournalEntryId = je!.Id;
            _db.CustodyTransactions.Add(new CustodyTransaction
            {
                CustodyId = c.Id, TxDate = input.Date.Date, Kind = CustodyTxKind.Expense, Amount = input.Amount, CategoryId = category.Id, Description = Clean(input.Description),
                ReceiptNumber = Clean(input.ReceiptNumber), HasReceiptPhoto = input.HasReceiptPhoto, JournalEntryId = je.Id, FinanceEntryId = entry.Id,
                ClientId = input.ClientId, CreatedByUserId = userId
            });
            await _db.SaveChangesAsync();
            if (own is not null) await own.CommitAsync();
            return (FinanceOperationResult.Ok(), false);
        }
        catch (DbUpdateException ex) when (FinanceService.BusinessError(ex) is string msg)
        {
            if (own is not null) await own.RollbackAsync();
            _db.ChangeTracker.Clear();
            return (FinanceOperationResult.Fail(msg), false);
        }
        finally { if (own is not null) await own.DisposeAsync(); }
    }

    // ============================ الإعادة والتسوية ============================

    /// <summary>إعادة (كل أو جزء من) المتبقي للصندوق. إن أصبح الرصيد صفرًا وطُلب الإغلاق تُسوّى العهدة.</summary>
    public async Task<FinanceOperationResult> ReturnAsync(int custodyId, decimal? amount, bool settle, DateTime date, int userId, int? cashBoxId = null)
    {
        if (!await CanManageAsync(userId)) return FinanceOperationResult.Fail("لا تملك صلاحية إدارة العهد");
        var c = await _db.Custodies.Include(x => x.Employee).FirstOrDefaultAsync(x => x.Id == custodyId);
        if (c is null) return FinanceOperationResult.Fail("العهدة غير موجودة");
        if (c.Status == CustodyStatus.Voided) return FinanceOperationResult.Fail("العهدة ملغاة");
        var balance = await BalanceAsync(c.Id);
        var value = amount ?? balance;
        if (value < 0 || value > balance) return FinanceOperationResult.Fail($"المبلغ أكبر من رصيد العهدة ({balance:N0} د.ع)");
        if (settle && value < balance) return FinanceOperationResult.Fail($"لا تُسوّى العهدة وفيها متبقٍّ {balance - value:N0} د.ع: أعده أو أبقِ العهدة فعّالة");
        if (value == 0 && !settle) return FinanceOperationResult.Fail("لا متبقٍّ لإعادته");
        if (await EnsureSetupAsync() is string setup) return FinanceOperationResult.Fail(setup);

        var own = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            if (value > 0)
            {
                var tx = new CustodyTransaction { CustodyId = c.Id, TxDate = date.Date, Kind = CustodyTxKind.Return, Amount = value, Description = settle ? "إعادة المتبقي وتسوية العهدة" : "إعادة جزء من المتبقي", CreatedByUserId = userId };
                _db.CustodyTransactions.Add(tx);
                await _db.SaveChangesAsync();
                var text = $"إعادة عهدة {c.CustodyNumber} من {c.Employee.FullName}";
                var (je, error) = await LedgerHelper.PostJournalAsync(_db, ReturnRule, value, date, JournalEntryType.AutoVoucher, text, userId, "CustodyTransactions", tx.Id, "CU");
                if (error is not null) { if (own is not null) await own.RollbackAsync(); _db.ChangeTracker.Clear(); return FinanceOperationResult.Fail(error); }
                await _db.SaveChangesAsync();
                tx.JournalEntryId = je!.Id;
                await _db.SaveChangesAsync();
                await new CashBoxService(_db).RecordAutoAsync(userId, CashBoxTxType.CustodyReturn, value, date, "CustodyTransactions", tx.Id, c.Employee.FullName, text, je.Id,
                                                              await ResolveBoxAsync(userId, cashBoxId) ?? c.CashBoxId);
                tx.CashBoxTxId = await _db.CashBoxTransactions.Where(t => t.ReferenceTable == "CustodyTransactions" && t.ReferenceId == tx.Id).Select(t => (int?)t.Id).FirstOrDefaultAsync();
            }
            if (settle) (c.Status, c.ClosedAt, c.ClosedByUserId) = (CustodyStatus.Settled, DateTime.UtcNow, userId);
            await _db.SaveChangesAsync();
            await new AuditService(_db).LogAsync(userId, "Edit", "Custodies", c.Id, $"{(settle ? "تسوية" : "إعادة جزء من")} {c.CustodyNumber}: {value:N0}");
            if (own is not null) await own.CommitAsync();
            return FinanceOperationResult.Ok();
        }
        catch (DbUpdateException ex) when (FinanceService.BusinessError(ex) is string msg)
        {
            if (own is not null) await own.RollbackAsync();
            _db.ChangeTracker.Clear();
            return FinanceOperationResult.Fail(msg);
        }
        finally { if (own is not null) await own.DisposeAsync(); }
    }

    /// <summary>تحديد المبلغ المعتاد الذي يعيد التجديد العهدة إليه.</summary>
    public async Task<FinanceOperationResult> SetTargetAsync(int custodyId, decimal target, int userId)
    {
        if (!await CanManageAsync(userId)) return FinanceOperationResult.Fail("لا تملك صلاحية إدارة العهد");
        if (target <= 0) return FinanceOperationResult.Fail("المبلغ يجب أن يكون أكبر من صفر");
        var c = await _db.Custodies.FirstOrDefaultAsync(x => x.Id == custodyId);
        if (c is null) return FinanceOperationResult.Fail("العهدة غير موجودة");
        c.TargetAmount = target;
        await _db.SaveChangesAsync();
        return FinanceOperationResult.Ok();
    }

    // ============================ التعديل والإلغاء ============================

    private static string? ReasonError(string? reason) => string.IsNullOrWhiteSpace(reason) ? "اكتب السبب" : null;

    /// <summary>
    /// تعديل حركة: صرف (المبلغ والغرض والتاريخ)، مصروف (المبلغ والباب والملاحظة والوصل). يُعدَّل الأصل وقيده بالمبلغ الجديد مع حفظ الأصلي،
    /// ولا يترك رصيد العهدة سالبًا.
    /// </summary>
    public async Task<FinanceOperationResult> EditAsync(int txId, decimal newAmount, int? newCategoryId, string? newDescription, string? newReceipt, DateTime newDate, string reason, int userId)
    {
        if (!await CanManageAsync(userId)) return FinanceOperationResult.Fail("لا تملك صلاحية إدارة العهد");
        if (ReasonError(reason) is string re) return FinanceOperationResult.Fail(re + " (سبب التعديل)");
        if (newAmount <= 0) return FinanceOperationResult.Fail("المبلغ يجب أن يكون أكبر من صفر");
        var t = await _db.CustodyTransactions.Include(x => x.Custody).Include(x => x.JournalEntry!).ThenInclude(j => j.Lines).Include(x => x.FinanceEntry).Include(x => x.CashBoxTx)
                                             .FirstOrDefaultAsync(x => x.Id == txId);
        if (t is null) return FinanceOperationResult.Fail("الحركة غير موجودة");
        if (t.IsVoided) return FinanceOperationResult.Fail("الحركة ملغاة");
        if (t.Custody.Status == CustodyStatus.Voided) return FinanceOperationResult.Fail("العهدة ملغاة");
        if (t.Kind == CustodyTxKind.Return) return FinanceOperationResult.Fail("حركة الإعادة تُلغى ثم تُسجَّل من جديد");

        var balance = await BalanceAsync(t.CustodyId);
        var delta = newAmount - t.Amount;
        var balanceAfter = t.Kind == CustodyTxKind.Issue ? balance + delta : balance - delta;
        if (balanceAfter < 0) return FinanceOperationResult.Fail($"التعديل يجعل رصيد العهدة سالبًا ({balanceAfter:N0} د.ع)");
        if (t.Kind == CustodyTxKind.Issue && delta > 0 && t.CashBoxTx is { } box)
        {
            var available = await new CashBoxService(_db).GetBalanceAsync(box.CashBoxId);
            if (delta > available) return FinanceOperationResult.Fail($"رصيد الصندوق ({available:N0} د.ع) لا يكفي لزيادة {delta:N0} د.ع");
        }
        FinanceCategory? category = null;
        if (t.Kind == CustodyTxKind.Expense)
        {
            category = await _db.FinanceCategories.FirstOrDefaultAsync(x => x.Id == (newCategoryId ?? t.CategoryId));
            if (category is null || category.Kind == FinanceCategoryKind.OtherIncome) return FinanceOperationResult.Fail("اختر بابًا للمصروف");
        }

        var own = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            var oldAmount = t.Amount;
            var oldCategory = t.CategoryId;
            t.OriginalAmount ??= t.Amount;
            t.Amount = newAmount;
            t.TxDate = newDate.Date;
            t.Description = Clean(newDescription);
            t.ModifiedByUserId = userId;
            t.ModifiedAt = DateTime.UtcNow;
            if (t.CashBoxTx is { } cb) { cb.OriginalAmount ??= cb.Amount; cb.Amount = Math.Sign(cb.Amount) * newAmount; cb.TxDate = newDate.Date; cb.ModifiedByUserId = userId; cb.ModifiedAt = DateTime.UtcNow; }
            if (t.JournalEntry is { } je)
            {
                je.EntryDate = newDate.Date;
                foreach (var line in je.Lines)
                {
                    if (line.Debit > 0) line.Debit = newAmount;
                    if (line.Credit > 0) line.Credit = newAmount;
                }
                je.Description = (je.Description ?? "").Replace(" (معدّل)", "") + " (معدّل)";
            }
            if (t.Kind == CustodyTxKind.Expense && category is not null)
            {
                t.CategoryId = category.Id;
                t.ReceiptNumber = Clean(newReceipt);
                if (t.FinanceEntry is { } fe)
                {
                    fe.Amount = newAmount;
                    fe.EntryDate = newDate.Date;
                    fe.CategoryId = category.Id;
                    fe.ReceiptNumber = Clean(newReceipt);
                    fe.Notes = $"من عهدة {t.Custody.CustodyNumber}" + (t.Description is string d ? $": {d}" : "");
                }
                if (oldCategory != category.Id && t.JournalEntry is { } je2)
                {
                    // الباب تغيّر: طرف المدين يتبع قاعدة نوع الباب الجديد (أو حسابه الخاص)
                    var ruleName = category.Kind == FinanceCategoryKind.NonOperating ? NonOperatingRule : ExpenseRule;
                    var accountId = category.AccountId ?? await _db.AccountMappingRules.Where(r => r.TransactionType == ruleName).Select(r => r.DebitAccountId).FirstAsync();
                    je2.Lines.First(l => l.Debit > 0).AccountId = accountId;
                }
            }
            await _db.SaveChangesAsync();
            await new AuditService(_db).LogAsync(userId, "Edit", "CustodyTransactions", t.Id,
                $"{t.Custody.CustodyNumber} {KindText(t.Kind)}: {oldAmount:N0} ← {newAmount:N0} — {reason.Trim()}");
            if (own is not null) await own.CommitAsync();
            return FinanceOperationResult.Ok();
        }
        catch (DbUpdateException ex) when (FinanceService.BusinessError(ex) is string msg)
        {
            if (own is not null) await own.RollbackAsync();
            _db.ChangeTracker.Clear();
            return FinanceOperationResult.Fail(msg);
        }
        finally { if (own is not null) await own.DisposeAsync(); }
    }

    /// <summary>إلغاء حركة بقيد عكسي: تبقى مشطوبة بسببها. إلغاء صرف لا يترك رصيد العهدة سالبًا، وإلغاء إعادة يعيد المبلغ للعهدة.</summary>
    public async Task<FinanceOperationResult> VoidTxAsync(int txId, string reason, int userId)
    {
        if (!await CanManageAsync(userId)) return FinanceOperationResult.Fail("لا تملك صلاحية إدارة العهد");
        if (ReasonError(reason) is string re) return FinanceOperationResult.Fail(re + " (سبب الإلغاء)");
        var t = await _db.CustodyTransactions.Include(x => x.Custody).Include(x => x.JournalEntry!).ThenInclude(j => j.Lines).Include(x => x.FinanceEntry).Include(x => x.CashBoxTx)
                                             .FirstOrDefaultAsync(x => x.Id == txId);
        if (t is null) return FinanceOperationResult.Fail("الحركة غير موجودة");
        if (t.IsVoided) return FinanceOperationResult.Fail("الحركة ملغاة مسبقًا");
        if (t.Custody.Status == CustodyStatus.Voided) return FinanceOperationResult.Fail("العهدة ملغاة");

        var balance = await BalanceAsync(t.CustodyId);
        var balanceAfter = t.Kind == CustodyTxKind.Issue ? balance - t.Amount : balance + t.Amount;
        if (balanceAfter < 0)
            return FinanceOperationResult.Fail($"لا يمكن إلغاء هذا الصرف لأن {t.Amount - balance:N0} د.ع منه صُرف فعلًا. ألغِ مصاريفها أولًا، أو خفّض مبلغ الصرف بالتعديل.");
        if (t.Kind == CustodyTxKind.Return && t.CashBoxTx is { } cb0)
        {
            var available = await new CashBoxService(_db).GetBalanceAsync(cb0.CashBoxId);
            if (t.Amount > available) return FinanceOperationResult.Fail($"الإلغاء يجعل رصيد الصندوق سالبًا ({available - t.Amount:N0} د.ع)");
        }

        var own = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            await ReverseAsync(t, reason, userId);
            // إلغاء إعادة من عهدة مسوّاة تعيدها فعّالة
            if (t.Kind == CustodyTxKind.Return && t.Custody.Status == CustodyStatus.Settled) (t.Custody.Status, t.Custody.ClosedAt, t.Custody.ClosedByUserId) = (CustodyStatus.Active, null, null);
            await _db.SaveChangesAsync();
            await new AuditService(_db).LogAsync(userId, "Void", "CustodyTransactions", t.Id, $"{t.Custody.CustodyNumber} {KindText(t.Kind)} {t.Amount:N0} — {reason.Trim()}");
            if (own is not null) await own.CommitAsync();
            return FinanceOperationResult.Ok();
        }
        catch (DbUpdateException ex) when (FinanceService.BusinessError(ex) is string msg)
        {
            if (own is not null) await own.RollbackAsync();
            _db.ChangeTracker.Clear();
            return FinanceOperationResult.Fail(msg);
        }
        finally { if (own is not null) await own.DisposeAsync(); }
    }

    private async Task ReverseAsync(CustodyTransaction t, string reason, int userId)
    {
        t.IsVoided = true;
        t.VoidReason = reason.Trim();
        t.VoidedByUserId = userId;
        t.VoidedAt = DateTime.UtcNow;
        if (t.CashBoxTx is { } cb) { cb.IsVoided = true; cb.VoidReason = "إلغاء: " + reason.Trim(); cb.ModifiedByUserId = userId; cb.ModifiedAt = DateTime.UtcNow; }
        if (t.FinanceEntry is { } fe) { fe.IsVoided = true; fe.VoidReason = reason.Trim(); fe.VoidedByUserId = userId; fe.VoidedAt = DateTime.UtcNow; }
        if (t.JournalEntry is { } je)
        {
            var count = await _db.JournalEntries.CountAsync();
            var rev = new JournalEntry
            {
                EntryNumber = $"CUR-{count + 1:D5}", EntryDate = DateTime.Today, EntryType = JournalEntryType.AutoVoucher,
                Description = $"عكس قيد حركة عهدة {t.Custody.CustodyNumber} ({KindText(t.Kind)}): {reason.Trim()}", CreatedByUserId = userId, IsPosted = true,
                SourceTable = t.FinanceEntryId is null ? "CustodyTransactions" : "FinanceEntries", SourceId = t.FinanceEntryId ?? t.Id
            };
            foreach (var l in je.Lines)
                rev.Lines.Add(new JournalEntryLine { AccountId = l.AccountId, Debit = l.Credit, Credit = l.Debit, Description = "عكس: " + l.Description });
            _db.JournalEntries.Add(rev);
        }
    }

    /// <summary>إلغاء عهدة كاملة: لا يُسمح إن صُرف منها أو أُعيد منها شيء (يُلغى ذلك أولًا). تُلغى حركات صرفها وتبقى ظاهرة.</summary>
    public async Task<FinanceOperationResult> VoidCustodyAsync(int custodyId, string reason, int userId)
    {
        if (!await CanManageAsync(userId)) return FinanceOperationResult.Fail("لا تملك صلاحية إدارة العهد");
        if (ReasonError(reason) is string re) return FinanceOperationResult.Fail(re + " (سبب الإلغاء)");
        var c = await _db.Custodies.Include(x => x.Transactions).ThenInclude(x => x.JournalEntry!).ThenInclude(j => j.Lines)
                                   .Include(x => x.Transactions).ThenInclude(x => x.CashBoxTx).FirstOrDefaultAsync(x => x.Id == custodyId);
        if (c is null) return FinanceOperationResult.Fail("العهدة غير موجودة");
        if (c.Status == CustodyStatus.Voided) return FinanceOperationResult.Fail("العهدة ملغاة مسبقًا");
        var spent = c.Transactions.Where(x => !x.IsVoided && x.Kind != CustodyTxKind.Issue).Sum(x => x.Amount);
        if (spent > 0)
            return FinanceOperationResult.Fail($"لا يمكن إلغاء العهدة كاملة لأن {spent:N0} د.ع صُرف منها أو أُعيد. ألغِ مصاريفها وإعاداتها أولًا، أو خفّض مبلغ الصرف بالتعديل.");
        var own = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            foreach (var t in c.Transactions.Where(x => !x.IsVoided).ToList()) { t.Custody = c; await ReverseAsync(t, reason, userId); }
            (c.Status, c.VoidReason, c.VoidedByUserId, c.VoidedAt) = (CustodyStatus.Voided, reason.Trim(), userId, DateTime.UtcNow);
            await _db.SaveChangesAsync();
            await new AuditService(_db).LogAsync(userId, "Void", "Custodies", c.Id, $"{c.CustodyNumber} — {reason.Trim()}");
            if (own is not null) await own.CommitAsync();
            return FinanceOperationResult.Ok();
        }
        catch (DbUpdateException ex) when (FinanceService.BusinessError(ex) is string msg)
        {
            if (own is not null) await own.RollbackAsync();
            _db.ChangeTracker.Clear();
            return FinanceOperationResult.Fail(msg);
        }
        finally { if (own is not null) await own.DisposeAsync(); }
    }
}
