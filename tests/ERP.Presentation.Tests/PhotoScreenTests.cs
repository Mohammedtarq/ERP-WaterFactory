using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.ViewModels.HR;
using ERP.Presentation.ViewModels.Settings;
using ERP.Presentation.ViewModels.Shell;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// الصور من الشاشات: صورة الموظف في نموذجه وقائمته وبطاقته المطبوعة وإشعار إجازته، صورة اليوزر في شاشة المستخدمين
/// (بدونها صورة موظفه)، وكل مستخدم يغيّر صورته بنفسه أسفل القائمة.
/// </summary>
[Collection("app")]
public class PhotoScreenTests
{
    private readonly AppFixture _f;
    public PhotoScreenTests(AppFixture f) => _f = f;

    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };

    private static string TempFile(byte[] bytes, string ext)
    {
        var path = Path.Combine(Path.GetTempPath(), $"erp-photo-{Guid.NewGuid():N}{ext}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task Employee_photo_card_and_user_photo_from_the_screens_and_the_sidebar()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        void NoErrors(string step) => Assert.True(dialogs.Errors.Count == 0, $"{step}: " + string.Join(" | ", dialogs.Errors));
        var notImage = TempFile(new byte[] { 1, 2, 3, 4, 5, 6 }, ".png");
        var png = TempFile(Png, ".png");
        var jpeg = TempFile(Jpeg, ".jpg");

        // ---------- الموظف: صورة في النموذج تُحفظ مع «حفظ» ----------
        var hr = shell.Open<HrModuleViewModel>(ModuleCode.HR);
        var emps = hr.Section<EmployeesSectionViewModel>();
        hr.SelectedTab = emps;
        await hr.LastActivation;
        await emps.IdleAsync();
        await emps.NewCommand.ExecuteAsync();
        emps.Editor!.FullName = "موظفة الصورة";
        emps.Editor.BaseSalary = 500_000;
        Assert.Equal("م ا", emps.EditorInitials);
        dialogs.ImageToPick = notImage;
        emps.ChoosePhotoCommand.Execute(null);
        Assert.Contains(dialogs.Errors, e => e.Contains("PNG أو JPG"));
        Assert.False(emps.HasEditorPhoto);
        dialogs.Errors.Clear();
        dialogs.ImageToPick = png;
        emps.ChoosePhotoCommand.Execute(null);
        Assert.True(emps.HasEditorPhoto);
        await emps.SaveCommand.ExecuteAsync();
        NoErrors("حفظ الموظف بصورته");
        var saved = emps.Items.Single(e => e.FullName == "موظفة الصورة");
        Assert.Equal(Png, saved.PhotoPreview);

        // فتح التعديل يُظهر الصورة، والإلغاء لا يمسّها
        await emps.EditCommand.ExecuteAsync(saved);
        Assert.Equal(Png, emps.EditorPhoto);
        emps.RemovePhotoCommand.Execute(null);
        Assert.False(emps.HasEditorPhoto);
        emps.CancelCommand.Execute(null);
        await using (var db = _f.NewDb())
            Assert.Equal(Png, await new PhotoService(db).GetEmployeeAsync(saved.Id));

        // ---------- بطاقة الموظف المطبوعة بصورته ----------
        await emps.PrintCardCommand.ExecuteAsync(saved);
        NoErrors("بطاقة الموظف");
        var card = Assert.Single(dialogs.Reports, r => r.Title == "بطاقة موظف — موظفة الصورة");
        Assert.Equal(Png, card.Photo);
        Assert.Contains(card.HeaderFields, f => f.Label == "الراتب الأساسي" && f.Value.StartsWith("500,000"));
        Assert.Equal(Png, card.WithVisibility(new HashSet<int>(), false).Photo);

        // ---------- كشف إجازات الموظف يحمل صورته ----------
        var leaves = hr.Leaves;
        hr.SelectedTab = leaves;
        await hr.LastActivation;
        await leaves.IdleAsync();
        leaves.Employee = leaves.Employees.Single(e => e.Id == saved.Id);
        await leaves.PrintBalanceCommand.ExecuteAsync();
        NoErrors("كشف الإجازات");
        Assert.Equal(Png, dialogs.Reports.Last().Photo);

        // ---------- المستخدمون: بلا صورة خاصة يأخذ صورة موظفه ----------
        var settings = shell.Open<SettingsModuleViewModel>(ModuleCode.SystemSettings);
        var users = settings.Section<UsersSectionViewModel>();
        settings.SelectedTab = users;
        await settings.LastActivation;
        await users.IdleAsync();
        await users.NewCommand.ExecuteAsync();
        users.Editor!.Username = "photo_screen_user";
        users.Editor.EmployeeId = saved.Id;
        users.NewPassword = "Photo@2026";
        await users.SaveCommand.ExecuteAsync();
        NoErrors("يوزر مربوط بالموظفة");
        var linked = users.Items.Single(u => u.Username == "photo_screen_user");
        Assert.Equal(Png, linked.PhotoPreview);
        await users.EditCommand.ExecuteAsync(linked);
        Assert.False(users.HasEditorPhoto);
        Assert.Equal(Png, users.EditorShownPhoto);
        Assert.Equal("يستعمل صورة الموظف المربوط به", users.PhotoHint);
        dialogs.ImageToPick = jpeg;
        users.ChoosePhotoCommand.Execute(null);
        Assert.Equal("صورة خاصة بالمستخدم", users.PhotoHint);
        await users.SaveCommand.ExecuteAsync();
        NoErrors("صورة اليوزر الخاصة");
        Assert.Equal(Jpeg, users.Items.Single(u => u.Username == "photo_screen_user").PhotoPreview);

        // ---------- أسفل القائمة: المستخدم يغيّر صورته بنفسه ويزيلها ----------
        await shell.IdleAllAsync();
        Assert.False(shell.HasOwnPhoto);
        dialogs.ImageToPick = png;
        await shell.ChangeMyPhotoCommand.ExecuteAsync();
        NoErrors("تغيير صورتي");
        Assert.True(shell.HasOwnPhoto);
        Assert.Equal(Png, shell.MyPhoto);
        dialogs.ImageToPick = notImage;
        await shell.ChangeMyPhotoCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("PNG أو JPG"));
        dialogs.Errors.Clear();
        Assert.Equal(Png, shell.MyPhoto);
        await shell.RemoveMyPhotoCommand.ExecuteAsync();
        NoErrors("إزالة صورتي");
        Assert.False(shell.HasOwnPhoto);

        foreach (var f in new[] { notImage, png, jpeg }) File.Delete(f);
        await shell.IdleAllAsync();
        Assert.Empty(_f.Unhandled);
    }
}
