using System.Security.Cryptography;
using System.Text.Json;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

// ============================ عقود الهاتف (JSON) ============================

/// <summary>حركة من الهاتف: رقم فريد يولّده الهاتف، والنوع، ووقت حدوثها، ومحتواها.</summary>
public record RepRequestEnvelope(Guid ClientId, RepRequestKind Kind, DateTime OccurredAt, string Payload, byte[]? Photo = null);

/// <summary>الزبون: رقمه في النظام، أو رقم طلب «زبون جديد» أُرسل من الهاتف نفسه.</summary>
public record CustomerRef(int? CustomerId = null, Guid? NewCustomerClientId = null);

public record SaleLinePayload(int ItemId, int PackagingLevelId, decimal Quantity, int? CustomRecipeId = null);

/// <summary>بيع نقدي أو آجل أو مجاني (السعر من النظام دائمًا — لا خصم من الهاتف).</summary>
public record SalePayload(CustomerRef Customer, List<SaleLinePayload> Lines, string? FreeReason = null);

public record CollectionPayload(CustomerRef Customer, decimal Amount);

public record NewCustomerPayload(string Name, string? Phone = null, string? Address = null, string? Province = null);

public record ExpensePayload(decimal Amount, string Category, string? Notes = null, int? VehicleId = null);

public record ReturnLinePayload(int ItemId, int PackagingLevelId, decimal Quantity, decimal Damaged = 0);

/// <summary>مرتجع زبون: الافتراضي خصم قيمته من دينه، والرد النقدي باختيار المندوب (Cash = true).</summary>
public record ReturnPayload(CustomerRef Customer, List<ReturnLinePayload> Lines, string Reason, bool Cash = false);

/// <summary>نتيجة الطلب كما تعود للهاتف.</summary>
public record RepIntakeResult(bool Accepted, RepRequestStatus Status, int? RequestId, string Message, int? ResultId = null, string? Warning = null);

/// <summary>سطر في شاشة «طلبات المندوبين».</summary>
public record RepRequestRow(int Id, DateTime OccurredAt, string RepName, RepRequestKind Kind, RepRequestStatus Status, string Summary, decimal Amount,
                            string? Warning, bool NeedsReview, bool Reviewed, string? ReviewedBy, string? RejectReason, string? ErrorMessage, bool HasPhoto)
{
    public string KindText => RepAppService.KindText(Kind);
    public string StatusText => Status switch
    {
        RepRequestStatus.Posted => NeedsReview ? (Reviewed ? "رُحّل — رُوجع" : "رُحّل — للاطلاع") : "رُحّل",
        RepRequestStatus.Pending => "بانتظار الاعتماد",
        RepRequestStatus.Rejected => "مرفوض",
        _ => "تعذّر"
    };
}

public enum RepRequestFilter { Pending, ToReview, All }

/// <summary>جهاز في شاشة إدارة الأجهزة.</summary>
public record RepDeviceRow(int Id, string RepName, string DeviceName, bool IsActive, DateTime RegisteredAt, DateTime? LastSeenAt, string RegisteredBy);

/// <summary>
/// تطبيق المندوبين — المرحلة 0: يستقبل حركات الهاتف ويرحّلها بخدمات النظام نفسها (الصلاحيات، قفل الشهر، الحجز، القيود):
/// تلقائيًا: البيع النقدي، الآجل والمجاني (للاطلاع بلا رفض)، التحصيل (كامل أو جزئي)، الزبون الجديد (يُربط بالمندوب)؛
/// وبانتظار الاعتماد: المصروف الميداني (بصورة الوصل). الرقم الفريد يجعل إعادة الإرسال آمنة.
/// </summary>
public class RepAppService
{
    private readonly ProjectDbContext _db;
    public RepAppService(ProjectDbContext db) => _db = db;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static string KindText(RepRequestKind k) => k switch
    {
        RepRequestKind.CashSale => "بيع نقدي",
        RepRequestKind.CreditSale => "بيع آجل",
        RepRequestKind.Free => "مجاني",
        RepRequestKind.Collection => "تحصيل",
        RepRequestKind.NewCustomer => "زبون جديد",
        RepRequestKind.Expense => "مصروف",
        _ => "مرتجع زبون"
    };

