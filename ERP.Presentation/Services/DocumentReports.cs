using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.Services;

/// <summary>
/// المستندات القابلة للطباعة في كل الوحدات، تُبنى من قاعدة البيانات مباشرة (نفس النسخة المحفوظة لا ما على الشاشة):
/// أمر الإنتاج، نتيجة المختبر، التعبئة، السندات، القيود، أوامر الشراء، الاستلام، الرواتب، والفواتير.
/// </summary>
public static class DocumentReports
{
    private static ReportDocument New(AppSession s, string title, string? stamp = null, string? notes = null) =>
        new() { CompanyName = s.ProjectName, Title = title, Stamp = stamp, Notes = notes, PrintedBy = s.FullName };

    private static string N(decimal v) => $"{v:N0}";
    private static string Q(decimal v) => $"{v:#,0.###}";

    // ============================ الإنتاج ============================

    public static async Task<ReportDocument?> ProductionOrderAsync(AppSession s, ProjectDbContext db, int orderId)
    {
        var o = await db.ProductionOrders.AsNoTracking()
            .Include(x => x.FinishedItem).Include(x => x.RawMaterialsWarehouse).Include(x => x.OutputBatch)
            .Include(x => x.CustomRecipe).Include(x => x.CreatedByUser).Include(x => x.Machine)
            .Include(x => x.Consumptions).ThenInclude(c => c.RawMaterialItem)
            .FirstOrDefaultAsync(x => x.Id == orderId);
        if (o is null) return null;
        var lines = (await new ProductionService(db).GetLinesAsync(orderId)).OrderBy(l => l.LineNo).ToList();
        var originals = await db.ProductionOrderLines.AsNoTracking().Where(l => l.ProductionOrderId == orderId && l.OutputBatch != null && l.OutputBatch.OriginalBatchNumber != null)
            .Select(l => new { l.LineNo, l.OutputBatch!.OriginalBatchNumber }).ToDictionaryAsync(x => x.LineNo, x => x.OriginalBatchNumber);
        string QcText(QCOverallResult? q) => q is null ? "لم يُفحص بعد" : ArabicLabels.Of(q.Value);

        var r = New(s, "أمر إنتاج", o.Status == ProductionOrderStatus.Cancelled ? "ملغى" : null);
        r.Field("رقم الأمر", o.MONumber);
        if (lines.Count <= 1)
        {
            var l = lines.FirstOrDefault();
            r.Field("المنتج", $"{o.FinishedItem.ItemName} ({o.FinishedItem.ItemCode})")
             .Field("الكمية المطلوبة", $"{Q(o.QuantityToProduce)} قطعة")
             .Field("الوصفة", o.CustomRecipe?.Name ?? "الوصفة الأساسية")
             .Field("رقم الدفعة", l?.OutputBatch)
             .Field("الرقم الأصلي للدفعة", originals.GetValueOrDefault(1))
             .Field("نتيجة المختبر", QcText(l?.LastQc))
             .Field("المعبّأ", l is null || l.PackedQuantity == 0 ? null : $"{Q(l.PackedQuantity)} قطعة");
        }
        else
        {
            // أمر متعدد الأصناف: سطر لكل صنف بكميته ودفعته ونتيجة مختبره وما عُبّئ منه
            foreach (var l in lines)
                r.Field($"الصنف {l.LineNo}", $"{l.FinishedItemName} — {Q(l.QuantityToProduce)} قطعة — دفعة {l.OutputBatch}"
                                              + (originals.TryGetValue(l.LineNo, out var orig) ? $" (الأصلي {orig})" : "")
                                              + (l.RecipeName is null ? "" : $" — {l.RecipeName}")
                                              + $" — المختبر: {QcText(l.LastQc)} — المعبّأ {Q(l.PackedQuantity)}");
            r.Field("إجمالي الكمية", $"{Q(o.QuantityToProduce)} قطعة");
        }
        foreach (var ov in await new PackagingTemplateService(db).GetOrderOverridesAsync(orderId))
            r.Field("استبدال مكوّن", $"{ov.Line.FinishedItem.ItemName}: {ov.OriginalItem.ItemName} ← {ov.ReplacementItem.ItemName} ({Q(ov.Quantity)}) — {ov.Reason} — {ov.ChangedByUser.Username}");
        r.Field("الحالة", ArabicLabels.Of(o.Status))
         .Field("مخزن المواد", o.RawMaterialsWarehouse.Name)
         .Field("الماكينة", o.Machine?.Name)
         .Field("أنشأه", o.CreatedByUser.Username);
        // مطابقة المواد: المصروف لتحت تصنيع الماكينة = المستهلك فعليًا + التالف + المُرجَع + المتبقي على الماكينة
        var materials = await new MachineService(db).GetOrderMaterialsAsync(orderId);
        var multi = lines.Count > 1;
        r.Columns.AddRange(multi
            ? new[] { "#", "المادة الأولية", "الكود", "للأصناف", "المطلوب", "المصروف", "المستهلك فعليًا", "التالف", "المتبقي على الماكينة" }
            : new[] { "#", "المادة الأولية", "الكود", "المطلوب", "المصروف", "المستهلك فعليًا", "التالف", "المتبقي على الماكينة" });
        var codes = o.Consumptions.GroupBy(c => c.RawMaterialItemId).ToDictionary(g => g.Key, g => g.First().RawMaterialItem.ItemCode);
        var i = 0;
        foreach (var m in materials)
        {
            var cells = new List<string> { (++i).ToString(), m.RawItemName, codes.GetValueOrDefault(m.RawItemId, "") };
            if (multi) cells.Add(m.IsShared ? $"مشتركة: {m.UsedBy}" : m.UsedBy);
            cells.AddRange(new[] { Q(m.Required), Q(m.Issued), Q(m.Consumed), Q(m.Damaged), Q(m.Remaining) });
            r.Rows.Add(cells.ToArray());
        }
        r.Total("عدد المواد", materials.Count.ToString()).Total("الكمية المطلوبة", $"{Q(o.QuantityToProduce)} قطعة", true);
        r.Signatures.AddRange(new[] { "مسؤول الإنتاج", "أمين مخزن المواد", "مراقب الجودة" });
        return r;
    }

