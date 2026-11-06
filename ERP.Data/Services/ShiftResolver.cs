using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>
/// شفت الموظف في يوم معيّن: شفته في البطاقة، إلا إن كان له استثناء مؤقت يغطي اليوم
/// (نقل لشفت آخر، و/أو وقت دخول خاص). يُحمَّل مرة لفترة ولمجموعة موظفين.
/// </summary>
public sealed class ShiftResolver
{
    private readonly List<EmployeeShiftOverride> _overrides;
    private ShiftResolver(List<EmployeeShiftOverride> overrides) => _overrides = overrides;

    public static readonly ShiftResolver None = new(new());

    public static async Task<ShiftResolver> LoadAsync(ProjectDbContext db, IReadOnlyCollection<int> employeeIds, DateTime from, DateTime to)
    {
        var (f, t) = (from.Date, to.Date);
        var list = await db.EmployeeShiftOverrides.AsNoTracking().Include(o => o.Shift)
            .Where(o => employeeIds.Contains(o.EmployeeId) && o.FromDate <= t && o.ToDate >= f).ToListAsync();
        return new ShiftResolver(list);
    }

    public EmployeeShiftOverride? OverrideFor(int employeeId, DateTime day) =>
        _overrides.Where(o => o.EmployeeId == employeeId && o.FromDate.Date <= day.Date && o.ToDate.Date >= day.Date)
                  .OrderByDescending(o => o.Id).FirstOrDefault();

    /// <summary>الشفت المطبَّق يوم <paramref name="day"/> (نسخة جديدة إن تغيّر وقت الدخول، فلا يُمسّ الشفت الأصلي).</summary>
    public Shift? For(int employeeId, Shift? own, DateTime day)
    {
        if (OverrideFor(employeeId, day) is not { } o) return own;
        var shift = o.Shift ?? own;
        if (shift is null || o.CheckInTime is not TimeSpan entry) return shift;
        return new Shift
        {
            Id = shift.Id, Name = $"{shift.Name} (دخول {entry:hh\\:mm})", CheckInTime = entry, CheckInGraceMinutes = shift.CheckInGraceMinutes,
            CheckOutTime = shift.CheckOutTime, CheckOutGraceMinutes = shift.CheckOutGraceMinutes,
            OvertimeStartsAfter = shift.OvertimeStartsAfter, IsEntryOnly = shift.IsEntryOnly
        };
    }
}
