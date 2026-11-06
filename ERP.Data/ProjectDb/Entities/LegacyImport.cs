namespace ERP.Data.ProjectDb.Entities;

/// <summary>عملية نقل من نظام سابق — مرة واحدة لكل نظام مصدر في المشروع (26_legacy_import.sql).</summary>
public class LegacyImport
{
    public int Id { get; set; }
    public string SourceSystem { get; set; } = "";
    public string SourceServer { get; set; } = "";
    public string SourceDatabase { get; set; } = "";
    public DateTime CutoverDate { get; set; }
    public string? Summary { get; set; }
    public int ImportedByUserId { get; set; }
    public User ImportedByUser { get; set; } = null!;
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>خريطة المعرّف القديم ← الجديد لكل سجل منقول (للتتبّع والمراجعة).</summary>
public class LegacyImportMapEntry
{
    public int Id { get; set; }
    public int ImportId { get; set; }
    public string EntityType { get; set; } = "";
    public string LegacyKey { get; set; } = "";
    public int NewId { get; set; }
}
