namespace ERP.Data.ProjectDb.Entities;

/// <summary>صورة الموظف (45_photos.sql) — جدول منفصل فلا تُحمَّل مع كل قراءة للموظفين.</summary>
public class EmployeePhoto
{
    public int EmployeeId { get; set; }
    public byte[] Photo { get; set; } = Array.Empty<byte>();
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>صورة اليوزر الخاصة (اختيارية — بدونها يأخذ صورة الموظف المربوط به).</summary>
public class UserPhoto
{
    public int UserId { get; set; }
    public byte[] Photo { get; set; } = Array.Empty<byte>();
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