    public static async Task<ReportDocument?> QcResultAsync(AppSession s, ProjectDbContext db, int qcResultId)
    {
        var q = await db.QCBatchResults.AsNoTracking()
            .Include(x => x.ProductionOrder).ThenInclude(o => o.FinishedItem)
            .Include(x => x.Batch).Include(x => x.TestedByUser)
            .Include(x => x.ResultLines).ThenInclude(l => l.QualityTest)
            .FirstOrDefaultAsync(x => x.Id == qcResultId);
        if (q is null) return null;
        var r = New(s, "شهادة فحص مختبري", q.OverallResult == QCOverallResult.Rejected ? "مرفوضة" : null);
        r.Field("أمر الإنتاج", q.ProductionOrder.MONumber)
         .Field("المنتج", q.ProductionOrder.FinishedItem.ItemName)
         .Field("رقم الدفعة", q.Batch.BatchNumber)
         .Field("تاريخ الفحص", q.TestDate.ToLocalTime().ToString("yyyy/MM/dd HH:mm"))
         .Field("الفاحص", q.TestedByUser.Username)
         .Field("النتيجة النهائية", ArabicLabels.Of(q.OverallResult));
        r.Columns.AddRange(new[] { "#", "الاختبار", "المعيار", "القيمة المقاسة", "النتيجة" });
        var i = 0;
        foreach (var l in q.ResultLines.OrderBy(l => l.QualityTest.TestName))
        {
            var t = l.QualityTest;
            var standard = t.StandardText ?? (t.StandardMin, t.StandardMax) switch
            {
                ({ } min, { } max) => $"{min:0.###} — {max:0.###}",
                ({ } min, null) => $"≥ {min:0.###}",
                (null, { } max) => $"≤ {max:0.###}",
                _ => "—"
            };
            r.Rows.Add(new[] { (++i).ToString(), t.TestName, standard, l.MeasuredValue, ArabicLabels.Of(l.Result) });
        }
        r.Total("عدد الاختبارات", q.ResultLines.Count.ToString())
         .Total("الراسب", q.ResultLines.Count(l => l.Result == QCLineResult.Fail).ToString())
         .Total("النتيجة", ArabicLabels.Of(q.OverallResult), true);
        r.Signatures.AddRange(new[] { "الفاحص", "مدير الجودة" });
        return r;
    }

