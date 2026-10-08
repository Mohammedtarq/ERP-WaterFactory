namespace ERP.Data.ProjectDb.Entities;

/// <summary>سجل عملية استيراد ملف بصمة.</summary>
public class FingerprintImport
{
    public int Id { get; set; }
    public string FileName { get; set; } = "";
    public DateTime PeriodFrom { get; set; }
    public DateTime PeriodTo { get; set; }
    public int Punches { get; set; }
    public int DaysApplied { get; set; }
    public string? UnmappedCodes { get; set; }
    public int ImportedByUserId { get; set; }
    public User ImportedByUser { get; set; } = null!;
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
}
