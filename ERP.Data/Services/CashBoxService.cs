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
    /// <summary>الاسم مع الرصيد، لقوائم الاختيار.</summary>
    public string Display => $"{Name} — {Balance:N0} د.ع";
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

/// <summary>سطر في تقرير «تعديلات الصناديق».</summary>
public class CashBoxChangeRow
{
    public string TxNumber { get; init; } = "";
    public string BoxName { get; init; } = "";
    public DateTime TxDate { get; init; }
    public string TypeLabel { get; init; } = "";
    public string Kind { get; init; } = "";
    public decimal? OldAmount { get; init; }
    public decimal? NewAmount { get; init; }
    public string? Reason { get; init; }
    public string By { get; init; } = "";
    public DateTime AtLocal { get; init; }
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
    public const string OpeningRule = "CashBoxOpening";        // مدين الصندوق / دائن رأس المال
    public const string AdjustGainRule = "CashBoxAdjustGain";  // مدين الصندوق / دائن فروقات جرد الصناديق (زيادة)
    public const string AdjustLossRule = "CashBoxAdjustLoss";  // مدين فروقات جرد الصناديق / دائن الصندوق (عجز)
    /// <summary>فرق التسوية الذي لا يتجاوزه صاحب «تسوية أرصدة الصناديق» منفردًا؛ فوقه يُرفع طلب لاعتماد المالية.</summary>
    public const decimal AdjustApprovalLimit = 100_000m;

    private readonly ProjectDbContext _db;
    public CashBoxService(ProjectDbContext db) => _db = db;

    /// <summary>الأدمن = دوره يملك صلاحية تعديل إعدادات النظام.</summary>
    public Task<bool> IsAdminAsync(int userId) =>
        _db.Users.Where(u => u.Id == userId && u.IsActive)
           .AnyAsync(u => u.Role.Permissions.Any(p => p.ModuleCode == ModuleCode.SystemSettings && p.CanEdit));

    /// <summary>من يصحّح حركات الصناديق ويسوّيها: الأدمن، أو من يملك صلاحية «تسوية أرصدة الصناديق».</summary>
    public async Task<bool> CanCorrectAsync(int userId) =>
        await IsAdminAsync(userId) || await SpecialPermission.HasAsync(_db, userId, SpecialPermission.CashAdjust);

    public const string HomeSpendError = "صندوق المنزل لا يُصرف منه تشغيل (مواد، رواتب، مصاريف): للأرباح خارج العمل فقط — اصرف من «صندوق المنزل» (الخمس، مساعدات، أصول) أو اسحب أرباح شريك";

    public Task<bool> IsHomeBoxAsync(int boxId) => _db.CashBoxes.AnyAsync(b => b.Id == boxId && b.BoxType == CashBoxType.Home);

    private async Task<string?> PeriodLockErrorAsync(DateTime date) =>
        await new PeriodLockService(_db).GetLockedThroughAsync() is DateTime locked && date.Date <= locked.Date
            ? $"الفترة مقفلة حتى {locked:yyyy/MM/dd} — لا تعديل ولا إلغاء فيها؛ صحّح بتسوية في الفترة الحالية"
            : null;

    public async Task<decimal> GetBalanceAsync(int boxId) =>
        await _db.CashBoxTransactions.Where(t => t.CashBoxId == boxId && !t.IsVoided).SumAsync(t => (decimal?)t.Amount) ?? 0;