    // ============================ الأجهزة ============================

    /// <returns>المفتاح السري يُعرض مرة واحدة ليُدخل في الهاتف.</returns>
    public async Task<(FinanceOperationResult result, string? deviceKey)> RegisterDeviceAsync(int repEmployeeId, string deviceName, int userId)
    {
        var rep = await _db.Employees.FirstOrDefaultAsync(e => e.Id == repEmployeeId);
        if (rep is null || !rep.IsSalesRep) return (FinanceOperationResult.Fail("اختر مندوبًا"), null);
        if (string.IsNullOrWhiteSpace(deviceName)) return (FinanceOperationResult.Fail("اكتب اسم الجهاز (مثل: هاتف علي سامسونج)"), null);
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var device = new RepDevice { RepEmployeeId = rep.Id, DeviceName = deviceName.Trim(), DeviceKey = key, RegisteredByUserId = userId };
        _db.RepDevices.Add(device);
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Add", "RepDevices", device.Id, $"تسجيل جهاز {device.DeviceName} للمندوب {rep.FullName}");
        return (FinanceOperationResult.Ok(), key);
    }

    public async Task<FinanceOperationResult> SetDeviceActiveAsync(int deviceId, bool active, int userId)
    {
        var device = await _db.RepDevices.Include(d => d.RepEmployee).FirstOrDefaultAsync(d => d.Id == deviceId);
        if (device is null) return FinanceOperationResult.Fail("الجهاز غير موجود");
        if (device.IsActive == active) return FinanceOperationResult.Fail(active ? "الجهاز مفعّل أصلًا" : "الجهاز موقوف أصلًا");
        device.IsActive = active;
        device.DeactivatedAt = active ? null : DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Edit", "RepDevices", device.Id,
            $"{(active ? "تفعيل" : "إيقاف")} جهاز {device.DeviceName} للمندوب {device.RepEmployee.FullName}");
        return FinanceOperationResult.Ok();
    }

    public async Task<List<RepDeviceRow>> DevicesAsync() =>
        (await _db.RepDevices.AsNoTracking().OrderByDescending(d => d.IsActive).ThenBy(d => d.RepEmployee.FullName)
            .Select(d => new { d.Id, Rep = d.RepEmployee.FullName, d.DeviceName, d.IsActive, d.RegisteredAt, d.LastSeenAt, By = d.RegisteredByUser.Username })
            .ToListAsync())
        .Select(d => new RepDeviceRow(d.Id, d.Rep, d.DeviceName, d.IsActive, d.RegisteredAt.ToLocalTime(), d.LastSeenAt?.ToLocalTime(), d.By)).ToList();

    // ============================ الإعدادات ============================

    public async Task<RepAppSetting> SettingsAsync() =>
        await _db.RepAppSettings.AsNoTracking().FirstOrDefaultAsync() ?? new RepAppSetting();

    public async Task<FinanceOperationResult> SaveSettingsAsync(bool allowCreditOverLimit, int cashAlertDays, int userId)
    {
        if (cashAlertDays is < 1 or > 30) return FinanceOperationResult.Fail("مدة تنبيه النقد بين يوم و30 يومًا");
        var s = await _db.RepAppSettings.FirstOrDefaultAsync();
        if (s is null) _db.RepAppSettings.Add(s = new RepAppSetting());
        (s.AllowCreditOverLimit, s.CashAlertDays) = (allowCreditOverLimit, cashAlertDays);
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Edit", "RepAppSettings", 1,
            $"تطبيق المندوبين: تجاوز حد الدين {(allowCreditOverLimit ? "مسموح" : "ممنوع")}، تنبيه النقد بعد {cashAlertDays} يوم");
        return FinanceOperationResult.Ok();
    }

    // ============================ الاستقبال ============================

    public async Task<RepIntakeResult> ReceiveAsync(string deviceKey, RepRequestEnvelope env)
    {
        var device = await _db.RepDevices.FirstOrDefaultAsync(d => d.DeviceKey == deviceKey);
        if (device is null || !device.IsActive) return new(false, RepRequestStatus.Failed, null, "الجهاز غير مسجّل أو موقوف — راجع الإدارة");
        device.LastSeenAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        if (env.ClientId == Guid.Empty) return new(false, RepRequestStatus.Failed, null, "الحركة بلا رقم فريد");

        // إعادة إرسال الحركة نفسها: النتيجة المحفوظة دون تكرار (والمتعذّرة تُعاد محاولتها)
        var request = await _db.RepRequests.FirstOrDefaultAsync(r => r.ClientId == env.ClientId);
        if (request is not null && request.DeviceId != device.Id) return new(false, RepRequestStatus.Failed, null, "رقم الحركة مستخدم من جهاز آخر");
        if (request is not null && request.Status != RepRequestStatus.Failed) return Result(request);

        if (request is null)
        {
            request = new RepRequest
            {
                ClientId = env.ClientId, DeviceId = device.Id, RepEmployeeId = device.RepEmployeeId, Kind = env.Kind,
                OccurredAt = env.OccurredAt, Payload = env.Payload, Photo = env.Photo, Status = RepRequestStatus.Failed, ErrorMessage = "قيد المعالجة"
            };
            _db.RepRequests.Add(request);
            await _db.SaveChangesAsync();
        }

        try
        {
            await ProcessAsync(device, request);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            request.Status = RepRequestStatus.Failed;
            request.ErrorMessage = ex is JsonException ? "محتوى الحركة غير مفهوم" : ex.Message;
        }
        await _db.SaveChangesAsync();
        return Result(request);
    }

    private static RepIntakeResult Result(RepRequest r) => r.Status switch
    {
        RepRequestStatus.Posted => new(true, r.Status, r.Id, $"رُحّل: {r.Summary}", r.ResultId, r.Warning),
        RepRequestStatus.Pending => new(true, r.Status, r.Id, $"بانتظار الاعتماد: {r.Summary}"),
        RepRequestStatus.Rejected => new(true, r.Status, r.Id, $"مرفوض: {r.RejectReason}"),
        _ => new(false, r.Status, r.Id, r.ErrorMessage ?? "تعذّر ترحيل الحركة")
    };

    private T Parse<T>(RepRequest r) => JsonSerializer.Deserialize<T>(r.Payload, Json) ?? throw new JsonException();

    private async Task ProcessAsync(RepDevice device, RepRequest r)
    {
        r.ErrorMessage = null;
        switch (r.Kind)
        {
            case RepRequestKind.CashSale or RepRequestKind.CreditSale or RepRequestKind.Free:
                await SaleAsync(device, r, Parse<SalePayload>(r));
                break;
            case RepRequestKind.Collection:
                await CollectionAsync(device, r, Parse<CollectionPayload>(r));
                break;
            case RepRequestKind.NewCustomer:
                await NewCustomerAsync(device, r, Parse<NewCustomerPayload>(r));
                break;
            case RepRequestKind.Expense:
                var e = Parse<ExpensePayload>(r);
                if (e.Amount <= 0 || string.IsNullOrWhiteSpace(e.Category)) Fail(r, "المصروف يحتاج مبلغًا ونوعًا");
                else
                {
                    r.Amount = e.Amount;
                    r.Summary = $"{e.Category.Trim()}{(string.IsNullOrWhiteSpace(e.Notes) ? "" : " — " + e.Notes.Trim())}{(r.Photo is null ? "" : " (مع صورة الوصل)")}";
                    r.Status = RepRequestStatus.Pending;
                }
                break;
            default:
                await ReturnIntakeAsync(r, Parse<ReturnPayload>(r));
                break;
        }
    }

    private static void Fail(RepRequest r, string message) { r.Status = RepRequestStatus.Failed; r.ErrorMessage = message; }

    /// <summary>مرتجع الزبون ينتظر الاعتماد: يُتحقق من محتواه الآن، ويُرحَّل عند الموافقة.</summary>
    private async Task ReturnIntakeAsync(RepRequest r, ReturnPayload p)
    {
        var customer = await ResolveCustomerAsync(r, p.Customer);
        if (customer is null) { Fail(r, "الزبون غير موجود"); return; }
        if (string.IsNullOrWhiteSpace(p.Reason)) { Fail(r, "اكتب سبب المرتجع"); return; }
        if (p.Lines is null || p.Lines.Count == 0 || p.Lines.Any(l => l.Quantity <= 0 || l.Damaged < 0 || l.Damaged > l.Quantity))
        { Fail(r, "أضف صنفًا بكمية أكبر من صفر، والتالف جزء منها"); return; }
        var levelIds = p.Lines.Select(l => l.PackagingLevelId).ToList();
        var names = (await _db.ItemPackagingLevels.AsNoTracking().Where(l => levelIds.Contains(l.Id))
                              .Select(l => new { l.Id, l.LevelName, l.Item.ItemName }).ToListAsync())
                    .ToDictionary(l => l.Id);
        r.Status = RepRequestStatus.Pending;
        r.Summary = $"{customer.Name}: {string.Join("، ", p.Lines.Select(l => $"{l.Quantity:#,0.##} {names.GetValueOrDefault(l.PackagingLevelId)?.LevelName} {names.GetValueOrDefault(l.PackagingLevelId)?.ItemName}{(l.Damaged > 0 ? $" (تالف {l.Damaged:#,0.##})" : "")}"))}"
                    + $" — {(p.Cash ? "رد نقدي" : "خصم من الدين")} — {p.Reason.Trim()}";
        if (r.Summary.Length > 300) r.Summary = r.Summary[..300];
    }

    private async Task<Customer?> ResolveCustomerAsync(RepRequest r, CustomerRef? c)
    {
        if (c is null) return null;
        if (c.CustomerId is int id) return await _db.Customers.FirstOrDefaultAsync(x => x.Id == id && x.IsActive);
        if (c.NewCustomerClientId is Guid g)
        {
            var created = await _db.RepRequests.AsNoTracking()
                .Where(x => x.ClientId == g && x.Kind == RepRequestKind.NewCustomer && x.Status == RepRequestStatus.Posted && x.RepEmployeeId == r.RepEmployeeId)
                .Select(x => x.ResultId).FirstOrDefaultAsync();
            return created is int cid ? await _db.Customers.FirstOrDefaultAsync(x => x.Id == cid) : null;
        }
        return null;
    }

    private async Task SaleAsync(RepDevice device, RepRequest r, SalePayload p)
    {
        var van = await _db.Warehouses.AsNoTracking().FirstOrDefaultAsync(w => w.IsActive && w.WarehouseType == WarehouseType.RepVan && w.OwnerEmployeeId == r.RepEmployeeId);
        if (van is null) { Fail(r, "لا توجد سيارة (كاش فان) مسجّلة لهذا المندوب"); return; }
        var customer = await ResolveCustomerAsync(r, p.Customer);
        if (customer is null) { Fail(r, "الزبون غير موجود — أرسل «زبون جديد» أولًا"); return; }
        if (p.Lines is null || p.Lines.Count == 0 || p.Lines.Any(l => l.Quantity <= 0)) { Fail(r, "أضف صنفًا واحدًا على الأقل بكمية أكبر من صفر"); return; }
        if (r.Kind == RepRequestKind.Free && string.IsNullOrWhiteSpace(p.FreeReason)) { Fail(r, "المجاني يحتاج سببًا"); return; }

        var free = r.Kind == RepRequestKind.Free;
        var sales = new SalesService(_db);
        var (created, invoiceId) = await sales.CreateInvoiceAsync(new SalesInvoiceHeaderInput(
            customer.Id, van.Id, r.OccurredAt.Date, r.Kind == RepRequestKind.CreditSale ? InvoicePaymentMethod.Credit : InvoicePaymentMethod.Cash,
            IsFreeSale: free, FreeSaleRecipient: free ? $"{customer.Name} — {p.FreeReason!.Trim()}" : null,
            SalesRepEmployeeId: r.RepEmployeeId, Notes: $"تطبيق المندوب — حركة {r.Id}"), device.RegisteredByUserId);
        if (!created.Success) { Fail(r, created.ErrorMessage ?? "تعذّر إنشاء الفاتورة"); return; }

        foreach (var l in p.Lines)
        {
            // السعر من النظام دائمًا (UnitPrice = null): لا خصم من الهاتف
            var added = await sales.AddLineAsync(invoiceId!.Value, new SalesInvoiceLineInput(l.ItemId, l.PackagingLevelId, l.Quantity, CustomRecipeId: l.CustomRecipeId), device.RegisteredByUserId);
            if (!added.Success)
            {
                await sales.DeleteDraftInvoiceAsync(invoiceId.Value, device.RegisteredByUserId);
                Fail(r, added.ErrorMessage ?? "سطر غير صحيح");
                return;
            }
        }
        var allowOver = r.Kind == RepRequestKind.CreditSale && (await SettingsAsync()).AllowCreditOverLimit;
        // منع التجاوز في إعدادات التطبيق يسري على الهاتف أيًّا كانت صلاحيات من سجّل الجهاز
        if (r.Kind == RepRequestKind.CreditSale && !allowOver && customer.CreditLimit is decimal cap)
        {
            var draftTotal = await _db.SalesInvoiceLines.Where(x => x.SalesInvoiceId == invoiceId).SumAsync(x => (decimal?)x.LineTotal) ?? 0;
            var current = await BalanceAsync(customer.Id);
            if (current + draftTotal > cap)
            {
                await sales.DeleteDraftInvoiceAsync(invoiceId!.Value, device.RegisteredByUserId);
                Fail(r, $"الزبون تجاوز حد دينه ({cap:N0} د.ع): رصيده {current:N0} والفاتورة تضيف {draftTotal:N0}. اقبض نقدًا أو راجع الإدارة.");
                return;
            }
        }
        var (posted, summary) = await sales.PostInvoiceAsync(invoiceId!.Value, device.RegisteredByUserId, allowOverLimit: allowOver);
        if (!posted.Success)
        {
            await sales.DeleteDraftInvoiceAsync(invoiceId.Value, device.RegisteredByUserId);
            Fail(r, posted.ErrorMessage ?? "تعذّر ترحيل الفاتورة");
            return;
        }

        var lines = await _db.SalesInvoiceLines.AsNoTracking().Where(x => x.SalesInvoiceId == invoiceId)
            .Select(x => new { x.QuantityInLevel, x.PackagingLevel.LevelName, x.Item.ItemName }).ToListAsync();
        r.Status = RepRequestStatus.Posted;
        r.ResultTable = "SalesInvoices";
        r.ResultId = invoiceId;
        r.Amount = summary?.TotalAmount ?? 0;
        r.NeedsReview = r.Kind is RepRequestKind.CreditSale or RepRequestKind.Free;
        r.Summary = $"{summary?.InvoiceNumber} — {customer.Name}: {string.Join("، ", lines.Select(x => $"{x.QuantityInLevel:#,0.##} {x.LevelName} {x.ItemName}"))}"
                    + (free ? $" — {p.FreeReason!.Trim()}" : "");
        if (r.Summary.Length > 300) r.Summary = r.Summary[..300];
        if (r.Kind == RepRequestKind.CreditSale && customer.CreditLimit is decimal limit)
        {
            var balance = await BalanceAsync(customer.Id);
            if (balance > limit) r.Warning = $"تجاوز حد الدين: الرصيد {balance:N0} من حد {limit:N0} د.ع";
        }
    }

    private Task<decimal> BalanceAsync(int customerId) =>
        _db.Database.SqlQueryRaw<decimal>("SELECT ISNULL((SELECT Balance FROM vw_CustomerBalances WHERE CustomerId = {0}), 0) AS Value", customerId).FirstAsync();

    private async Task CollectionAsync(RepDevice device, RepRequest r, CollectionPayload p)
    {
        var customer = await ResolveCustomerAsync(r, p.Customer);
        if (customer is null) { Fail(r, "الزبون غير موجود"); return; }
        if (p.Amount <= 0) { Fail(r, "مبلغ التحصيل يجب أن يكون أكبر من صفر"); return; }
        var done = await new RepsService(_db).RecordDebtCollectionAsync(r.RepEmployeeId, customer.Id, p.Amount, r.OccurredAt, device.RegisteredByUserId);
        if (!done.Success) { Fail(r, done.ErrorMessage ?? "تعذّر تسجيل التحصيل"); return; }
        r.Status = RepRequestStatus.Posted;
        r.ResultTable = "Vouchers";
        r.ResultId = await _db.Vouchers.Where(v => v.PartyType == VoucherPartyType.Customer && v.PartyId == customer.Id).MaxAsync(v => (int?)v.Id);
        r.Amount = p.Amount;
        r.Summary = $"تحصيل من {customer.Name}";
    }

    private async Task NewCustomerAsync(RepDevice device, RepRequest r, NewCustomerPayload p)
    {
        var name = (p.Name ?? "").Trim();
        if (name.Length == 0) { Fail(r, "اكتب اسم الزبون"); return; }
        var customer = new Customer { Name = name.Length > 200 ? name[..200] : name, Phone = p.Phone?.Trim(), Address = p.Address?.Trim(), Province = p.Province?.Trim() };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();
        _db.RepCustomerAssignments.Add(new RepCustomerAssignment { EmployeeId = r.RepEmployeeId, CustomerId = customer.Id });
        await _db.SaveChangesAsync();
        r.Status = RepRequestStatus.Posted;
        r.ResultTable = "Customers";
        r.ResultId = customer.Id;
        r.Summary = $"زبون جديد: {customer.Name}{(string.IsNullOrWhiteSpace(customer.Phone) ? "" : " — " + customer.Phone)}";
        if (await _db.Customers.AnyAsync(c => c.Id != customer.Id && c.Name == customer.Name)) r.Warning = "يوجد زبون آخر بالاسم نفسه";
        await new AuditService(_db).LogAsync(device.RegisteredByUserId, "Add", "Customers", customer.Id, $"زبون جديد من تطبيق المندوب: {customer.Name}");
    }

    // ============================ الاعتماد والمراجعة ============================

    public async Task<FinanceOperationResult> ApproveAsync(int requestId, int userId)
    {
        if (!await SpecialPermission.HasAsync(_db, userId, SpecialPermission.RepApproval))
            return FinanceOperationResult.Fail($"الاعتماد يحتاج صلاحية «{SpecialPermission.NameOf(SpecialPermission.RepApproval)}»");
        var r = await _db.RepRequests.FirstOrDefaultAsync(x => x.Id == requestId);
        if (r is null || r.Status != RepRequestStatus.Pending) return FinanceOperationResult.Fail("الطلب ليس بانتظار الاعتماد");
        if (r.Kind == RepRequestKind.Return) return await ApproveReturnAsync(r, userId);
        if (r.Kind != RepRequestKind.Expense) return FinanceOperationResult.Fail("هذا النوع لا يُعتمد من هنا");

        var e = Parse<ExpensePayload>(r);
        var description = $"{e.Category.Trim()}{(string.IsNullOrWhiteSpace(e.Notes) ? "" : " — " + e.Notes.Trim())}";
        var done = await new RepsService(_db).RecordFieldExpenseAsync(r.RepEmployeeId, e.Amount, description, r.OccurredAt, userId, e.VehicleId);
        if (!done.Success)
        {
            r.ErrorMessage = done.ErrorMessage;
            await _db.SaveChangesAsync();
            return done;
        }
        r.Status = RepRequestStatus.Posted;
        r.ErrorMessage = null;
        (r.ReviewedByUserId, r.ReviewedAt) = (userId, DateTime.UtcNow);
        r.ResultTable = "RepWalletTransactions";
        r.ResultId = await _db.RepWalletTransactions.Where(w => w.EmployeeId == r.RepEmployeeId).MaxAsync(w => (int?)w.Id);
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Post", "RepRequests", r.Id, $"اعتماد {KindText(r.Kind)}: {r.Summary} — {r.Amount:N0}");
        return FinanceOperationResult.Ok();
    }

    private async Task<FinanceOperationResult> ApproveReturnAsync(RepRequest r, int userId)
    {
        var p = Parse<ReturnPayload>(r);
        var van = await _db.Warehouses.AsNoTracking().FirstOrDefaultAsync(w => w.IsActive && w.WarehouseType == WarehouseType.RepVan && w.OwnerEmployeeId == r.RepEmployeeId);
        var customer = await ResolveCustomerAsync(r, p.Customer);
        if (van is null || customer is null) return FinanceOperationResult.Fail(van is null ? "لا توجد سيارة لهذا المندوب" : "الزبون غير موجود");
        var (done, created) = await new CustomerReturnService(_db).CreateAsync(new CustomerReturnRequest(
            customer.Id, van.Id, r.OccurredAt, p.Cash ? CustomerReturnSettlement.Cash : CustomerReturnSettlement.Debt, p.Reason,
            p.Lines.Select(l => new CustomerReturnLineInput(l.ItemId, l.PackagingLevelId, l.Quantity, l.Damaged)).ToList(), userId, r.Id));
        if (!done.Success)
        {
            r.ErrorMessage = done.ErrorMessage;
            await _db.SaveChangesAsync();
            return done;
        }
        r.Status = RepRequestStatus.Posted;
        r.ErrorMessage = null;
        r.Amount = created!.TotalAmount;
        (r.ReviewedByUserId, r.ReviewedAt) = (userId, DateTime.UtcNow);
        (r.ResultTable, r.ResultId) = ("CustomerReturns", created.Id);
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Post", "RepRequests", r.Id, $"اعتماد مرتجع زبون: {created.ReturnNumber} — {created.TotalAmount:N0}");
        return FinanceOperationResult.Ok();
    }

    public async Task<FinanceOperationResult> RejectAsync(int requestId, string reason, int userId)
    {
        if (!await SpecialPermission.HasAsync(_db, userId, SpecialPermission.RepApproval))
            return FinanceOperationResult.Fail($"الرفض يحتاج صلاحية «{SpecialPermission.NameOf(SpecialPermission.RepApproval)}»");
        if (string.IsNullOrWhiteSpace(reason)) return FinanceOperationResult.Fail("اكتب سبب الرفض ليصل للمندوب");
        var r = await _db.RepRequests.FirstOrDefaultAsync(x => x.Id == requestId);
        // الآجل والمجاني لا يُرفضان (سياسة المعمل): يُرحّلان ويُراجَعان فقط
        if (r is null || r.Status != RepRequestStatus.Pending) return FinanceOperationResult.Fail("لا يُرفض إلا طلب بانتظار الاعتماد (المصروف ومرتجع الزبون)");
        r.Status = RepRequestStatus.Rejected;
        r.RejectReason = reason.Trim();
        (r.ReviewedByUserId, r.ReviewedAt) = (userId, DateTime.UtcNow);
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Edit", "RepRequests", r.Id, $"رفض {KindText(r.Kind)}: {r.Summary} — {r.RejectReason}");
        return FinanceOperationResult.Ok();
    }

    /// <summary>الآجل والمجاني «للاطلاع»: علامة «تمت المراجعة» بلا رفض.</summary>
    public async Task<FinanceOperationResult> MarkReviewedAsync(IReadOnlyCollection<int> requestIds, int userId)
    {
        if (!await SpecialPermission.HasAsync(_db, userId, SpecialPermission.RepApproval))
            return FinanceOperationResult.Fail($"المراجعة تحتاج صلاحية «{SpecialPermission.NameOf(SpecialPermission.RepApproval)}»");
        var rows = await _db.RepRequests.Where(r => requestIds.Contains(r.Id) && r.NeedsReview && r.ReviewedAt == null && r.Status == RepRequestStatus.Posted).ToListAsync();
        if (rows.Count == 0) return FinanceOperationResult.Fail("لا شيء للمراجعة");
        foreach (var r in rows) (r.ReviewedByUserId, r.ReviewedAt) = (userId, DateTime.UtcNow);
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Edit", "RepRequests", null, $"مراجعة {rows.Count} حركة آجل/مجاني من المندوبين");
        return FinanceOperationResult.Ok();
    }

    public async Task<List<RepRequestRow>> ListAsync(RepRequestFilter filter, DateTime from, DateTime to, int? repEmployeeId = null)
    {
        var (f, t) = (from.Date.ToUniversalTime(), to.Date.AddDays(1).ToUniversalTime());
        var q = _db.RepRequests.AsNoTracking().Where(r => repEmployeeId == null || r.RepEmployeeId == repEmployeeId);
        q = filter switch
        {
            RepRequestFilter.Pending => q.Where(r => r.Status == RepRequestStatus.Pending),
            RepRequestFilter.ToReview => q.Where(r => r.Status == RepRequestStatus.Posted && r.NeedsReview && r.ReviewedAt == null),
            _ => q.Where(r => r.ReceivedAt >= f && r.ReceivedAt < t)
        };
        return (await q.OrderByDescending(r => r.Id)
                .Select(r => new
                {
                    r.Id, r.OccurredAt, Rep = r.RepEmployee.FullName, r.Kind, r.Status, r.Summary, r.Amount, r.Warning, r.NeedsReview,
                    Reviewed = r.ReviewedAt != null, By = r.ReviewedByUser != null ? r.ReviewedByUser.Username : null, r.RejectReason, r.ErrorMessage,
                    HasPhoto = r.Photo != null
                }).ToListAsync())
            .Select(r => new RepRequestRow(r.Id, r.OccurredAt, r.Rep, r.Kind, r.Status, r.Summary, r.Amount, r.Warning, r.NeedsReview, r.Reviewed, r.By,
                                           r.RejectReason, r.ErrorMessage, r.HasPhoto)).ToList();
    }

    public Task<byte[]?> PhotoAsync(int requestId) =>
        _db.RepRequests.AsNoTracking().Where(r => r.Id == requestId).Select(r => r.Photo).FirstOrDefaultAsync();

    /// <summary>طلبات المندوب المعلّقة (تمنع إغلاق تسويته حتى تُعتمد أو تُرفض).</summary>
    public Task<int> PendingCountAsync(int repEmployeeId) =>
        _db.RepRequests.CountAsync(r => r.RepEmployeeId == repEmployeeId && r.Status == RepRequestStatus.Pending);
}