    public static async Task<ReportDocument?> PackingAsync(AppSession s, ProjectDbContext db, int orderId)
    {
        var o = await db.ProductionOrders.AsNoTracking().Include(x => x.FinishedItem).Include(x => x.OutputBatch).FirstOrDefaultAsync(x => x.Id == orderId);
        if (o is null) return null;
        var rows = await db.PackingOrders.AsNoTracking().Where(p => p.ProductionOrderId == orderId).OrderBy(p => p.Id)
            .Select(p => new { p.PackingDate, Item = p.PackagingLevel.Item.ItemName, Batch = p.Line != null && p.Line.OutputBatch != null ? p.Line.OutputBatch.BatchNumber : null,
                               Level = p.PackagingLevel.LevelName, p.UnitsPackaged, Pieces = p.UnitsPackaged * p.PackagingLevel.EquivalentBaseUnits,
                               Warehouse = p.ResultingFinishedGoodsWarehouse.Name, User = p.CreatedByUser.Username })
            .ToListAsync();
        var r = New(s, "محضر تعبئة");
        var lines = await new ProductionService(db).GetLinesAsync(orderId);
        r.Field("أمر الإنتاج", o.MONumber).Field("المنتج", string.Join(" + ", lines.OrderBy(l => l.LineNo).Select(l => l.FinishedItemName)))
         .Field("رقم الدفعة", string.Join("، ", lines.OrderBy(l => l.LineNo).Select(l => l.OutputBatch)))
         .Field("المطلوب إنتاجه", $"{Q(o.QuantityToProduce)} قطعة");
        r.Columns.AddRange(new[] { "التاريخ", "الصنف", "الدفعة", "الوحدة", "العدد", "بالقطعة", "إلى مخزن", "المستخدم" });
        foreach (var p in rows)
            r.Rows.Add(new[] { p.PackingDate.ToLocalTime().ToString("yyyy/MM/dd HH:mm"), p.Item, p.Batch ?? "", p.Level, Q(p.UnitsPackaged), Q(p.Pieces), p.Warehouse, p.User });
        var total = rows.Sum(p => p.Pieces);
        r.Total("إجمالي المعبّأ", $"{Q(total)} قطعة", true).Total("المتبقي", $"{Q(Math.Max(0, o.QuantityToProduce - total))} قطعة");
        r.Signatures.AddRange(new[] { "مسؤول التعبئة", "أمين مخزن المنتج التام" });
        return r;
    }

    // ============================ المخازن والمندوبين ============================

    /// <summary>مستند مخزني مطبوع (من واجهة المخزن أو من شاشة مستندات المندوبين).</summary>
    public static ReportDocument StockDocumentReport(AppSession s, StockDocument d)
    {
        var r = new ReportDocument { Key = $"stock-doc-{d.DocumentType}", CompanyName = s.ProjectName, Title = $"مستند {ArabicLabels.Of(d.DocumentType)}", PrintedBy = s.FullName, Notes = d.Notes };
        r.Field("رقم المستند", d.DocumentNumber)
         .Field("التاريخ", d.DocumentDate.ToString("yyyy/MM/dd"))
         .Field(d.DocumentType is StockDocumentType.Transfer or StockDocumentType.RepLoad or StockDocumentType.RepReturn ? "من مخزن" : "المخزن", d.Warehouse.Name)
         .Field("إلى مخزن", d.CounterWarehouse?.Name)
         .Field(d.DocumentType switch
         {
             StockDocumentType.Receipt => "المصدر", StockDocumentType.Issue => "الجهة المستلمة",
             StockDocumentType.RepLoad or StockDocumentType.RepReturn => "المندوب", _ => "الجهة المستفيدة"
         }, d.RepEmployee?.FullName ?? d.PartyName)
         .Field("سبب التلف", d.DamageReason is null ? null : ArabicLabels.Of(d.DamageReason))
         .Field("المستخدم", d.CreatedByUser.Username);
        var isReturn = d.DocumentType == StockDocumentType.RepReturn;
        r.Columns.AddRange(isReturn
            ? new[] { "#", "الصنف", "الوحدة", "الكمية", "القطع", "الحالة", "التشغيلة" }
            : new[] { "#", "الصنف", "الوحدة", "الكمية", "القطع", "التشغيلة", "الصلاحية" });
        var i = 0;
        foreach (var l in d.Lines.OrderBy(l => l.Id))
        {
            var batch = l.Batch?.BatchNumber ?? (d.DocumentType == StockDocumentType.Receipt ? "—" : "تلقائي");
            r.Rows.Add(isReturn
                ? new[] { (++i).ToString(), $"{l.Item.ItemName} ({l.Item.ItemCode})", l.PackagingLevel.LevelName, $"{l.QuantityInLevel:N0}",
                          $"{l.QuantityBaseUnits:N0}", l.IsDamaged ? "تلف ميداني" : "سليم", batch }
                : new[] { (++i).ToString(), $"{l.Item.ItemName} ({l.Item.ItemCode})", l.PackagingLevel.LevelName, $"{l.QuantityInLevel:N0}",
                          $"{l.QuantityBaseUnits:N0}", batch, l.Batch?.ExpiryDate?.ToString("yyyy/MM/dd") ?? "" });
        }
        r.Total("عدد السطور", d.Lines.Count.ToString());
        if (isReturn)
            r.Total("سليم يعود للمخزن", $"{d.Lines.Where(l => !l.IsDamaged).Sum(l => l.QuantityBaseUnits):N0}")
             .Total("تلف ميداني", $"{d.Lines.Where(l => l.IsDamaged).Sum(l => l.QuantityBaseUnits):N0}");
        r.Total("إجمالي القطع", $"{d.Lines.Sum(l => l.QuantityBaseUnits):N0}", true);
        r.Signatures.AddRange(d.DocumentType switch
        {
            StockDocumentType.Receipt => new[] { "المسلِّم", "أمين المخزن", "المدير" },
            StockDocumentType.Transfer => new[] { "أمين المخزن المرسِل", "الناقل", "أمين المخزن المستلم" },
            StockDocumentType.RepLoad => new[] { "أمين المخزن", "المندوب المستلم", "المدير" },
            StockDocumentType.RepReturn => new[] { "المندوب المسلِّم", "أمين المخزن", "المدير" },
            _ => new[] { "المستلم", "أمين المخزن", "المدير" }
        });
        return r;
    }

