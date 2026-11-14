namespace ERP.Data.ProjectDb.Entities;

public class Department
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>الشفت الافتراضي للقسم: يأخذه موظفوه ما لم يكن لأحدهم شفت خاص (59_department_shift_annual_raise.sql).</summary>
    public int? DefaultShiftId { get; set; }
    public Shift? DefaultShift { get; set; }
    /// <summary>لم يعد مستعملًا: مبلغ العلاوة صار من درجة الموظف (60_raise_grades.sql) — يبقى محفوظًا بلا حذف.</summary>
    public decimal? AnnualRaiseAmount { get; set; }
}