    /// <summary>الأدمن يرى كل الصناديق؛ غيره يرى صناديقه فقط. الموقوفة مخفية إلا بطلبها (الأدمن ليعيد تفعيلها).</summary>
    public async Task<List<CashBoxRow>> GetBoxesAsync(int userId, bool includeInactive = false)
    {
        var admin = await IsAdminAsync(userId);
        var seeAll = admin || await SpecialPermission.HasAsync(_db, userId, SpecialPermission.AllCashBoxes);
        var boxes = await _db.CashBoxes.AsNoTracking()
            .Where(b => (includeInactive || b.IsActive) && (seeAll || b.OwnerUserId == userId))
            .Select(b => new { b.Id, b.Name, b.BoxType, Owner = b.OwnerUser != null ? b.OwnerUser.Username : null, b.IsDefault, b.IsActive })
            .ToListAsync();
        var ids = boxes.Select(b => b.Id).ToList();
        var balances = await _db.CashBoxTransactions.Where(t => ids.Contains(t.CashBoxId) && !t.IsVoided)
            .GroupBy(t => t.CashBoxId).Select(g => new { g.Key, Sum = g.Sum(t => t.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum);
        // الترتيب في الذاكرة: مقارنة Enum مخزَّن كنص داخل ORDER BY لا تُترجم على SQL Server
        return boxes.OrderBy(b => b.BoxType switch { CashBoxType.Main => 0, CashBoxType.User => 1, CashBoxType.Cards => 3, _ => 2 }).ThenBy(b => b.Name).Select(b => new CashBoxRow
        {
            Id = b.Id, Name = b.Name, BoxType = b.BoxType, OwnerUsername = b.Owner, IsDefault = b.IsDefault, IsActive = b.IsActive,
            Balance = balances.GetValueOrDefault(b.Id)
        }).ToList();
    }

    /// <summary>
    /// «الصندوق العام» = مجموع الصناديق المفعّلة عدا صندوق البطاقات الإلكترونية، ومجموع صناديق البطاقات على حدة.
    /// </summary>
    public async Task<(decimal general, decimal cards)> GetTotalsAsync()
    {
        var sums = await _db.CashBoxTransactions.Where(t => !t.IsVoided && t.CashBox.IsActive)
            .GroupBy(t => t.CashBox.BoxType).Select(g => new { g.Key, Sum = g.Sum(t => t.Amount) }).ToListAsync();
        return (sums.Where(x => x.Key != CashBoxType.Cards).Sum(x => x.Sum), sums.Where(x => x.Key == CashBoxType.Cards).Sum(x => x.Sum));
    }

    /// <summary>صندوق البطاقات الإلكترونية الفعّال (الأول إن تعدّد) — يستقبل مبالغ البيع الإلكتروني.</summary>
    public Task<int?> CardsBoxIdAsync() =>
        _db.CashBoxes.Where(b => b.IsActive && b.BoxType == CashBoxType.Cards).OrderBy(b => b.Id).Select(b => (int?)b.Id).FirstOrDefaultAsync();

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

    internal async Task<string> NextNumberAsync(DateTime date)
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
        if (box.BoxType == CashBoxType.Main) { box.OwnerUserId = null; box.IsActive = true; }
        if (box.BoxType == CashBoxType.Cards)
        {
            if (box.IsDefault) return FinanceOperationResult.Fail("صندوق البطاقات الإلكترونية لا يكون افتراضيًا: لا يستقبل نقدًا");
            box.OwnerUserId = null;
        }
        // كل مستخدم له صندوق واحد مفعّل تُسجَّل عليه عملياته النقدية
        if (box.BoxType == CashBoxType.User && box.IsActive
            && await _db.CashBoxes.AnyAsync(b => b.Id != box.Id && b.IsActive && b.BoxType == CashBoxType.User && b.OwnerUserId == box.OwnerUserId))
            return FinanceOperationResult.Fail("لهذا المستخدم صندوق مفعّل؛ أوقفه أولًا أو أنشئ هذا غير مفعّل");
        if (box.IsDefault) box.IsActive = true;
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

    /// <summary>
    /// تفعيل/إيقاف صندوق (أدمن فقط). صندوق المستخدم غير المفعّل لا يستقبل شيئًا: مبيعاته ومقبوضاته النقدية
    /// تذهب للصندوق الافتراضي (الرئيسي) تلقائيًا بلا أي رسالة، ورصيده وكشفه يبقيان للعرض.
    /// </summary>
    public async Task<FinanceOperationResult> SetActiveAsync(int boxId, bool active, int userId)
    {
        if (!await IsAdminAsync(userId)) return FinanceOperationResult.Fail("تفعيل الصناديق وإيقافها للأدمن فقط");
        var box = await _db.CashBoxes.FindAsync(boxId);
        if (box is null) return FinanceOperationResult.Fail("الصندوق غير موجود");
        if (!active && box.IsDefault)
            return FinanceOperationResult.Fail("الصندوق الافتراضي يستقبل المبيعات النقدية ولا يُوقف — اجعل صندوقًا آخر افتراضيًا أولًا");
        if (active && box.BoxType == CashBoxType.User
            && await _db.CashBoxes.AnyAsync(b => b.Id != box.Id && b.IsActive && b.BoxType == CashBoxType.User && b.OwnerUserId == box.OwnerUserId))
            return FinanceOperationResult.Fail("لصاحب هذا الصندوق صندوق آخر مفعّل؛ أوقفه أولًا (لكل مستخدم صندوق مفعّل واحد)");
        box.IsActive = active;
        await _db.SaveChangesAsync();
        return FinanceOperationResult.Ok();
    }

    /// <summary>حذف نهائي لصندوق لم تُسجَّل عليه أي حركة (أدمن فقط)؛ ما عليه حركات يُوقف ويُخفى بدل الحذف.</summary>
    public async Task<FinanceOperationResult> DeleteBoxAsync(int boxId, int userId)
    {
        if (!await IsAdminAsync(userId)) return FinanceOperationResult.Fail("حذف الصناديق للأدمن فقط");
        var box = await _db.CashBoxes.FindAsync(boxId);
        if (box is null) return FinanceOperationResult.Fail("الصندوق غير موجود");
        if (box.IsDefault) return FinanceOperationResult.Fail("الصندوق الافتراضي لا يُحذف");
        if (await _db.CashBoxTransactions.AnyAsync(t => t.CashBoxId == boxId || t.CounterCashBoxId == boxId))
            return FinanceOperationResult.Fail("على هذا الصندوق حركات فلا يُحذف — أوقفه ليُخفى من العرض (ويُعاد تفعيله عند الحاجة)");
        _db.CashBoxes.Remove(box);
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Delete", "CashBoxes", boxId, $"حذف صندوق بلا حركات: {box.Name}");
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
        if (await IsHomeBoxAsync(boxId)) return (FinanceOperationResult.Fail(HomeSpendError), null);
        if (await ApprovalLimits.CheckPaymentAsync(_db, userId, amount) is string limitError) return (FinanceOperationResult.Fail(limitError), null);
        var balance = await GetBalanceAsync(boxId);
        if (balance < amount) return (FinanceOperationResult.Fail($"رصيد الصندوق غير كافٍ: المتاح {balance:N0} د.ع"), null);
        return await ManualAsync(boxId, CashBoxTxType.Withdrawal, -amount, date, party, description, userId, WithdrawalRule);
    }

    /// <summary>
    /// رصيد افتتاحي للصندوق (للأدمن): الجرد الفعلي عند بدء العمل بالنظام أو النقل من نظام سابق.
    /// قيده: الصندوق مدين ورأس المال دائن، ويُعدَّل أو يُلغى كأي حركة يدوية.
    /// </summary>
    public async Task<(FinanceOperationResult result, CashBoxTransaction? tx)> OpeningAsync(int boxId, decimal amount, DateTime date, string? description, int userId)
    {
        if (amount <= 0) return (FinanceOperationResult.Fail("المبلغ يجب أن يكون أكبر من صفر"), null);
        if (!await IsAdminAsync(userId)) return (FinanceOperationResult.Fail("الرصيد الافتتاحي للصندوق للأدمن فقط"), null);
        if (!await _db.CashBoxes.AnyAsync(b => b.Id == boxId)) return (FinanceOperationResult.Fail("الصندوق غير موجود"), null);
        return await ManualAsync(boxId, CashBoxTxType.Opening, amount, date, null, description, userId, OpeningRule);
    }

    private async Task<(FinanceOperationResult, CashBoxTransaction?)> ManualAsync(int boxId, CashBoxTxType type, decimal signed, DateTime date,
        string? party, string? description, int userId, string rule)
    {
        await using var dbTx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        var boxName = await _db.CashBoxes.Where(b => b.Id == boxId).Select(b => b.Name).FirstAsync();
        var verb = type switch { CashBoxTxType.Deposit => "إيداع في", CashBoxTxType.Opening => "رصيد افتتاحي —", _ => "سحب من" };
        var text = $"{verb} {boxName}" + (string.IsNullOrWhiteSpace(description) ? "" : $" — {description.Trim()}");
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
        if (dbTx is not null) await dbTx.CommitAsync();
        return (FinanceOperationResult.Ok(), t);
    }

    /// <summary>مناقلة: سطران مرتبطان (خارج من المصدر، داخل للهدف) في معاملة واحدة، دون قيد (نفس حساب الصندوق).</summary>
    public async Task<(FinanceOperationResult result, CashBoxTransaction? outTx)> TransferAsync(int fromBoxId, int toBoxId, decimal amount, DateTime date, string? description, int userId,
                                                                                                bool skipOwnerCheck = false)
    {
        if (amount <= 0) return (FinanceOperationResult.Fail("المبلغ يجب أن يكون أكبر من صفر"), null);
        if (fromBoxId == toBoxId) return (FinanceOperationResult.Fail("اختر صندوقًا مختلفًا للمناقلة"), null);
        if (!skipOwnerCheck && await CanOperateAsync(fromBoxId, userId) is { } err) return (FinanceOperationResult.Fail(err), null);
        var target = await _db.CashBoxes.AsNoTracking().FirstOrDefaultAsync(b => b.Id == toBoxId);
        if (target is null || !target.IsActive) return (FinanceOperationResult.Fail("الصندوق المستلم غير موجود أو موقوف"), null);
        var balance = await GetBalanceAsync(fromBoxId);
        if (balance < amount) return (FinanceOperationResult.Fail($"رصيد الصندوق غير كافٍ: المتاح {balance:N0} د.ع"), null);

        await using var dbTx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
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
        if (dbTx is not null) await dbTx.CommitAsync();
        return (FinanceOperationResult.Ok(), outTx);
    }

    // ============================ التعديل والإلغاء (أدمن فقط) ============================

    private static bool IsManual(CashBoxTxType t) => t is CashBoxTxType.Opening or CashBoxTxType.Deposit or CashBoxTxType.Withdrawal
                                                          or CashBoxTxType.TransferIn or CashBoxTxType.TransferOut;

    /// <summary>
    /// تصحيح حركة يدوية: يُعكس قيدها القديم بقيد مستقل ويُنشأ قيد جديد بالقيم الصحيحة (لا يُغيَّر القيد في مكانه)،
    /// ويحفظ السطر المبلغ الأصلي ومن صحّح ومتى. السبب إلزامي، والفترة المقفلة ممنوعة على الجميع.
    /// </summary>
    public async Task<FinanceOperationResult> UpdateAsync(int txId, decimal newAmount, DateTime newDate, string? newDescription, int userId, string? reason = null)
    {
        if (!await CanCorrectAsync(userId)) return FinanceOperationResult.Fail("تعديل حركات الصندوق للأدمن فقط أو لمن يملك صلاحية «تسوية أرصدة الصناديق»");
        if (newAmount <= 0) return FinanceOperationResult.Fail("المبلغ يجب أن يكون أكبر من صفر");
        if (string.IsNullOrWhiteSpace(reason)) return FinanceOperationResult.Fail("اكتب سبب التعديل");
        var t = await _db.CashBoxTransactions.Include(x => x.JournalEntry!).ThenInclude(j => j.Lines).FirstOrDefaultAsync(x => x.Id == txId);
        if (t is null) return FinanceOperationResult.Fail("الحركة غير موجودة");
        if (t.IsVoided) return FinanceOperationResult.Fail("الحركة ملغاة");
        if (!IsManual(t.TxType)) return FinanceOperationResult.Fail("هذه الحركة ناتجة عن مستند (فاتورة/سند) — تُعدَّل من مستندها الأصلي");
        if (await PeriodLockErrorAsync(t.TxDate) is { } lockOld) return FinanceOperationResult.Fail(lockOld);
        if (await PeriodLockErrorAsync(newDate) is { } lockNew) return FinanceOperationResult.Fail(lockNew);

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

        await using var dbTx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        var oldAmount = t.Amount;
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
            var count = await _db.JournalEntries.CountAsync();
            var reversal = new JournalEntry
            {
                EntryNumber = $"CBR-{count + 1:D5}", EntryDate = DateTime.Today, EntryType = JournalEntryType.AutoVoucher,
                Description = $"عكس قيد حركة صندوق مُصحَّحة {t.TxNumber}: {reason.Trim()}", CreatedByUserId = userId, IsPosted = true,
                SourceTable = "CashBoxTransactions", SourceId = t.Id
            };
            var fresh = new JournalEntry
            {
                EntryNumber = $"CBC-{count + 2:D5}", EntryDate = newDate.Date, EntryType = JournalEntryType.AutoVoucher,
                Description = (je.Description ?? "").Replace(" (مُصحَّح)", "") + " (مُصحَّح)", CreatedByUserId = userId, IsPosted = true,
                SourceTable = "CashBoxTransactions", SourceId = t.Id
            };
            foreach (var line in je.Lines)
            {
                reversal.Lines.Add(new JournalEntryLine { AccountId = line.AccountId, Debit = line.Credit, Credit = line.Debit, Description = "عكس: " + line.Description });
                fresh.Lines.Add(new JournalEntryLine
                {
                    AccountId = line.AccountId, Debit = line.Debit > 0 ? newAmount : 0, Credit = line.Credit > 0 ? newAmount : 0, Description = line.Description
                });
            }
            _db.JournalEntries.AddRange(reversal, fresh);
            await _db.SaveChangesAsync();
            t.JournalEntryId = fresh.Id;
        }
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Edit", "CashBoxTransactions", t.Id,
            $"تصحيح حركة صندوق {t.TxNumber}: {Math.Abs(oldAmount):N0} ← {newAmount:N0} د.ع — {reason.Trim()}");
        if (dbTx is not null) await dbTx.CommitAsync();
        return FinanceOperationResult.Ok();
    }

