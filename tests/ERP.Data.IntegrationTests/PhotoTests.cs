using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>
/// صور الموظفين والمستخدمين: PNG/JPG حتى 2 ميغابايت، اليوزر بلا صورة خاصة يأخذ صورة موظفه، الإزالة تعيده لها،
/// حذف الموظف يحذف صورته، وكل تغيير في سجل التدقيق.
/// </summary>
[Collection("provisioned")]
public class PhotoTests
{
    private readonly ProvisionedFixture _f;
    public PhotoTests(ProvisionedFixture f) => _f = f;

    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };

    [Fact]
    public async Task Employee_and_user_photos_with_fallback_validation_cascade_and_audit()
    {
        Assert.True(_f.Install.Success, _f.Install.ErrorMessage);
        await using var db = _f.NewDb();
        var admin = _f.AdminLocalId;
        var photos = new PhotoService(db);

        var emp = new Employee { FullName = "موظف اختبار الصور", BaseSalary = 600_000 };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        var role = await db.Roles.Select(r => r.Id).FirstAsync();
        var user = new User { Username = "photo_user_test", PasswordHash = "x", RoleId = role, EmployeeId = emp.Id };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // التحقق: ليست صورة، أو أكبر من 2 ميغابايت
        Assert.Contains("PNG أو JPG", (await photos.SetEmployeeAsync(emp.Id, new byte[] { 1, 2, 3, 4, 5 }, admin)).ErrorMessage);
        var big = new byte[PhotoService.MaxBytes + 1];
        Png.CopyTo(big, 0);
        Assert.Contains("2 ميغابايت", (await photos.SetEmployeeAsync(emp.Id, big, admin)).ErrorMessage);
        Assert.Null(await photos.GetEmployeeAsync(emp.Id));

        // صورة الموظف ← اليوزر المربوط يأخذها ما دام بلا صورة خاصة
        Assert.True((await photos.SetEmployeeAsync(emp.Id, Png, admin)).Success);
        Assert.Equal(Png, await photos.GetEmployeeAsync(emp.Id));
        Assert.Equal(Png, await photos.GetUserDisplayAsync(user.Id));
        Assert.False(await photos.HasOwnUserPhotoAsync(user.Id));

        // صورة خاصة لليوزر يغيّرها بنفسه، تعلو على صورة الموظف، وإزالتها تعيده لصورة الموظف
        Assert.True((await photos.SetUserAsync(user.Id, Jpeg, user.Id)).Success);
        Assert.Equal(Jpeg, await photos.GetUserDisplayAsync(user.Id));
        Assert.True(await photos.HasOwnUserPhotoAsync(user.Id));
        Assert.True((await photos.SetUserAsync(user.Id, null, user.Id)).Success);
        Assert.Equal(Png, await photos.GetUserDisplayAsync(user.Id));
        // إزالة ما لا يوجد لا تُسجَّل
        Assert.True((await photos.SetUserAsync(user.Id, null, user.Id)).Success);

        // تبديل صورة الموظف ثم إزالتها
        Assert.True((await photos.SetEmployeeAsync(emp.Id, Jpeg, admin)).Success);
        Assert.Equal(Jpeg, (await photos.AllEmployeesAsync())[emp.Id]);
        Assert.True((await photos.SetEmployeeAsync(emp.Id, null, admin)).Success);
        Assert.Null(await photos.GetUserDisplayAsync(user.Id));

        var audit = await db.AuditLogs.AsNoTracking().Where(a => (a.TableName == "EmployeePhotos" && a.RecordId == emp.Id.ToString())
                                                              || (a.TableName == "UserPhotos" && a.RecordId == user.Id.ToString()))
            .Where(a => a.Summary != null).OrderBy(a => a.Id).Select(a => a.Summary).ToListAsync();   // سطور الالتقاط التلقائي بلا ملخص تُستبعد
        Assert.Equal(new[]
        {
            "تغيير صورة الموظف موظف اختبار الصور", "تغيير صورته", "إزالة صورته",
            "تغيير صورة الموظف موظف اختبار الصور", "إزالة صورة الموظف موظف اختبار الصور"
        }, audit);
        Assert.Equal("صور الموظفين", AuditService.TableTitle("EmployeePhotos"));

        // حذف موظف (بلا حركات) يحذف صورته معه — لا سطور يتيمة
        var temp = new Employee { FullName = "موظف يُحذف مع صورته", BaseSalary = 1 };
        db.Employees.Add(temp);
        await db.SaveChangesAsync();
        Assert.True((await photos.SetEmployeeAsync(temp.Id, Png, admin)).Success);
        db.ChangeTracker.Clear();
        db.Employees.Remove(new Employee { Id = temp.Id });
        await db.SaveChangesAsync();
        Assert.False(await db.EmployeePhotos.AnyAsync(p => p.EmployeeId == temp.Id));

        Assert.Equal("س م", PhotoService.Initials("سارة المحاسبة"));
        Assert.Equal("ع", PhotoService.Initials(" علي "));
        Assert.Equal("ع م", PhotoService.Initials("علي المندوب"));
        Assert.Equal("؟", PhotoService.Initials(null));
    }
}
