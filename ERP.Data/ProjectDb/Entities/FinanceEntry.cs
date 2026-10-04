namespace ERP.Data.ProjectDb.Entities;

/// <summary>تشغيلي: يدخل كلفة القنينة. غير تشغيلي (توسعة، مكائن): يُطرح من ربح الشهر فقط. إيراد آخر: من «الواردات».</summary>
public enum FinanceCategoryKind { Operating, NonOperating, OtherIncome }

/// <summary>نوع مصروف أو إيراد آخر (31_expenses_final_accounts.sql).</summary>
public class FinanceCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public FinanceCategoryKind Kind { get; set; } = FinanceCategoryKind.Operating;
    /// <summary>NULL = الحساب الافتراضي لنوعه (مصروفات عمومية / غير تشغيلية / إيرادات أخرى).</summary>
    public int? AccountId { get; set; }
    public ChartOfAccount? Account { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

/// <summary>مصروف أو إيراد آخر نقدي: من صندوق المستخدم تلقائيًا، وقيده في الخلفية. لا يُحذف بل يُلغى.</summary>
public class FinanceEntry
{
    public int Id { get; set; }
    public string EntryNumber { get; set; } = "";
    public DateTime EntryDate { get; set; } = DateTime.Today;
    public int CategoryId { get; set; }
    public FinanceCategory Category { get; set; } = null!;
    public decimal Amount { get; set; }
    public int? VehicleId { get; set; }
    public Vehicle? Vehicle { get; set; }
    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }
    public string? PartyName { get; set; }
    public string? ReceiptNumber { get; set; }
    public string? Notes { get; set; }
    public int? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
    public bool IsVoided { get; set; }
    public string? VoidReason { get; set; }
    public int? VoidedByUserId { get; set; }
    public DateTime? VoidedAt { get; set; }
    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>رأس المال التشغيلي المخصص، بتاريخ سريان: الساري في يوم = آخر صف تاريخه ≤ ذلك اليوم.</summary>
public class WorkingCapitalSetting
{
    public int Id { get; set; }
    public DateTime EffectiveFrom { get; set; } = DateTime.Today;
    public decimal Amount { get; set; }
    public string? Notes { get; set; }
    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
