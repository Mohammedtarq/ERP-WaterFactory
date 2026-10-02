using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

public class CashBoxRow
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public CashBoxType BoxType { get; init; }
    public string? OwnerUsername { get; init; }
    public bool IsDefault { get; init; }
    public bool IsActive { get; init; }
    public decimal Balance { get; init; }
    public override string ToString() => Name;
}

public class CashBoxTxRow
{
    public int Id { get; init; }
    public string TxNumber { get; init; } = "";
    public DateTime TxDate { get; init; }
    public CashBoxTxType TxType { get; init; }
    public string TypeLabel { get; init; } = "";
    public decimal In { get; init; }
    public decimal Out { get; init; }
    public decimal Balance { get; set; }
    public string? CounterBox { get; init; }
    public string? PartyName { get; init; }
    public string? Description { get; init; }
    public string? Reference { get; init; }
    public string CreatedBy { get; init; } = "";
    public bool IsVoided { get; init; }
    public string? VoidReason { get; init; }
    public bool IsModified { get; init; }
    /// <summary>حركة يدوية (إيداع/سحب/مناقلة/افتتاحي) — وحدها قابلة للتعديل والإلغاء من الأدمن.</summary>
    public bool IsManual { get; init; }
}

/// <summary>
/// الصناديق المالية: إيداع، سحب، مناقلة بين الصناديق، كشف حساب برصيد تراكمي.
/// - المبيعات النقدية وسندات القبض/الصرف النقدية وتسليم نقد المندوب تدخل تلقائيًا (RecordAutoAsync).
/// - الإيداع والسحب يُنشئان قيدًا عبر العقل المالي (CashBoxDeposit / CashBoxWithdrawal)؛ المناقلة لا (نفس حساب الصندوق).
/// - لا يُسمح بسحب أو مناقلة تجعل رصيد الصندوق سالبًا.
/// - التعديل والإلغاء وإنشاء الصناديق للأدمن فقط (صلاحية تعديل إعدادات النظام)، مع حفظ المبلغ الأصلي ومن عدّل ومتى.
/// </summary>
public class CashBoxService
{
    public const string DepositRule = "CashBoxDeposit";        // مدين الصندوق / دائن جاري المالك
    public const string WithdrawalRule = "CashBoxWithdrawal";  // مدين مصروفات عمومية / دائن الصندوق

    private readonly ProjectDbContext _db;
    public CashBoxService(ProjectDbContext db) => _db = db;

    /// <summary>الأدمن = دوره يملك صلاحية تعديل إعدادات النظام.</summary>
    public Task<bool> IsAdminAsync(int userId) =>
        _db.Users.Where(u => u.Id == userId && u.IsActive)
           .AnyAsync(u => u.Role.Permissions.Any(p => p.ModuleCode == ModuleCode.SystemSettings && p.CanEdit));

    public async Task<decimal> GetBalanceAsync(int boxId) =>
        await _db.CashBoxTransactions.Where(t => t.CashBoxId == boxId && !t.IsVoided).SumAsync(t => (decimal?)t.Amount) ?? 0;

