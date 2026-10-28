namespace ERP.Data.ProjectDb.Entities;

/// <summary>ما يحتاج موافقة القسم المالي قبل خروج النقد من الصندوق.</summary>
public enum PaymentRequestKind { EmployeeAdvance, EmployeeWithdrawal, PayrollPayout, PartnerWithdrawal }

public enum PaymentRequestStatus { Pending, Approved, Rejected }

/// <summary>
/// طلب صرف ينتظر موافقة القسم المالي (65_payment_requests.sql): سلفة أو مسحوب موظف، أو صرف رواتب شهر معتمد من الصندوق،
/// أو سحب أرباح شريك. لا يخرج نقد ولا يُقيَّد شيء قبل الاعتماد؛ وعند الاعتماد يُنفَّذ الصرف بالبيانات المحفوظة في الطلب.
/// </summary>
public class PaymentRequest
{
    public int Id { get; set; }
    public string RequestNumber { get; set; } = "";
    public PaymentRequestKind Kind { get; set; }
    public PaymentRequestStatus Status { get; set; } = PaymentRequestStatus.Pending;
    public decimal Amount { get; set; }
    /// <summary>ملخص للعرض: الجهة والمبلغ والشهر.</summary>
    public string Summary { get; set; } = "";
    /// <summary>بيانات الصرف الأصلية (JSON) لتنفيذها عند الاعتماد.</summary>
    public string Payload { get; set; } = "";
    public int? CashBoxId { get; set; }
    public int RequestedByUserId { get; set; }
    public User RequestedByUser { get; set; } = null!;
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public int? DecidedByUserId { get; set; }
    public User? DecidedByUser { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? RejectReason { get; set; }
    public string? ResultTable { get; set; }
    public int? ResultId { get; set; }
}