    /// <summary>إلغاء حركة يدوية: تبقى في السجل مشطوبة (لا تدخل الرصيد)، ويُنشأ قيد عكسي لقيدها.</summary>
    public async Task<FinanceOperationResult> VoidAsync(int txId, string reason, int userId)
    {
        if (!await CanCorrectAsync(userId)) return FinanceOperationResult.Fail("إلغاء حركات الصندوق للأدمن فقط أو لمن يملك صلاحية «تسوية أرصدة الصناديق»");
        if (string.IsNullOrWhiteSpace(reason)) return FinanceOperationResult.Fail("اكتب سبب الإلغاء");
        var t = await _db.CashBoxTransactions.Include(x => x.JournalEntry!).ThenInclude(j => j.Lines).FirstOrDefaultAsync(x => x.Id == txId);
        if (t is null) return FinanceOperationResult.Fail("الحركة غير موجودة");
        if (t.IsVoided) return FinanceOperationResult.Fail("الحركة ملغاة مسبقًا");
        if (!IsManual(t.TxType)) return FinanceOperationResult.Fail("هذه الحركة ناتجة عن مستند (فاتورة/سند) — لا تُلغى من الصندوق");
        if (await PeriodLockErrorAsync(t.TxDate) is { } lockErr) return FinanceOperationResult.Fail(lockErr);

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
        await new AuditService(_db).LogAsync(userId, "Void", "CashBoxTransactions", t.Id, $"إلغاء حركة صندوق {t.TxNumber} ({Math.Abs(t.Amount):N0} د.ع) — {reason.Trim()}");
        await dbTx.CommitAsync();
        return FinanceOperationResult.Ok();
    }

