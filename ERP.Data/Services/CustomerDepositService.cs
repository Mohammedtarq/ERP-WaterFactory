using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>سطر في قائمة أرصدة التأمينات.</summary>
public class CustomerDepositBalanceRow
{
    public int CustomerId { get; init; }
    public string CustomerName { get; init; } = "";
    public decimal Received { get; init; }
    public decimal Refunded { get; init; }
    public decimal Balance => Received - Refunded;
    public DateTime LastDate { get; init; }
}

/// <summary>سند تأمين في سجل العميل مع الرصيد التراكمي.</summary>
public class CustomerDepositRow
{
    public int Id { get; init; }
    public string DepositNumber { get; init; } = "";
    public DateTime DepositDate { get; init; }
    public CustomerDepositKind Kind { get; init; }
    public string KindText => CustomerDepositService.KindLabel(Kind);
    public decimal In { get; init; }
    public decimal Out { get; init; }
    public decimal Balance { get; init; }
    public string? CurrencyText { get; init; }
    public string? Purpose { get; init; }
    public string? RecipeName { get; init; }
    public string? Notes { get; init; }
    public string CreatedBy { get; init; } = "";
    public bool IsVoided { get; init; }
    public string? VoidReason { get; init; }
}

/// <summary>
/// تأمينات العملاء: مبالغ يودعها العميل كأمانة (مثل تأمين طباعة ستيكر خاص باسمه).
/// منفصلة تمامًا عن الدين — لا تدخل كشف الفواتير ولا توزيع الدفعات. الاستلام يدخل الصندوق والإرجاع يخرج منه،
/// وكل سند يقيَّد على حساب "تأمينات العملاء" (التزام). الرصيد الافتتاحي (نقل من نظام سابق) بلا حركة صندوق.
/// </summary>
public class CustomerDepositService
{
    public const string ReceiptRule = "CustomerDepositReceipt";   // مدين الصندوق / دائن تأمينات العملاء
    public const string RefundRule = "CustomerDepositRefund";     // مدين تأمينات العملاء / دائن الصندوق
    public const string OpeningRule = "CustomerDepositOpening";   // مدين رأس المال / دائن تأمينات العملاء

    private readonly ProjectDbContext _db;
    public CustomerDepositService(ProjectDbContext db) => _db = db;

    public static string KindLabel(CustomerDepositKind k) => k switch
    {
        CustomerDepositKind.Receipt => "استلام تأمين",
        CustomerDepositKind.Refund => "إرجاع تأمين",
        _ => "رصيد افتتاحي"
    };

    public async Task<decimal> GetBalanceAsync(int customerId) =>
        await _db.CustomerDeposits.Where(d => d.CustomerId == customerId && !d.IsVoided)
                 .SumAsync(d => (decimal?)(d.Kind == CustomerDepositKind.Refund ? -d.Amount : d.Amount)) ?? 0;

    public Task<(FinanceOperationResult result, CustomerDeposit? deposit)> ReceiveAsync(int customerId, decimal amount, DateTime date, int userId,
        string? purpose = null, int? customRecipeId = null, string? notes = null, string currency = "IQD", decimal? currencyAmount = null, int? cashBoxId = null) =>
        CreateAsync(CustomerDepositKind.Receipt, customerId, amount, date, userId, purpose, customRecipeId, notes, currency, currencyAmount, cashBoxId);

    public Task<(FinanceOperationResult result, CustomerDeposit? deposit)> RefundAsync(int customerId, decimal amount, DateTime date, int userId,
        string? notes = null, int? cashBoxId = null) =>
        CreateAsync(CustomerDepositKind.Refund, customerId, amount, date, userId, null, null, notes, "IQD", null, cashBoxId);