    // ============================ المالية ============================

    public static async Task<ReportDocument?> VoucherAsync(AppSession s, ProjectDbContext db, int voucherId)
    {
        var v = await db.Vouchers.AsNoTracking().Include(x => x.CreatedByUser).FirstOrDefaultAsync(x => x.Id == voucherId);
        if (v is null) return null;
        var party = v.PartyId is null ? null : v.PartyType switch
        {
            VoucherPartyType.Customer => await db.Customers.Where(c => c.Id == v.PartyId).Select(c => c.Name).FirstOrDefaultAsync(),
            VoucherPartyType.Supplier => await db.Suppliers.Where(c => c.Id == v.PartyId).Select(c => c.Name).FirstOrDefaultAsync(),
            VoucherPartyType.Employee => await db.Employees.Where(c => c.Id == v.PartyId).Select(c => c.FullName).FirstOrDefaultAsync(),
            _ => null
        };
        var receipt = v.VoucherType == VoucherType.Receipt;
        var r = new ReportDocument
        {
            CompanyName = s.ProjectName, Title = receipt ? "سند قبض" : "سند صرف", Notes = v.Notes, PrintedBy = s.FullName,
            Key = "Voucher", ReceiptCapable = true
        };
        r.Field("رقم السند", v.VoucherNumber)
         .Field("التاريخ", v.VoucherDate.ToString("yyyy/MM/dd"))
         .Field(receipt ? "استلمنا من" : "صرفنا إلى", party ?? ArabicLabels.Of(v.PartyType))
         .Field("طريقة الدفع", ArabicLabels.Of(v.PaymentMethod))
         .Field("المستخدم", v.CreatedByUser.Username);
        r.Total("المبلغ", $"{N(v.Amount)} د.ع", true).Total("المبلغ كتابةً", ArabicNumberWords.Amount(v.Amount));
        r.Signatures.AddRange(receipt ? new[] { "المستلم (أمين الصندوق)", "الدافع" } : new[] { "المستلم", "أمين الصندوق", "المدير" });
        return r;
    }