    // ============================ التسوية بالجرد الفعلي ============================

    /// <summary>
    /// تسوية صندوق بالجرد: يُدخل المبلغ الموجود فعلًا فيحسب النظام الفرق ويقيّده مستقلًا (زيادة/عجز) بسبب إلزامي.
    /// لا يُعدَّل رصيد قديم بصمت. فرق أكبر من <see cref="AdjustApprovalLimit"/> لغير الأدمن يُرفع طلبًا لاعتماد المالية.
    /// </summary>
    public async Task<(FinanceOperationResult result, CashBoxTransaction? tx, PaymentRequest? request)> AdjustToCountAsync(
        int boxId, decimal actualAmount, DateTime date, string? reason, int userId)
    {
        if (!await CanCorrectAsync(userId)) return (FinanceOperationResult.Fail("التسوية بالجرد للأدمن فقط أو لمن يملك صلاحية «تسوية أرصدة الصناديق»"), null, null);
        if (actualAmount < 0) return (FinanceOperationResult.Fail("المبلغ الفعلي لا يكون سالبًا"), null, null);
        if (string.IsNullOrWhiteSpace(reason)) return (FinanceOperationResult.Fail("اكتب سبب التسوية (مثلًا: جرد فعلي، خطأ إدخال)"), null, null);
        var box = await _db.CashBoxes.AsNoTracking().FirstOrDefaultAsync(b => b.Id == boxId);
        if (box is null || !box.IsActive) return (FinanceOperationResult.Fail("الصندوق غير موجود أو موقوف"), null, null);
        if (await PeriodLockErrorAsync(date) is { } lockErr) return (FinanceOperationResult.Fail(lockErr), null, null);
        var diff = actualAmount - await GetBalanceAsync(boxId);
        if (diff == 0) return (FinanceOperationResult.Fail("الرصيد الدفتري يساوي الجرد الفعلي — لا فرق للتسوية"), null, null);

        if (Math.Abs(diff) > AdjustApprovalLimit && !await IsAdminAsync(userId))
        {
            var (res, req) = await new PaymentApprovalService(_db).RequestCashAdjustmentAsync(boxId, actualAmount, diff, date, reason, userId);
            return (res, null, req);
        }
        var (r, t) = await PostAdjustmentAsync(boxId, diff, date, reason.Trim(), userId);
        return (r, t, null);
    }

