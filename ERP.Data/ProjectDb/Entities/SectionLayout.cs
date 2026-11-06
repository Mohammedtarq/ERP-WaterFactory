namespace ERP.Data.ProjectDb.Entities;

/// <summary>قسم منقول بقرار الإدارة إلى وحدة أخرى (36_section_layout.sql). المفتاح = اسم نوع الشاشة.</summary>
public class SectionPlacement
{
    public string SectionKey { get; set; } = "";
    public string TargetModule { get; set; } = "";
    public int ChangedByUserId { get; set; }
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>قسم مخفي عن دور بعينه.</summary>
public class RoleHiddenSection
{
    public int RoleId { get; set; }
    public Role Role { get; set; } = null!;
    public string SectionKey { get; set; } = "";
}