    /// <summary>رصيد تأمين منقول من نظام سابق: قيد فقط، دون حركة صندوق (النقد محسوب في رصيد الصندوق الافتتاحي).</summary>
    public Task<(FinanceOperationResult result, CustomerDeposit? deposit)> OpeningAsync(int customerId, decimal amount, DateTime date, int userId, string? notes = null) =>
        CreateAsync(CustomerDepositKind.Opening, customerId, amount, date, userId, null, null, notes, "IQD", null, null);

    private async Task<(FinanceOperationResult, CustomerDeposit?)> CreateAsync(CustomerDepositKind kind, int customerId, decimal amount, DateTime date, int userId,
        string? purpose, int? customRecipeId, string? notes, string currency, decimal? currencyAmount, int? cashBoxId)
    {
        if (amount <= 0) return (FinanceOperationResult.Fail("المبلغ يجب أن يكون أكبر من صفر"), null);
        var customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId);
        if (customer is null) return (FinanceOperationResult.Fail("اختر العميل"), null);
        currency = string.IsNullOrWhiteSpace(currency) ? "IQD" : currency.Trim().ToUpperInvariant();
        if (currency != "IQD" && (currencyAmount is null or <= 0))
            return (FinanceOperationResult.Fail($"أدخل المبلغ المستلم بعملة {currency} إضافة إلى ما يعادله بالدينار"), null);
        if (customRecipeId is int rid && !await _db.CustomRecipes.AnyAsync(r => r.Id == rid && r.CustomerId == customerId))
            return (FinanceOperationResult.Fail("الوصفة (الستيكر الخاص) المختارة ليست لهذا العميل"), null);

        if (kind == CustomerDepositKind.Refund)
        {
            var balance = await GetBalanceAsync(customerId);
            if (amount > balance) return (FinanceOperationResult.Fail($"رصيد تأمين العميل {balance:N0} د.ع فقط — لا يمكن إرجاع أكثر منه"), null);
        }

        int? boxId = null;
        if (kind != CustomerDepositKind.Opening)
        {
            boxId = cashBoxId ?? await ResolveBoxAsync(userId);
            if (boxId is null) return (FinanceOperationResult.Fail("لا يوجد صندوق مفعّل لتسجيل النقد فيه — أنشئ صندوقًا من المالية ← الصناديق"), null);
            if (cashBoxId is not null && !await _db.CashBoxes.AnyAsync(b => b.Id == cashBoxId && b.IsActive))
                return (FinanceOperationResult.Fail("الصندوق غير موجود أو موقوف"), null);
            if (kind == CustomerDepositKind.Refund)
            {
                var boxBalance = await new CashBoxService(_db).GetBalanceAsync(boxId.Value);
                if (boxBalance < amount) return (FinanceOperationResult.Fail($"رصيد الصندوق غير كافٍ للإرجاع: المتاح {boxBalance:N0} د.ع"), null);
            }
        }

        await using var tx = await _db.Database.BeginTransactionAsync();
        var number = await NextNumberAsync(date);
        var rule = kind switch { CustomerDepositKind.Receipt => ReceiptRule, CustomerDepositKind.Refund => RefundRule, _ => OpeningRule };
        var text = $"{KindLabel(kind)} {number} — {customer.Name}" + (string.IsNullOrWhiteSpace(purpose) ? "" : $" ({purpose.Trim()})");
        var (entry, error) = await LedgerHelper.PostJournalAsync(_db, rule, amount, date, JournalEntryType.AutoVoucher, text, userId, "CustomerDeposits", null, "DP");
        if (error is not null) return (FinanceOperationResult.Fail(error), null);
        await _db.SaveChangesAsync();

        var deposit = new CustomerDeposit
        {
            DepositNumber = number, CustomerId = customerId, DepositDate = date.Date, Kind = kind, Amount = amount,
            Currency = currency, CurrencyAmount = currency == "IQD" ? null : currencyAmount,
            Purpose = Clean(purpose), CustomRecipeId = customRecipeId, Notes = Clean(notes),
            JournalEntryId = entry!.Id, CreatedByUserId = userId
        };
        _db.CustomerDeposits.Add(deposit);
        await _db.SaveChangesAsync();
        entry.SourceId = deposit.Id;
        await _db.SaveChangesAsync();

