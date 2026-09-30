namespace ERP.Data.ProjectDb.Entities;

/// <summary>
/// سطر واحد في محفظة المندوب (عهدة نقدية). الرصيد الحالي = مجموع (AmountIn - AmountOut)
/// لكل موظف، بنفس مبدأ "السجل بدل العمود" المستخدم في المخزون.
/// </summary>
public class RepWalletTransaction
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public DateTime TransactionDate { get; set; } = DateTime.UtcNow;
    public string Description { get; set; } = string.Empty;
    public decimal AmountIn { get; set; }     // مبيعات نقدية + تحصيل ديون
    public decimal AmountOut { get; set; }    // مصروفات ميدانية + تسليم للخزينة

    public string? ReferenceTable { get; set; }
    public int? ReferenceId { get; set; }

    public int? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
}
