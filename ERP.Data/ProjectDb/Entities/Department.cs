namespace ERP.Data.ProjectDb.Entities;

public class Department
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>الشفت الافتراضي للقسم: يأخذه موظفوه ما لم يكن لأحدهم شفت خاص (59_department_shift_annual_raise.sql).</summary>
    public int? DefaultShiftId { get; set; }
    public Shift? DefaultShift { get; set; }
    /// <summary>مبلغ العلاوة السنوية لموظفي القسم (يعلو عليه مبلغ الموظف إن حُدّد).</summary>
    public decimal? AnnualRaiseAmount { get; set; }
}