    /// <summary>سند تأمين عميل (استلام/إرجاع/افتتاحي) مع رصيد التأمين بعده — يصلح للكاشير 80mm.</summary>
    public static async Task<ReportDocument?> CustomerDepositAsync(AppSession s, ProjectDbContext db, int depositId)
    {
        var svc = new CustomerDepositService(db);
        var d = await svc.GetAsync(depositId);
        if (d is null) return null;
        var balanceAfter = (await svc.GetHistoryAsync(d.CustomerId)).First(r => r.Id == d.Id).Balance;
        var title = d.Kind switch
        {
            CustomerDepositKind.Receipt => "سند استلام تأمين",
            CustomerDepositKind.Refund => "سند إرجاع تأمين",
            _ => "رصيد تأمين افتتاحي"
        };
        var r = new ReportDocument
        {
            CompanyName = s.ProjectName, Title = title, Stamp = d.IsVoided ? "ملغى" : null, Notes = d.Notes, PrintedBy = s.FullName,
            Key = "CustomerDeposit", ReceiptCapable = true
        };
        r.Field("رقم السند", d.DepositNumber)
         .Field("التاريخ", d.DepositDate.ToString("yyyy/MM/dd"))
         .Field(d.Kind == CustomerDepositKind.Refund ? "أرجعنا إلى" : "استلمنا من", d.Customer.Name)
         .Field("الغرض", d.Purpose)
         .Field("الستيكر / الوصفة الخاصة", d.CustomRecipe?.Name)
         .Field("المبلغ بالعملة الأصلية", d.CurrencyAmount is { } ca ? $"{ca:N2} {d.Currency}" : null)
         .Field("المستخدم", d.CreatedByUser.Username);
        r.Total("المبلغ", $"{N(d.Amount)} د.ع", true)
         .Total("المبلغ كتابةً", ArabicNumberWords.Amount(d.Amount))
         .Total("رصيد تأمين العميل بعد السند", $"{N(balanceAfter)} د.ع");
        if (d.IsVoided) r.Total("سبب الإلغاء", d.VoidReason ?? "");
        r.Signatures.AddRange(d.Kind == CustomerDepositKind.Refund ? new[] { "المستلم (العميل)", "أمين الصندوق", "المدير" }
                                                                    : new[] { "المستلم (أمين الصندوق)", "الدافع (العميل)" });
        return r;
    }

    public static async Task<ReportDocument?> JournalEntryAsync(AppSession s, ProjectDbContext db, int entryId)
    {
        var e = await db.JournalEntries.AsNoTracking().Include(x => x.CreatedByUser).Include(x => x.Lines).ThenInclude(l => l.Account)
            .FirstOrDefaultAsync(x => x.Id == entryId);
        if (e is null) return null;
        var r = New(s, "قيد يومية", e.IsPosted ? null : "غير مرحّل", e.Description);
        r.Field("رقم القيد", e.EntryNumber).Field("التاريخ", e.EntryDate.ToString("yyyy/MM/dd"))
         .Field("النوع", ArabicLabels.Of(e.EntryType)).Field("المستخدم", e.CreatedByUser.Username);
        r.Columns.AddRange(new[] { "رمز الحساب", "اسم الحساب", "البيان", "مدين", "دائن" });
        foreach (var l in e.Lines.OrderByDescending(l => l.Debit).ThenBy(l => l.Id))
            r.Rows.Add(new[] { l.Account.AccountCode, l.Account.AccountName, l.Description ?? "", l.Debit == 0 ? "" : N(l.Debit), l.Credit == 0 ? "" : N(l.Credit) });
        r.Total("مجموع المدين", N(e.Lines.Sum(l => l.Debit))).Total("مجموع الدائن", N(e.Lines.Sum(l => l.Credit)))
         .Total("التوازن", e.IsBalanced ? "متوازن ✓" : "غير متوازن", true);
        r.Signatures.AddRange(new[] { "المحاسب", "المدقق", "المدير المالي" });
        return r;
    }

    // ============================ المشتريات ============================

