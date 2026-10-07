using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;

namespace ERP.Presentation;

public record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>ترجمة كل القيم المخزّنة إنجليزيًا في قاعدة البيانات إلى نص عربي للعرض.</summary>
public static class ArabicLabels
{
    private static readonly Dictionary<Enum, string> Map = new()
    {
        [CustomerType.Agent] = "وكيل", [CustomerType.SubCustomer] = "عميل فرعي", [CustomerType.Direct] = "عميل مباشر",
        [InvoicePaymentMethod.Cash] = "نقدي", [InvoicePaymentMethod.Credit] = "آجل",
        [InvoicePaymentMethod.Partial] = "دفع جزئي", [InvoicePaymentMethod.Electronic] = "دفع إلكتروني",
        [DocumentStatus.Draft] = "مسودة", [DocumentStatus.Posted] = "مرحّلة", [DocumentStatus.Voided] = "ملغاة",
        [AccountType.Asset] = "أصول", [AccountType.Liability] = "خصوم", [AccountType.Equity] = "حقوق ملكية",
        [AccountType.Revenue] = "إيرادات", [AccountType.Expense] = "مصروفات",
        [JournalEntryType.Manual] = "يدوي", [JournalEntryType.AutoVoucher] = "سند تلقائي",
        [JournalEntryType.AutoSales] = "مبيعات تلقائي", [JournalEntryType.AutoPurchase] = "مشتريات تلقائي",
        [JournalEntryType.AutoPayroll] = "رواتب تلقائي", [JournalEntryType.AutoProduction] = "إنتاج تلقائي",
        [VoucherType.Receipt] = "سند قبض", [VoucherType.Payment] = "سند صرف",
        [VoucherPartyType.Customer] = "عميل", [VoucherPartyType.Supplier] = "مورد",
        [VoucherPartyType.Employee] = "موظف", [VoucherPartyType.Other] = "أخرى",
        [PaymentMethod.Cash] = "نقدًا", [PaymentMethod.Bank] = "تحويل بنكي", [PaymentMethod.Cheque] = "صك", [PaymentMethod.Return] = "مرتجع بضاعة",
        [WarehouseType.Main] = "رئيسي", [WarehouseType.Sub] = "فرعي", [WarehouseType.Returns] = "مرتجعات",
        [WarehouseType.Damaged] = "تالف", [WarehouseType.UnderInspection] = "تحت الفحص",
        [WarehouseType.Transit] = "بضاعة بالطريق", [WarehouseType.RepVan] = "كاش فان مندوب",
        [WarehouseType.RawMaterial] = "مواد أولية", [WarehouseType.FinishedGoods] = "منتج تام", [WarehouseType.WorkInProcess] = "تحت التصنيع",
        [SourcingMethod.Manufactured] = "تصنيع", [SourcingMethod.Purchased] = "شراء", [SourcingMethod.Both] = "تصنيع وشراء",
        [LocationLevelType.Zone] = "منطقة", [LocationLevelType.Shelf] = "رف", [LocationLevelType.Bin] = "موقع دقيق",
        [SupplierPaymentTerms.Cash] = "نقدي", [SupplierPaymentTerms.Credit] = "آجل",
        [SupplierPaymentTerms.AdvancePlusCredit] = "دفعة مقدمة + آجل",
        [PurchaseOrderStatus.Draft] = "مسودة", [PurchaseOrderStatus.Sent] = "مُرسل للمورد",
        [PurchaseOrderStatus.PartiallyReceived] = "مستلم جزئيًا", [PurchaseOrderStatus.Completed] = "مكتمل",
        [PurchaseOrderStatus.Cancelled] = "ملغى",
        [StockTransactionType.Receipt] = "استلام", [StockTransactionType.SalesIssue] = "صرف مبيعات",
        [StockTransactionType.ProductionConsume] = "استهلاك إنتاج", [StockTransactionType.ProductionOutput] = "ناتج إنتاج",
        [StockTransactionType.Packing] = "تعبئة", [StockTransactionType.Transfer] = "تحويل",
        [StockTransactionType.Damaged] = "تالف", [StockTransactionType.FreeIssue] = "صرف مجاني",
        [StockTransactionType.ReturnToWarehouse] = "إرجاع للمخزن", [StockTransactionType.RepLoad] = "تحميل مندوب",
        [StockTransactionType.RepSale] = "بيع مندوب", [StockTransactionType.RepFreeSale] = "بيع مجاني مندوب",
        [StockTransactionType.RepDamaged] = "تالف مندوب", [StockTransactionType.RepReturn] = "مرتجع مندوب",
        [StockTransactionType.SyncConflictAdjustment] = "تسوية تعارض مزامنة",
        [DamageReason.Transit] = "أثناء النقل", [DamageReason.Warehouse] = "داخل المخزن", [DamageReason.Production] = "أثناء الإنتاج",
        [AttendanceStatus.Present] = "حاضر", [AttendanceStatus.Late] = "متأخر", [AttendanceStatus.Absent] = "غائب",
        [AttendanceStatus.ApprovedLeave] = "إجازة معتمدة",
        [PromotionMovementType.Promotion] = "ترقية", [PromotionMovementType.AnnualRaise] = "علاوة سنوية",
        [PromotionMovementType.AnnualBonus] = "مكافأة سنوية",
        [PromotionApplicationType.PermanentAddition] = "إضافة دائمة للراتب", [PromotionApplicationType.OneTime] = "لمرة واحدة",
        [PayrollRunStatus.Draft] = "مسودة", [PayrollRunStatus.Approved] = "معتمدة",
        [ProductionOrderStatus.Draft] = "مسودة", [ProductionOrderStatus.InProgress] = "قيد التشغيل",
        [ProductionOrderStatus.Completed] = "مكتمل", [ProductionOrderStatus.Cancelled] = "ملغى",
        [QCOverallResult.Passed] = "ناجحة", [QCOverallResult.Rejected] = "مرفوضة",
        [QCLineResult.Pass] = "ناجح", [QCLineResult.Fail] = "راسب",
        [SyncConflictStatus.Pending] = "بانتظار التسوية", [SyncConflictStatus.Resolved] = "مسوّى",
        [StockAdjustmentKind.Damaged] = "تالف", [StockAdjustmentKind.Disposal] = "إتلاف", [StockAdjustmentKind.Return] = "إرجاع للمخزن",
        [StockTransactionType.Issue] = "إخراج مخزني",
        [StockTransactionType.WipIssue] = "صرف لأمر إنتاج", [StockTransactionType.WipReturn] = "إرجاع من تحت التصنيع",
        [StockTransactionType.WipAdjust] = "تعديل مشرف (تحت التصنيع)",
        [StockTransactionType.SalesVoid] = "إلغاء فاتورة (إرجاع للمخزن)", [StockTransactionType.StocktakeVariance] = "فرق جرد",
        [StockTransactionType.CustomerReturn] = "مرتجع زبون",
        [WipAdjustmentKind.Remaining] = "المتبقي", [WipAdjustmentKind.Damaged] = "التالف",
        [StockDocumentType.Receipt] = "إدخال مخزني", [StockDocumentType.Issue] = "إخراج مخزني",
        [StockDocumentType.Transfer] = "مناقلة إلى مخزن آخر", [StockDocumentType.Damaged] = "تالف",
        [StockDocumentType.FreeIssue] = "مسحوب مجاني",
        [StockDocumentType.RepLoad] = "إسناد حمولة لمندوب", [StockDocumentType.RepReturn] = "إرجاع من مندوب",
        [DamageReason.Field] = "تلف ميداني",
        [BeneficiaryCategory.Government] = "جهة حكومية", [BeneficiaryCategory.Drivers] = "سائقون",
        [BeneficiaryCategory.Partners] = "شركاء وإدارة", [BeneficiaryCategory.Staff] = "موظفون", [BeneficiaryCategory.Reps] = "مندوبون",
        [BeneficiaryCategory.Other] = "أخرى",
        [CashBoxType.Main] = "صندوق رئيسي", [CashBoxType.User] = "صندوق مستخدم", [CashBoxType.Bank] = "بنك / دفع إلكتروني", [CashBoxType.Home] = "صندوق المنزل (الفائض)", [CashBoxType.Cards] = "بطاقات إلكترونية",
        [CashBoxTxType.Opening] = "رصيد افتتاحي", [CashBoxTxType.Deposit] = "إيداع", [CashBoxTxType.Withdrawal] = "سحب",
        [CashBoxTxType.TransferIn] = "مناقلة واردة", [CashBoxTxType.TransferOut] = "مناقلة صادرة",
        [CashBoxTxType.SalesReceipt] = "مبيعات نقدية", [CashBoxTxType.VoucherReceipt] = "سند قبض",
        [CashBoxTxType.VoucherPayment] = "سند صرف", [CashBoxTxType.RepHandover] = "تسليم نقد مندوب",
        [CashBoxTxType.CustomerDepositIn] = "استلام تأمين عميل", [CashBoxTxType.CustomerDepositOut] = "إرجاع تأمين عميل",
        [CashBoxTxType.EmployeeAdvance] = "سلفة / مسحوب موظف", [CashBoxTxType.PartnerWithdrawal] = "سحب أرباح شريك",
        [CashBoxTxType.Expense] = "مصروف", [CashBoxTxType.OtherIncome] = "إيراد آخر", [CashBoxTxType.TempWages] = "أجور عمال وقتيين",
        [FinanceCategoryKind.Operating] = "تشغيلي (يدخل كلفة القنينة)", [FinanceCategoryKind.NonOperating] = "غير تشغيلي (توسعة، مكائن)",
        [FinanceCategoryKind.OtherIncome] = "إيراد آخر",
        [PartnerTxKind.ProfitShare] = "حصة أرباح", [PartnerTxKind.Withdrawal] = "سحب أرباح", [PartnerTxKind.Opening] = "رصيد افتتاحي",
        [FinishedGoodsValuation.Cost] = "بسعر الكلفة", [FinishedGoodsValuation.SalePrice] = "بسعر البيع",
        [EmployeeDeductionKind.Loan] = "سلفة", [EmployeeDeductionKind.Withdrawal] = "مسحوب", [EmployeeDeductionKind.Penalty] = "عقوبة",
        [CustomerDepositKind.Receipt] = "استلام تأمين", [CustomerDepositKind.Refund] = "إرجاع تأمين", [CustomerDepositKind.Opening] = "رصيد افتتاحي",
    };

    /// <summary>القيم النصية كما تخرج من Views قاعدة البيانات (Status/CustomerType/PaymentMethod/TxType).</summary>
    private static readonly Dictionary<string, string> TextMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SalesInvoice"] = "فاتورة مبيعات", ["OpeningBalance"] = "رصيد افتتاحي", ["PaidAtSale"] = "مدفوع عند البيع",
        ["ReceiptVoucher"] = "سند قبض", ["PaymentVoucher"] = "سند صرف",
    };

    public static string Of(object? value) => value switch
    {
        null => "",
        Enum e => Map.TryGetValue(e, out var s) ? s : e.ToString(),
        string text => TextMap.TryGetValue(text, out var t) ? t : FromEnumName(text),
        bool b => b ? "نعم" : "لا",
        _ => value.ToString() ?? ""
    };

    private static string FromEnumName(string text)
    {
        foreach (var (key, label) in Map)
            if (key.ToString().Equals(text, StringComparison.OrdinalIgnoreCase)) return label;
        return text;
    }

    public static IReadOnlyList<Option<T>> OptionsOf<T>() where T : struct, Enum
        => Enum.GetValues<T>().Select(v => new Option<T>(v, Of(v))).ToList();
}
