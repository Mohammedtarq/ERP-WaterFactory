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

/// <summary>تحديث واحد من الرحمة (بيانات أساسية): يُسجَّل بتغييراته ليمكن التراجع عنه.</summary>
public class LegacyUpdateRun
{
    public int Id { get; set; }
    public string SourceDatabase { get; set; } = "";
    public DateTime AppliedAt { get; set; } = DateTime.UtcNow;
    public int AppliedByUserId { get; set; }
    public string? Summary { get; set; }
    public string? PreBackupPath { get; set; }
    public DateTime? UndoneAt { get; set; }
    public int? UndoneByUserId { get; set; }
    public string? UndoReason { get; set; }
    public List<LegacyUpdateChange> Changes { get; set; } = new();
}

public class LegacyUpdateChange
{
    public int Id { get; set; }
    public int RunId { get; set; }
    public LegacyUpdateRun Run { get; set; } = null!;
    /// <summary>Customer / Supplier / ProductPrice.</summary>
    public string EntityType { get; set; } = "";
    public int EntityId { get; set; }
    public string? LegacyKey { get; set; }
    /// <summary>Created أو Updated.</summary>
    public string Action { get; set; } = "";
    public string? Field { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}
