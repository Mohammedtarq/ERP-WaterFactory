namespace ERP.Data.Services;

/// <summary>الاسم العربي لكل نوع عملية في العقل المالي (قواعد الربط المحاسبي). الرمز الإنجليزي يبقى داخليًا فقط.</summary>
public static class RuleNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CashReceiptVoucher"] = "سند قبض نقدي من عميل",
        ["CashPaymentVoucher"] = "سند صرف نقدي (مصروف)",
        ["FinanceExpense"] = "مصروف تشغيلي من الصندوق",
        ["FinanceNonOperating"] = "مصروف غير تشغيلي (توسعة، مكائن)",
        ["FinanceOtherIncome"] = "إيراد آخر إلى الصندوق",
        ["TempWagesPayment"] = "صرف أجور العمال الوقتيين",
        ["SupplierPaymentVoucher"] = "دفعة نقدية لمورد",
        ["SupplierAdvancePayment"] = "دفعة مقدمة لمورد",
        ["GoodsReceiptOnAccount"] = "استلام بضاعة على الحساب",
        ["SupplierAdvanceOffset"] = "تسوية دفعة مقدمة لمورد",
        ["SalesInvoiceCash"] = "فاتورة مبيعات نقدية",
        ["SalesInvoiceCredit"] = "فاتورة مبيعات آجلة",
        ["SalesInvoiceElectronic"] = "فاتورة مبيعات بدفع إلكتروني",
        ["SalesInvoiceRepCash"] = "مبيعات نقدية للمندوب (كاش فان)",
        ["SalesTax"] = "ضريبة المبيعات",
        ["LoadingSuppliesCharge"] = "رسوم مستلزمات التحميل",
        ["PayrollAccrual"] = "استحقاق الرواتب",
        ["RepFieldExpense"] = "مصروف ميداني للمندوب",
        ["RepCashHandover"] = "تسليم نقد المندوب للصندوق",
        ["RepDebtCollection"] = "تحصيل المندوب لدين عميل",
        ["CashBoxDeposit"] = "إيداع في الصندوق",
        ["CashBoxWithdrawal"] = "سحب من الصندوق",
        ["CashBoxOpening"] = "رصيد افتتاحي للصندوق",
        ["CustomerDepositReceipt"] = "استلام تأمين عميل",
        ["CustomerDepositRefund"] = "إرجاع تأمين عميل",
        ["CustomerDepositOpening"] = "رصيد افتتاحي لتأمين عميل",
        ["EmployeeAdvancePayout"] = "صرف سلفة أو مسحوب لموظف",
        ["EmployeeAdvanceOpening"] = "رصيد افتتاحي لسلفة موظف",
        ["PartnerProfitShare"] = "توزيع أرباح المطابقة على الشركاء",
        ["PartnerWithdrawal"] = "سحب أرباح شريك",
        ["PartnerOpening"] = "رصيد افتتاحي لشريك",
        ["DamagedSaleCash"] = "بيع مواد تالفة نقدًا",
        ["CustomerOpeningBalance"] = "رصيد افتتاحي مدين لعميل",
        ["CustomerOpeningCredit"] = "رصيد افتتاحي دائن لعميل",
        ["SupplierOpeningBalance"] = "رصيد افتتاحي لمورد (ذمة)",
        ["SupplierOpeningAdvance"] = "رصيد افتتاحي لدفعة مقدمة لمورد",
    };

    /// <summary>الاسم العربي، أو الرمز نفسه لقاعدة أضافها المستخدم بلا اسم معروف.</summary>
    public static string Of(string? code) =>
        string.IsNullOrWhiteSpace(code) ? "" : Names.TryGetValue(code.Trim(), out var n) ? n : code.Trim();
}