    public static async Task<ReportDocument?> PurchaseOrderAsync(AppSession s, ProjectDbContext db, int orderId)
    {
        var o = await db.PurchaseOrders.AsNoTracking().Include(x => x.Supplier).Include(x => x.Warehouse).Include(x => x.CreatedByUser)
            .Include(x => x.Lines).ThenInclude(l => l.Item).FirstOrDefaultAsync(x => x.Id == orderId);
        if (o is null) return null;
        var r = New(s, "أمر شراء", o.Status == PurchaseOrderStatus.Cancelled ? "ملغى" : null);
        r.Field("رقم الأمر", o.PONumber).Field("المورد", o.Supplier.Name).Field("التاريخ", o.OrderDate.ToString("yyyy/MM/dd"))
         .Field("التسليم المتوقع", o.ExpectedDeliveryDate?.ToString("yyyy/MM/dd")).Field("مخزن الاستلام", o.Warehouse.Name)
         .Field("شروط الدفع", ArabicLabels.Of(o.PaymentTerms)).Field("الحالة", ArabicLabels.Of(o.Status));
        r.Columns.AddRange(new[] { "#", "الصنف", "الكمية", "سعر الوحدة", "الإجمالي", "المستلم" });
        var i = 0;
        foreach (var l in o.Lines.OrderBy(l => l.Id))
            r.Rows.Add(new[] { (++i).ToString(), $"{l.Item.ItemName} ({l.Item.ItemCode})", Q(l.QuantityOrdered), N(l.ExpectedUnitCost),
                               N(l.QuantityOrdered * l.ExpectedUnitCost), Q(l.QuantityReceived) });
        var total = o.Lines.Sum(l => l.QuantityOrdered * l.ExpectedUnitCost);
        r.Total("الإجمالي", $"{N(total)} د.ع", true);
        if (o.AdvanceAmount > 0) r.Total("الدفعة المقدمة", $"{N(o.AdvanceAmount)} د.ع");
        r.Signatures.AddRange(new[] { "مسؤول المشتريات", "المدير" });
        return r;
    }

    public static async Task<ReportDocument?> GoodsReceiptAsync(AppSession s, ProjectDbContext db, int receiptId)
    {
        var g = await db.GoodsReceipts.AsNoTracking().Include(x => x.Supplier).Include(x => x.Warehouse).Include(x => x.PurchaseOrder).Include(x => x.CreatedByUser)
            .Include(x => x.Lines).ThenInclude(l => l.Item).Include(x => x.Lines).ThenInclude(l => l.Batch)
            .FirstOrDefaultAsync(x => x.Id == receiptId);
        if (g is null) return null;
        var r = New(s, "محضر استلام بضاعة");
        r.Field("رقم الاستلام", g.ReceiptNumber).Field("المورد", g.Supplier.Name).Field("التاريخ", g.ReceiptDate.ToString("yyyy/MM/dd"))
         .Field("أمر الشراء", g.PurchaseOrder?.PONumber).Field("فاتورة المورد", g.SupplierInvoiceNumber).Field("المخزن", g.Warehouse.Name)
         .Field("المستلم", g.CreatedByUser.Username);
        r.Columns.AddRange(new[] { "#", "الصنف", "التشغيلة", "الصلاحية", "الكمية", "التكلفة", "الإجمالي" });
        var i = 0;
        foreach (var l in g.Lines.OrderBy(l => l.Id))
            r.Rows.Add(new[] { (++i).ToString(), l.Item.ItemName, l.Batch.BatchNumber, l.Batch.ExpiryDate?.ToString("yyyy/MM/dd") ?? "",
                               Q(l.QuantityReceived), N(l.UnitCost), N(l.QuantityReceived * l.UnitCost) });
        r.Total("إجمالي الاستلام", $"{N(g.Lines.Sum(l => l.QuantityReceived * l.UnitCost))} د.ع", true);
        r.Signatures.AddRange(new[] { "المورد / السائق", "أمين المخزن", "مراقب الجودة" });
        return r;
    }

    // ============================ الرواتب ============================

    public static async Task<ReportDocument?> PayrollAsync(AppSession s, ProjectDbContext db, int runId)
    {
        var run = await db.PayrollRuns.AsNoTracking().Include(x => x.ApprovedByUser).Include(x => x.Lines).ThenInclude(l => l.Employee)
            .FirstOrDefaultAsync(x => x.Id == runId);
        if (run is null) return null;
        var r = New(s, $"كشف رواتب {run.PeriodMonth:00}/{run.PeriodYear}", run.Status == PayrollRunStatus.Draft ? "مسودة — غير معتمد" : null);
        r.Field("الشهر", $"{run.PeriodMonth:00}/{run.PeriodYear}").Field("الحالة", ArabicLabels.Of(run.Status)).Field("اعتمده", run.ApprovedByUser?.Username)
         .Field("عدد الموظفين", run.Lines.Count.ToString());
        r.Columns.AddRange(new[] { "#", "الموظف", "العملة", "الأساسي", "خصم الغياب", "مخصصات", "حوافز", "الصافي", "التوقيع" });
        var i = 0;
        foreach (var l in run.Lines.OrderBy(l => l.Employee.FullName))
            r.Rows.Add(new[] { (++i).ToString(), l.Employee.FullName, l.Currency, N(l.BaseSalary), N(l.AbsenceDeduction), N(l.Allowances),
                               N(l.MonthlyIncentiveAmount + l.RepIncentiveAmount + l.SalesManagerIncentiveAmount), N(l.NetSalary), "" });
        foreach (var g in run.Lines.GroupBy(l => l.Currency))
            r.Total($"صافي الرواتب ({g.Key})", N(g.Sum(l => l.NetSalary)), g.Key == "IQD");
        r.Signatures.AddRange(new[] { "مسؤول الموارد البشرية", "المحاسب", "المدير" });
        return r;
    }

