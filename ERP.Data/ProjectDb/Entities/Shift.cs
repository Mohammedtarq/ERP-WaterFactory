namespace ERP.Data.ProjectDb.Entities;

public class Shift
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public TimeSpan CheckInTime { get; set; }
    public int CheckInGraceMinutes { get; set; }
    public TimeSpan CheckOutTime { get; set; }
    public int CheckOutGraceMinutes { get; set; }
    public TimeSpan? OvertimeStartsAfter { get; set; }

    /// <summary>«دخول فقط» (المبيعات): بصمة الحضور تكفي ولا يُطلب خروج.</summary>
    public bool IsEntryOnly { get; set; }

    /// <summary>شفت يعبر منتصف الليل (مثل 16:00–02:00): بصمات ما بعد 12 تُحسب ليوم بداية الشفت.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool IsOvernight => CheckOutTime < CheckInTime;
}