    /// <summary>الأدمن يرى كل الصناديق؛ غيره يرى صناديقه فقط.</summary>
    public async Task<List<CashBoxRow>> GetBoxesAsync(int userId, bool includeInactive = false)
    {
        var admin = await IsAdminAsync(userId);
        var boxes = await _db.CashBoxes.AsNoTracking()
            .Where(b => (includeInactive || b.IsActive) && (admin || b.OwnerUserId == userId))
            .Select(b => new { b.Id, b.Name, b.BoxType, Owner = b.OwnerUser != null ? b.OwnerUser.Username : null, b.IsDefault, b.IsActive })
            .ToListAsync();
        var ids = boxes.Select(b => b.Id).ToList();
        var balances = await _db.CashBoxTransactions.Where(t => ids.Contains(t.CashBoxId) && !t.IsVoided)
            .GroupBy(t => t.CashBoxId).Select(g => new { g.Key, Sum = g.Sum(t => t.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum);
        // الترتيب في الذاكرة: مقارنة Enum مخزَّن كنص داخل ORDER BY لا تُترجم على SQL Server
        return boxes.OrderBy(b => b.BoxType == CashBoxType.Main ? 0 : 1).ThenBy(b => b.Name).Select(b => new CashBoxRow
        {
            Id = b.Id, Name = b.Name, BoxType = b.BoxType, OwnerUsername = b.Owner, IsDefault = b.IsDefault, IsActive = b.IsActive,
            Balance = balances.GetValueOrDefault(b.Id)
        }).ToList();
    }

    /// <summary>كل الصناديق الفعّالة كوجهات للمناقلة (حتى لغير الأدمن: يسلّم نقده للصندوق الرئيسي).</summary>
    public Task<List<CashBox>> GetTransferTargetsAsync() =>
        _db.CashBoxes.AsNoTracking().Where(b => b.IsActive).OrderBy(b => b.Name).ToListAsync();

    private async Task<string?> CanOperateAsync(int boxId, int userId)
    {
        var box = await _db.CashBoxes.AsNoTracking().FirstOrDefaultAsync(b => b.Id == boxId);
        if (box is null || !box.IsActive) return "الصندوق غير موجود أو موقوف";
        if (box.OwnerUserId != userId && !await IsAdminAsync(userId)) return "لا يمكنك التصرف إلا في صندوقك";
        return null;
    }

    private async Task<string> NextNumberAsync(DateTime date)
    {
        var n = (await _db.Database.SqlQueryRaw<int>("SELECT NEXT VALUE FOR seq_CashBoxTx AS [Value]").ToListAsync())[0];
        return $"CB-{date.Year}-{n:D6}";
    }

    // ============================ الصناديق ============================

    public async Task<FinanceOperationResult> SaveBoxAsync(CashBox box, int userId)
    {
        if (!await IsAdminAsync(userId)) return FinanceOperationResult.Fail("إنشاء الصناديق وتعديلها للأدمن فقط");
        if (string.IsNullOrWhiteSpace(box.Name)) return FinanceOperationResult.Fail("اكتب اسم الصندوق");
        if (box.BoxType == CashBoxType.User && box.OwnerUserId is null) return FinanceOperationResult.Fail("اختر المستخدم صاحب الصندوق");
        if (box.BoxType == CashBoxType.Main) box.OwnerUserId = null;
        if (await _db.CashBoxes.AnyAsync(b => b.Name == box.Name.Trim() && b.Id != box.Id)) return FinanceOperationResult.Fail("يوجد صندوق بنفس الاسم");

        await using var tx = await _db.Database.BeginTransactionAsync();
        if (box.IsDefault)
            await _db.CashBoxes.Where(b => b.IsDefault && b.Id != box.Id).ExecuteUpdateAsync(s => s.SetProperty(b => b.IsDefault, false));
        box.Name = box.Name.Trim();
        if (box.Id == 0) _db.CashBoxes.Add(box);
        else _db.CashBoxes.Update(box);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return FinanceOperationResult.Ok();
    }

    // ============================ الحركات اليدوية ============================

    public async Task<(FinanceOperationResult result, CashBoxTransaction? tx)> DepositAsync(int boxId, decimal amount, DateTime date, string? party, string? description, int userId)
    {
        if (amount <= 0) return (FinanceOperationResult.Fail("المبلغ يجب أن يكون أكبر من صفر"), null);
        if (await CanOperateAsync(boxId, userId) is { } err) return (FinanceOperationResult.Fail(err), null);
        return await ManualAsync(boxId, CashBoxTxType.Deposit, amount, date, party, description, userId, DepositRule);
    }

    public async Task<(FinanceOperationResult result, CashBoxTransaction? tx)> WithdrawAsync(int boxId, decimal amount, DateTime date, string? party, string? description, int userId)
    {
        if (amount <= 0) return (FinanceOperationResult.Fail("المبلغ يجب أن يكون أكبر من صفر"), null);
        if (await CanOperateAsync(boxId, userId) is { } err) return (FinanceOperationResult.Fail(err), null);
        var balance = await GetBalanceAsync(boxId);
        if (balance < amount) return (FinanceOperationResult.Fail($"رصيد الصندوق غير كافٍ: المتاح {balance:N0} د.ع"), null);
        return await ManualAsync(boxId, CashBoxTxType.Withdrawal, -amount, date, party, description, userId, WithdrawalRule);
    }

    private async Task<(FinanceOperationResult, CashBoxTransaction?)> ManualAsync(int boxId, CashBoxTxType type, decimal signed, DateTime date,
        string? party, string? description, int userId, string rule)
    {
        await using var dbTx = await _db.Database.BeginTransactionAsync();
        var boxName = await _db.CashBoxes.Where(b => b.Id == boxId).Select(b => b.Name).FirstAsync();
        var text = $"{(type == CashBoxTxType.Deposit ? "إيداع في" : "سحب من")} {boxName}" + (string.IsNullOrWhiteSpace(description) ? "" : $" — {description.Trim()}");
        var (entry, jeError) = await LedgerHelper.PostJournalAsync(_db, rule, Math.Abs(signed), date, JournalEntryType.AutoVoucher, text, userId,
                                                                   "CashBoxTransactions", null, "CB");
        if (jeError is not null) return (FinanceOperationResult.Fail(jeError), null);
        await _db.SaveChangesAsync();
        var t = new CashBoxTransaction
        {
            TxNumber = await NextNumberAsync(date), CashBoxId = boxId, TxDate = date.Date, TxType = type, Amount = signed,
            PartyName = Clean(party), Description = Clean(description), JournalEntryId = entry!.Id, CreatedByUserId = userId
        };
        _db.CashBoxTransactions.Add(t);
        await _db.SaveChangesAsync();
        entry.SourceId = t.Id;
        await _db.SaveChangesAsync();
        await dbTx.CommitAsync();
        return (FinanceOperationResult.Ok(), t);
    }

    /// <summary>مناقلة: سطران مرتبطان (خارج من المصدر، داخل للهدف) في معاملة واحدة، دون قيد (نفس حساب الصندوق).</summary>
    public async Task<(FinanceOperationResult result, CashBoxTransaction? outTx)> TransferAsync(int fromBoxId, int toBoxId, decimal amount, DateTime date, string? description, int userId)
    {
        if (amount <= 0) return (FinanceOperationResult.Fail("المبلغ يجب أن يكون أكبر من صفر"), null);
        if (fromBoxId == toBoxId) return (FinanceOperationResult.Fail("اختر صندوقًا مختلفًا للمناقلة"), null);
        if (await CanOperateAsync(fromBoxId, userId) is { } err) return (FinanceOperationResult.Fail(err), null);
        var target = await _db.CashBoxes.AsNoTracking().FirstOrDefaultAsync(b => b.Id == toBoxId);
        if (target is null || !target.IsActive) return (FinanceOperationResult.Fail("الصندوق المستلم غير موجود أو موقوف"), null);
        var balance = await GetBalanceAsync(fromBoxId);
        if (balance < amount) return (FinanceOperationResult.Fail($"رصيد الصندوق غير كافٍ: المتاح {balance:N0} د.ع"), null);

        await using var dbTx = await _db.Database.BeginTransactionAsync();
        var group = Guid.NewGuid();
        var outTx = new CashBoxTransaction
        {
            TxNumber = await NextNumberAsync(date), CashBoxId = fromBoxId, CounterCashBoxId = toBoxId, TxDate = date.Date,
            TxType = CashBoxTxType.TransferOut, Amount = -amount, TransferGroup = group, Description = Clean(description), CreatedByUserId = userId
        };
        var inTx = new CashBoxTransaction
        {
            TxNumber = await NextNumberAsync(date), CashBoxId = toBoxId, CounterCashBoxId = fromBoxId, TxDate = date.Date,
            TxType = CashBoxTxType.TransferIn, Amount = amount, TransferGroup = group, Description = Clean(description), CreatedByUserId = userId
        };
        _db.CashBoxTransactions.AddRange(outTx, inTx);
        await _db.SaveChangesAsync();
        await dbTx.CommitAsync();
        return (FinanceOperationResult.Ok(), outTx);
    }

    // ============================ التعديل والإلغاء (أدمن فقط) ============================

    private static bool IsManual(CashBoxTxType t) => t is CashBoxTxType.Opening or CashBoxTxType.Deposit or CashBoxTxType.Withdrawal
                                                          or CashBoxTxType.TransferIn or CashBoxTxType.TransferOut;

    public async Task<FinanceOperationResult> UpdateAsync(int txId, decimal newAmount, DateTime newDate, string? newDescription, int userId)
    {
        if (!await IsAdminAsync(userId)) return FinanceOperationResult.Fail("تعديل حركات الصندوق للأدمن فقط");
        if (newAmount <= 0) return FinanceOperationResult.Fail("المبلغ يجب أن يكون أكبر من صفر");
        var t = await _db.CashBoxTransactions.Include(x => x.JournalEntry!).ThenInclude(j => j.Lines).FirstOrDefaultAsync(x => x.Id == txId);
        if (t is null) return FinanceOperationResult.Fail("الحركة غير موجودة");
        if (t.IsVoided) return FinanceOperationResult.Fail("الحركة ملغاة");
        if (!IsManual(t.TxType)) return FinanceOperationResult.Fail("هذه الحركة ناتجة عن مستند (فاتورة/سند) — تُعدَّل من مستندها الأصلي");

        var legs = t.TransferGroup is null ? new List<CashBoxTransaction> { t }
            : await _db.CashBoxTransactions.Where(x => x.TransferGroup == t.TransferGroup).ToListAsync();

        // التعديل لا يجعل أي صندوق سالبًا
        foreach (var leg in legs)
        {
            var newSigned = Math.Sign(leg.Amount) * newAmount;
            var balanceAfter = await GetBalanceAsync(leg.CashBoxId) - leg.Amount + newSigned;
            if (balanceAfter < 0)
            {
                var name = await _db.CashBoxes.Where(b => b.Id == leg.CashBoxId).Select(b => b.Name).FirstAsync();
                return FinanceOperationResult.Fail($"التعديل يجعل رصيد \"{name}\" سالبًا ({balanceAfter:N0} د.ع)");
            }
        }

        await using var dbTx = await _db.Database.BeginTransactionAsync();
        foreach (var leg in legs)
        {
            leg.OriginalAmount ??= leg.Amount;
            leg.Amount = Math.Sign(leg.Amount) * newAmount;
            leg.TxDate = newDate.Date;
            leg.Description = Clean(newDescription);
            leg.ModifiedByUserId = userId;
            leg.ModifiedAt = DateTime.UtcNow;
        }
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
        await _db.SaveChangesAsync();
        await dbTx.CommitAsync();
        return FinanceOperationResult.Ok();
    }

    /// <summary>إلغاء حركة يدوية: تبقى في السجل مشطوبة (لا تدخل الرصيد)، ويُنشأ قيد عكسي لقيدها.</summary>
    public async Task<FinanceOperationResult> VoidAsync(int txId, string reason, int userId)
    {
        if (!await IsAdminAsync(userId)) return FinanceOperationResult.Fail("إلغاء حركات الصندوق للأدمن فقط");
        if (string.IsNullOrWhiteSpace(reason)) return FinanceOperationResult.Fail("اكتب سبب الإلغاء");
        var t = await _db.CashBoxTransactions.Include(x => x.JournalEntry!).ThenInclude(j => j.Lines).FirstOrDefaultAsync(x => x.Id == txId);
        if (t is null) return FinanceOperationResult.Fail("الحركة غير موجودة");
        if (t.IsVoided) return FinanceOperationResult.Fail("الحركة ملغاة مسبقًا");
        if (!IsManual(t.TxType)) return FinanceOperationResult.Fail("هذه الحركة ناتجة عن مستند (فاتورة/سند) — لا تُلغى من الصندوق");

        var legs = t.TransferGroup is null ? new List<CashBoxTransaction> { t }
            : await _db.CashBoxTransactions.Where(x => x.TransferGroup == t.TransferGroup).ToListAsync();
        foreach (var leg in legs.Where(l => l.Amount > 0))
        {
            var after = await GetBalanceAsync(leg.CashBoxId) - leg.Amount;
            if (after < 0)
            {
                var name = await _db.CashBoxes.Where(b => b.Id == leg.CashBoxId).Select(b => b.Name).FirstAsync();
                return FinanceOperationResult.Fail($"الإلغاء يجعل رصيد \"{name}\" سالبًا ({after:N0} د.ع)");
            }
        }

        await using var dbTx = await _db.Database.BeginTransactionAsync();
        foreach (var leg in legs)
        {
            leg.IsVoided = true;
            leg.VoidReason = reason.Trim();
            leg.ModifiedByUserId = userId;
            leg.ModifiedAt = DateTime.UtcNow;
        }
        if (t.JournalEntry is { } je)
        {
            var count = await _db.JournalEntries.CountAsync();
            var reversal = new JournalEntry
            {
                EntryNumber = $"CBR-{count + 1:D5}", EntryDate = DateTime.Today, EntryType = JournalEntryType.AutoVoucher,
                Description = $"عكس قيد حركة صندوق ملغاة {t.TxNumber}: {reason.Trim()}", CreatedByUserId = userId, IsPosted = true,
                SourceTable = "CashBoxTransactions", SourceId = t.Id
            };
            foreach (var line in je.Lines)
                reversal.Lines.Add(new JournalEntryLine { AccountId = line.AccountId, Debit = line.Credit, Credit = line.Debit, Description = "عكس: " + line.Description });
            _db.JournalEntries.Add(reversal);
        }
        await _db.SaveChangesAsync();
        await dbTx.CommitAsync();
        return FinanceOperationResult.Ok();
    }

    // ============================ التسجيل التلقائي ============================

    /// <summary>
    /// حركة تلقائية من مستند (سند، تسليم مندوب) في صندوق المستخدم أو الافتراضي — نفس منطق ترحيل المبيعات
    /// (sp_CashBox_RecordAuto)، وداخل معاملة المستدعي إن وُجدت.
    /// </summary>
    public async Task RecordAutoAsync(int userId, CashBoxTxType type, decimal signedAmount, DateTime date, string referenceTable, int? referenceId,
                                      string? party, string description, int? journalEntryId, int? cashBoxId = null)
    {
        await _db.Database.ExecuteSqlRawAsync(
            "EXEC sp_CashBox_RecordAuto @UserId, @TxType, @Amount, @TxDate, @ReferenceTable, @ReferenceId, @PartyName, @Description, @JournalEntryId, @CashBoxId",
            new SqlParameter("@UserId", userId), new SqlParameter("@TxType", type.ToString()),
            new SqlParameter("@Amount", signedAmount), new SqlParameter("@TxDate", date.Date),
            new SqlParameter("@ReferenceTable", referenceTable), new SqlParameter("@ReferenceId", (object?)referenceId ?? DBNull.Value),
            new SqlParameter("@PartyName", (object?)party ?? DBNull.Value), new SqlParameter("@Description", description),
            new SqlParameter("@JournalEntryId", (object?)journalEntryId ?? DBNull.Value), new SqlParameter("@CashBoxId", (object?)cashBoxId ?? DBNull.Value));
    }

    // ============================ الكشف ============================

    public async Task<(decimal opening, List<CashBoxTxRow> rows)> GetStatementAsync(int boxId, DateTime from, DateTime to, bool includeVoided = true)
    {
        var opening = await _db.CashBoxTransactions.Where(t => t.CashBoxId == boxId && !t.IsVoided && t.TxDate < from.Date)
            .SumAsync(t => (decimal?)t.Amount) ?? 0;
        var raw = await _db.CashBoxTransactions.AsNoTracking()
            .Where(t => t.CashBoxId == boxId && t.TxDate >= from.Date && t.TxDate <= to.Date && (includeVoided || !t.IsVoided))
            .OrderBy(t => t.TxDate).ThenBy(t => t.Id)
            .Select(t => new
            {
                t.Id, t.TxNumber, t.TxDate, t.TxType, t.Amount, Counter = t.CounterCashBox != null ? t.CounterCashBox.Name : null,
                t.PartyName, t.Description, t.ReferenceTable, t.ReferenceId, User = t.CreatedByUser.Username, t.IsVoided, t.VoidReason,
                Modified = t.ModifiedAt != null
            }).ToListAsync();

        var invoiceIds = raw.Where(r => r.ReferenceTable == "SalesInvoices" && r.ReferenceId != null).Select(r => r.ReferenceId!.Value).ToList();
        var voucherIds = raw.Where(r => r.ReferenceTable == "Vouchers" && r.ReferenceId != null).Select(r => r.ReferenceId!.Value).ToList();
        var invoices = await _db.SalesInvoices.Where(i => invoiceIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.InvoiceNumber);
        var vouchers = await _db.Vouchers.Where(v => voucherIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.VoucherNumber);

        var balance = opening;
        var rows = new List<CashBoxTxRow>();
        foreach (var r in raw)
        {
            if (!r.IsVoided) balance += r.Amount;
            rows.Add(new CashBoxTxRow
            {
                Id = r.Id, TxNumber = r.TxNumber, TxDate = r.TxDate, TxType = r.TxType, TypeLabel = TypeLabel(r.TxType),
                In = r.Amount > 0 ? r.Amount : 0, Out = r.Amount < 0 ? -r.Amount : 0, Balance = balance,
                CounterBox = r.Counter, PartyName = r.PartyName, Description = r.Description,
                Reference = r.ReferenceTable switch
                {
                    "SalesInvoices" when r.ReferenceId is { } i => invoices.GetValueOrDefault(i),
                    "Vouchers" when r.ReferenceId is { } v => vouchers.GetValueOrDefault(v),
                    _ => null
                },
                CreatedBy = r.User, IsVoided = r.IsVoided, VoidReason = r.VoidReason, IsModified = r.Modified, IsManual = IsManual(r.TxType)
            });
        }
        return (opening, rows);
    }

    public Task<CashBoxTransaction?> GetTransactionAsync(int id) =>
        _db.CashBoxTransactions.AsNoTracking().Include(t => t.CashBox).Include(t => t.CounterCashBox)
           .Include(t => t.CreatedByUser).Include(t => t.ModifiedByUser).FirstOrDefaultAsync(t => t.Id == id);

    public static string TypeLabel(CashBoxTxType t) => t switch
    {
        CashBoxTxType.Opening => "رصيد افتتاحي",
        CashBoxTxType.Deposit => "إيداع",
        CashBoxTxType.Withdrawal => "سحب",
        CashBoxTxType.TransferIn => "مناقلة واردة",
        CashBoxTxType.TransferOut => "مناقلة صادرة",
        CashBoxTxType.SalesReceipt => "مبيعات نقدية",
        CashBoxTxType.VoucherReceipt => "سند قبض",
        CashBoxTxType.VoucherPayment => "سند صرف",
        _ => "تسليم نقد مندوب"
    };

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