        if (boxId is int box)
            await new CashBoxService(_db).RecordAutoAsync(userId,
                kind == CustomerDepositKind.Receipt ? CashBoxTxType.CustomerDepositIn : CashBoxTxType.CustomerDepositOut,
                kind == CustomerDepositKind.Receipt ? amount : -amount, date, "CustomerDeposits", deposit.Id, customer.Name, text, entry.Id, box);

        await tx.CommitAsync();
        return (FinanceOperationResult.Ok(), deposit);
    }

    /// <summary>
    /// إلغاء سند تأمين (للأدمن): تُلغى حركة الصندوق المرتبطة ويُعكس القيد. يُرفض إن جعل رصيد تأمين العميل
    /// أو رصيد الصندوق سالبًا (مثلًا إلغاء استلام سبق إرجاعه).
    /// </summary>
    public async Task<FinanceOperationResult> VoidAsync(int depositId, string reason, int userId)
    {
        var cash = new CashBoxService(_db);
        if (!await cash.IsAdminAsync(userId)) return FinanceOperationResult.Fail("إلغاء سندات التأمين للأدمن فقط");
        if (string.IsNullOrWhiteSpace(reason)) return FinanceOperationResult.Fail("اكتب سبب الإلغاء");
        var d = await _db.CustomerDeposits.Include(x => x.JournalEntry!).ThenInclude(j => j.Lines).FirstOrDefaultAsync(x => x.Id == depositId);
        if (d is null) return FinanceOperationResult.Fail("السند غير موجود");
        if (d.IsVoided) return FinanceOperationResult.Fail("السند ملغى مسبقًا");

        if (d.Kind != CustomerDepositKind.Refund)
        {
            var after = await GetBalanceAsync(d.CustomerId) - d.Amount;
            if (after < 0) return FinanceOperationResult.Fail($"الإلغاء يجعل رصيد تأمين العميل سالبًا ({after:N0} د.ع) — ألغِ سند الإرجاع اللاحق أولًا");
        }
        var cashTx = await _db.CashBoxTransactions.FirstOrDefaultAsync(t => t.ReferenceTable == "CustomerDeposits" && t.ReferenceId == d.Id && !t.IsVoided);
        if (cashTx is not null && cashTx.Amount > 0)
        {
            var boxAfter = await cash.GetBalanceAsync(cashTx.CashBoxId) - cashTx.Amount;
            if (boxAfter < 0) return FinanceOperationResult.Fail($"الإلغاء يجعل رصيد الصندوق سالبًا ({boxAfter:N0} د.ع)");
        }

        await using var tx = await _db.Database.BeginTransactionAsync();
        d.IsVoided = true;
        d.VoidReason = reason.Trim();
        if (cashTx is not null)
        {
            cashTx.IsVoided = true;
            cashTx.VoidReason = reason.Trim();
            cashTx.ModifiedByUserId = userId;
            cashTx.ModifiedAt = DateTime.UtcNow;
        }
        if (d.JournalEntry is { } je)
        {
            var count = await _db.JournalEntries.CountAsync();
            var reversal = new JournalEntry
            {
                EntryNumber = $"DPR-{count + 1:D5}", EntryDate = DateTime.Today, EntryType = JournalEntryType.AutoVoucher,
                Description = $"عكس قيد سند تأمين ملغى {d.DepositNumber}: {reason.Trim()}", CreatedByUserId = userId, IsPosted = true,
                SourceTable = "CustomerDeposits", SourceId = d.Id
            };
            foreach (var line in je.Lines)
                reversal.Lines.Add(new JournalEntryLine { AccountId = line.AccountId, Debit = line.Credit, Credit = line.Debit, Description = "عكس: " + line.Description });
            _db.JournalEntries.Add(reversal);
        }
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return FinanceOperationResult.Ok();
    }

    /// <summary>سجل تأمينات العميل بالترتيب الزمني مع الرصيد التراكمي (الملغى يظهر ولا يؤثر).</summary>
    public async Task<List<CustomerDepositRow>> GetHistoryAsync(int customerId)
    {
        var raw = await _db.CustomerDeposits.AsNoTracking().Where(d => d.CustomerId == customerId)
            .OrderBy(d => d.DepositDate).ThenBy(d => d.Id)
            .Select(d => new
            {
                d.Id, d.DepositNumber, d.DepositDate, d.Kind, d.Amount, d.Currency, d.CurrencyAmount, d.Purpose,
                Recipe = d.CustomRecipe != null ? d.CustomRecipe.Name : null, d.Notes, User = d.CreatedByUser.Username, d.IsVoided, d.VoidReason
            }).ToListAsync();
        decimal balance = 0;
        var rows = new List<CustomerDepositRow>();
        foreach (var d in raw)
        {
            var signed = d.Kind == CustomerDepositKind.Refund ? -d.Amount : d.Amount;
            if (!d.IsVoided) balance += signed;
            rows.Add(new CustomerDepositRow
            {
                Id = d.Id, DepositNumber = d.DepositNumber, DepositDate = d.DepositDate, Kind = d.Kind,
                In = signed > 0 ? d.Amount : 0, Out = signed < 0 ? d.Amount : 0, Balance = balance,
                CurrencyText = d.Currency == "IQD" || d.CurrencyAmount is null ? null : $"{d.CurrencyAmount:N2} {d.Currency}",
                Purpose = d.Purpose, RecipeName = d.Recipe, Notes = d.Notes, CreatedBy = d.User, IsVoided = d.IsVoided, VoidReason = d.VoidReason
            });
        }
        return rows;
    }

    /// <summary>العملاء الذين لديهم تأمينات (رصيد قائم أولًا).</summary>
    public async Task<List<CustomerDepositBalanceRow>> GetBalancesAsync()
    {
        var rows = await _db.CustomerDeposits.AsNoTracking().Where(d => !d.IsVoided)
            .GroupBy(d => new { d.CustomerId, d.Customer.Name })
            .Select(g => new CustomerDepositBalanceRow
            {
                CustomerId = g.Key.CustomerId, CustomerName = g.Key.Name,
                Received = g.Where(d => d.Kind != CustomerDepositKind.Refund).Sum(d => d.Amount),
                Refunded = g.Where(d => d.Kind == CustomerDepositKind.Refund).Sum(d => d.Amount),
                LastDate = g.Max(d => d.DepositDate)
            }).ToListAsync();
        return rows.OrderByDescending(r => r.Balance).ThenBy(r => r.CustomerName).ToList();
    }

    public Task<CustomerDeposit?> GetAsync(int id) =>
        _db.CustomerDeposits.AsNoTracking().Include(d => d.Customer).Include(d => d.CustomRecipe).Include(d => d.CreatedByUser)
           .FirstOrDefaultAsync(d => d.Id == id);

    /// <summary>نفس ترتيب sp_CashBox_RecordAuto: صندوق المستخدم ← الافتراضي ← أي صندوق رئيسي.</summary>
    private async Task<int?> ResolveBoxAsync(int userId) =>
        await _db.CashBoxes.Where(b => b.IsActive && (b.OwnerUserId == userId || b.IsDefault || b.BoxType == CashBoxType.Main))
            .OrderBy(b => b.OwnerUserId == userId ? 0 : b.IsDefault ? 1 : 2).ThenBy(b => b.Id)
            .Select(b => (int?)b.Id).FirstOrDefaultAsync();

    private async Task<string> NextNumberAsync(DateTime date)
    {
        var n = (await _db.Database.SqlQueryRaw<int>("SELECT NEXT VALUE FOR seq_CustomerDeposit AS [Value]").ToListAsync())[0];
        return $"DP-{date.Year}-{n:D6}";
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
