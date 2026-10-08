namespace ERP.Data.ProjectDb.Entities;

/// <summary>هاتف مندوب مسجّل (38_rep_app.sql): المفتاح السري يعرّف الجهاز، والإيقاف يمنعه فورًا.</summary>
public class RepDevice
{
    public int Id { get; set; }
    public int RepEmployeeId { get; set; }
    public Employee RepEmployee { get; set; } = null!;
    public string DeviceName { get; set; } = "";
    public string DeviceKey { get; set; } = "";
    public bool IsActive { get; set; } = true;
    /// <summary>الترحيل التلقائي يجري بصلاحيات من سجّل الجهاز.</summary>
    public int RegisteredByUserId { get; set; }
    public User RegisteredByUser { get; set; } = null!;
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenAt { get; set; }
    public DateTime? DeactivatedAt { get; set; }
}

public enum RepRequestKind { CashSale, CreditSale, Free, Collection, NewCustomer, Expense, Return }

/// <summary>Posted = رُحّل، Pending = بانتظار الاعتماد، Rejected = مرفوض، Failed = تعذّر ترحيله (السبب في ErrorMessage).</summary>
public enum RepRequestStatus { Posted, Pending, Rejected, Failed }

/// <summary>حركة من هاتف المندوب برقم فريد (ClientId) فلا تتكرر عند إعادة الإرسال.</summary>
public class RepRequest
{
    public int Id { get; set; }
    public Guid ClientId { get; set; }
    public int DeviceId { get; set; }
    public RepDevice Device { get; set; } = null!;
    public int RepEmployeeId { get; set; }
    public Employee RepEmployee { get; set; } = null!;
    public RepRequestKind Kind { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public string Payload { get; set; } = "";
    public byte[]? Photo { get; set; }
    public RepRequestStatus Status { get; set; }
    public string Summary { get; set; } = "";
    public decimal Amount { get; set; }
    public string? Warning { get; set; }
    public string? ResultTable { get; set; }
    public int? ResultId { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>الآجل والمجاني «للاطلاع»: يُعلَّمان مراجَعين دون رفض.</summary>
    public bool NeedsReview { get; set; }
    public int? ReviewedByUserId { get; set; }
    public User? ReviewedByUser { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? RejectReason { get; set; }
}

/// <summary>إعدادات تطبيق المندوبين (سطر واحد).</summary>
public class RepAppSetting
{
    public int Id { get; set; } = 1;
    public bool AllowCreditOverLimit { get; set; } = true;
    public int CashAlertDays { get; set; } = 2;
}
