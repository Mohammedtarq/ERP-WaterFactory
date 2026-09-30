namespace ERP.Data.ProjectDb.Entities;

public enum JournalEntryType { Manual, AutoVoucher, AutoSales, AutoPurchase, AutoPayroll, AutoProduction }

public class JournalEntry
{
    public int Id { get; set; }
    public string EntryNumber { get; set; } = string.Empty;
    public DateTime EntryDate { get; set; }
    public JournalEntryType EntryType { get; set; }
    public string? Description { get; set; }

    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;

    public bool IsPosted { get; set; }
    public string? SourceTable { get; set; }
    public int? SourceId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<JournalEntryLine> Lines { get; set; } = new List<JournalEntryLine>();

    /// <summary>لا يُسمح بالترحيل إلا حين يتساوى إجمالي المدين مع إجمالي الدائن.</summary>
    public bool IsBalanced => Lines.Sum(l => l.Debit) == Lines.Sum(l => l.Credit);
}

public class JournalEntryLine
{
    public int Id { get; set; }

    public int JournalEntryId { get; set; }
    public JournalEntry JournalEntry { get; set; } = null!;

    public int AccountId { get; set; }
    public ChartOfAccount Account { get; set; } = null!;

    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string? Description { get; set; }
}