    /// <summary>ترحيل قيد التسوية وحركتها (بعد التحقق). الفرق موجب = زيادة، سالب = عجز.</summary>
    internal async Task<(FinanceOperationResult, CashBoxTransaction?)> PostAdjustmentAsync(int boxId, decimal diff, DateTime date, string reason, int userId)
    {
        var setup = await new HomeBoxService(_db).EnsureSetupAsync();
        if (setup is not null) return (FinanceOperationResult.Fail(setup), null);
        await using var dbTx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        var boxName = await _db.CashBoxes.Where(b => b.Id == boxId).Select(b => b.Name).FirstAsync();
        var text = $"{(diff > 0 ? "زيادة جرد" : "عجز جرد")} {boxName} — {reason}";
        var (entry, jeError) = await LedgerHelper.PostJournalAsync(_db, diff > 0 ? AdjustGainRule : AdjustLossRule, Math.Abs(diff), date,
                                                                   JournalEntryType.AutoVoucher, text, userId, "CashBoxTransactions", null, "CBA");
        if (jeError is not null) return (FinanceOperationResult.Fail(jeError), null);
        await _db.SaveChangesAsync();
        var t = new CashBoxTransaction
        {
            TxNumber = await NextNumberAsync(date), CashBoxId = boxId, TxDate = date.Date, TxType = CashBoxTxType.Adjustment, Amount = diff,
            Description = Clean(reason), JournalEntryId = entry!.Id, CreatedByUserId = userId
        };
        _db.CashBoxTransactions.Add(t);
        await _db.SaveChangesAsync();
        entry.SourceId = t.Id;
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Insert", "CashBoxTransactions", t.Id, $"تسوية {t.TxNumber}: {text} ({diff:+#,0;-#,0} د.ع)");
        if (dbTx is not null) await dbTx.CommitAsync();
        return (FinanceOperationResult.Ok(), t);
    }

    /// <summary>تقرير تعديلات الصناديق: كل حركة صُحِّحت أو أُلغيت أو سُوِّيت، بالقديم والجديد ومن عدّل ومتى.</summary>
    public async Task<List<CashBoxChangeRow>> ChangesAsync(DateTime from, DateTime to, int? boxId = null)
    {
        var f = from.Date;
        var e = to.Date.AddDays(1);
        var rows = await _db.CashBoxTransactions.AsNoTracking()
            .Where(t => (boxId == null || t.CashBoxId == boxId)
                        && ((t.ModifiedAt != null && t.ModifiedAt >= f.ToUniversalTime() && t.ModifiedAt < e.ToUniversalTime())
                            || (t.TxType == CashBoxTxType.Adjustment && t.TxDate >= f && t.TxDate < e)))
            .OrderByDescending(t => t.ModifiedAt ?? t.CreatedAt)
            .Select(t => new
            {
                t.Id, t.TxNumber, Box = t.CashBox.Name, t.TxDate, t.TxType, t.Amount, t.OriginalAmount, t.IsVoided, t.VoidReason, t.Description,
                By = t.ModifiedByUser != null ? t.ModifiedByUser.Username : t.CreatedByUser.Username, At = t.ModifiedAt ?? t.CreatedAt
            }).ToListAsync();
        return rows.Select(r => new CashBoxChangeRow
        {
            TxNumber = r.TxNumber, BoxName = r.Box, TxDate = r.TxDate, TypeLabel = TypeLabel(r.TxType),
            Kind = r.TxType == CashBoxTxType.Adjustment ? "تسوية جرد" : r.IsVoided ? "إلغاء" : "تصحيح",
            OldAmount = r.TxType == CashBoxTxType.Adjustment ? null : Math.Abs(r.OriginalAmount ?? r.Amount),
            NewAmount = r.TxType == CashBoxTxType.Adjustment ? r.Amount : r.IsVoided ? null : Math.Abs(r.Amount),
            Reason = r.IsVoided ? r.VoidReason : r.Description, By = r.By, AtLocal = DateTime.SpecifyKind(r.At, DateTimeKind.Utc).ToLocalTime()
        }).ToList();
    }

    // ============================ التسجيل التلقائي ============================

    /// <summary>
    /// حركة تلقائية من مستند (سند، تسليم مندوب) في صندوق المستخدم أو الافتراضي — نفس منطق ترحيل المبيعات
    /// (sp_CashBox_RecordAuto)، وداخل معاملة المستدعي إن وُجدت.
    /// </summary>
    public async Task RecordAutoAsync(int userId, CashBoxTxType type, decimal signedAmount, DateTime date, string referenceTable, int? referenceId,
                                      string? party, string description, int? journalEntryId, int? cashBoxId = null)
    {
        // صندوق المنزل لا يخرج منه إلا سحب الشريك وصرفه خارج العمل (الخمس/مساعدات/أصول)؛ الباقي يُرفض ولو لم تفحصه الشاشة
        if (signedAmount < 0 && cashBoxId is int homeCheck && type != CashBoxTxType.PartnerWithdrawal && type != CashBoxTxType.HomeSpend
            && await IsHomeBoxAsync(homeCheck))
            throw new InvalidOperationException(HomeSpendError);
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
        var depositIds = raw.Where(r => r.ReferenceTable == "CustomerDeposits" && r.ReferenceId != null).Select(r => r.ReferenceId!.Value).ToList();
        var deposits = await _db.CustomerDeposits.Where(d => depositIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.DepositNumber);

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
                    "CustomerDeposits" when r.ReferenceId is { } d => deposits.GetValueOrDefault(d),
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
        CashBoxTxType.CustomerDepositIn => "استلام تأمين عميل",
        CashBoxTxType.CustomerDepositOut => "إرجاع تأمين عميل",
        CashBoxTxType.EmployeeAdvance => "سلفة / مسحوب موظف",
        CashBoxTxType.PartnerWithdrawal => "سحب أرباح شريك",
        CashBoxTxType.CustodyIssue => "صرف عهدة موظف",
        CashBoxTxType.CustodyReturn => "إعادة عهدة موظف",
        CashBoxTxType.HomeSpend => "صرف من صندوق المنزل",
        CashBoxTxType.Adjustment => "تسوية جرد",
        CashBoxTxType.Expense => "مصروف",
        CashBoxTxType.OtherIncome => "إيراد آخر",
        CashBoxTxType.TempWages => "أجور عمال وقتيين",
        CashBoxTxType.PayrollPayout => "صرف رواتب",
        _ => "تسليم نقد مندوب"
    };

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