    // ============================ المبيعات ============================

    public static async Task<ReportDocument?> SalesInvoiceAsync(AppSession s, ProjectDbContext db, int invoiceId)
    {
        var inv = await db.SalesInvoices.AsNoTracking().Include(x => x.Customer).Include(x => x.Warehouse)
            .Include(x => x.Lines).ThenInclude(l => l.Item).Include(x => x.Lines).ThenInclude(l => l.PackagingLevel)
            .FirstOrDefaultAsync(x => x.Id == invoiceId);
        if (inv is null) return null;
        var posted = inv.Status == DocumentStatus.Posted;
        var r = new ReportDocument
        {
            CompanyName = s.ProjectName, Title = inv.IsFreeSale ? "إذن صرف — بيع مجاني" : "فاتورة مبيعات", Stamp = posted ? null : "مسودة — غير مرحّلة",
            Notes = inv.Notes, PrintedBy = s.FullName, Key = "SalesInvoice", ReceiptCapable = true,
            ReceiptColumns = inv.IsFreeSale ? new[] { 1, 2, 3 } : new[] { 1, 3, 5, 6 }
        };
        r.Field("رقم الفاتورة", inv.InvoiceNumber).Field("التاريخ", inv.InvoiceDate.ToString("yyyy/MM/dd"))
         .Field(inv.IsFreeSale ? "الجهة المستفيدة" : "العميل", inv.IsFreeSale ? inv.FreeSaleRecipient : inv.Customer.Name)
         .Field("نوع العميل", inv.IsFreeSale ? null : ArabicLabels.Of(inv.Customer.CustomerType))
         .Field("طريقة الدفع", inv.IsFreeSale ? null : ArabicLabels.Of(inv.PaymentMethod)).Field("المخزن", inv.Warehouse.Name);
        r.Columns.AddRange(inv.IsFreeSale ? new[] { "#", "الصنف", "الوحدة", "الكمية", "القطع" }
                                          : new[] { "#", "الصنف", "الوحدة", "الكمية", "القطع", "السعر", "المبلغ" });
        var i = 0;
        foreach (var l in inv.Lines.OrderBy(l => l.Id))
        {
            var row = new List<string> { (++i).ToString(), $"{l.Item.ItemName} ({l.Item.ItemCode})", l.PackagingLevel.LevelName, Q(l.QuantityInLevel), Q(l.QuantityBaseUnits) };
            if (!inv.IsFreeSale) row.AddRange(new[] { N(l.UnitPrice), N(l.LineTotal) });
            r.Rows.Add(row);
        }
        r.Total("إجمالي القطع", Q(inv.Lines.Sum(l => l.QuantityBaseUnits)));
        if (!inv.IsFreeSale && posted)
        {
            r.Total("المجموع", $"{N(inv.SubTotal)} د.ع");
            if (inv.TaxAmount != 0) r.Total($"الضريبة ({inv.TaxRate:0.##}%)", $"{N(inv.TaxAmount)} د.ع");
            if (inv.LoadingSuppliesAmount != 0) r.Total("رسوم التحميل", $"{N(inv.LoadingSuppliesAmount)} د.ع");
            r.Total("الإجمالي", $"{N(inv.TotalAmount)} د.ع", true).Total("المدفوع", $"{N(inv.AmountPaidNow)} د.ع")
             .Total("المتبقي على العميل", $"{N(inv.TotalAmount - inv.AmountPaidNow)} د.ع", inv.TotalAmount > inv.AmountPaidNow)
             .Total("الإجمالي كتابةً", ArabicNumberWords.Amount(inv.TotalAmount));
        }
        else if (!inv.IsFreeSale)
            r.Total("المجموع (قبل الترحيل)", $"{N(inv.Lines.Sum(l => l.LineTotal))} د.ع", true);
        r.Signatures.AddRange(new[] { "المستلم", "أمين المخزن", "المحاسب" });
        return r;
    }
}
