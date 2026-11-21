using ERP.Data.ControlDb;
using ERP.Data.ControlDb.Entities;
using ERP.Data.Services;
using ERP.Data.Setup;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// ملاحظة التجربة 16: حذف مشروع من شاشة «اختيار المشروع» — للمدير فقط، بكتابة اسمه للتأكيد،
/// وبعد نسخة احتياطية تلقائية تُحذف قاعدته نهائيًا ويختفي من القائمة. والسجل المكرر الذي يشير لقاعدة مشروع آخر
/// يُزال وحده دون المساس بالقاعدة.
/// </summary>
[Collection("app")]
public class ProjectDeletionTests
{
    private readonly AppFixture _f;
    public ProjectDeletionTests(AppFixture f) => _f = f;

    private ControlDbContext Control() => new(new DbContextOptionsBuilder<ControlDbContext>().UseSqlServer(_f.ControlConnection).Options);

    private async Task<bool> DatabaseExistsAsync(string name)
    {
        await using var conn = new SqlConnection(new SqlConnectionStringBuilder(_f.ControlConnection) { InitialCatalog = "master" }.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT DB_ID(@n)", conn);
        cmd.Parameters.AddWithValue("@n", name);
        return await cmd.ExecuteScalarAsync() is not (null or DBNull);
    }

    [Fact]
    public async Task Admin_deletes_a_trial_project_after_backup_by_typing_its_name()
    {
        var dbName = $"ERP_DelTest_{Guid.NewGuid():N}"[..28];
        const string projectName = "يونس تجريبي للحذف";
        var install = await new ProvisioningService().InstallAsync(new InstallRequest(
            _f.ControlConnection, projectName, dbName, "أحمد المدير", AppFixture.AdminUser, Guid.NewGuid().ToString("N"), false,
            ExistingAdminPolicy.LinkWithoutPassword));
        Assert.True(install.Success, install.ErrorMessage);
        Assert.True(await DatabaseExistsAsync(dbName));

        // سجل مكرر يشير لقاعدة المشروع الأساسي (مثل مشاريع التجربة المكررة)
        int duplicateId;
        await using (var c = Control())
        {
            var main = await c.Projects.FirstAsync(p => p.ProjectName == "مصنع المياه - البصرة");
            var admin = await c.GlobalUsers.FirstAsync(u => u.Username == AppFixture.AdminUser);
            var mainAccess = await c.UserProjectAccesses.FirstAsync(a => a.ProjectId == main.Id && a.GlobalUserId == admin.Id);
            var dup = new Project { ProjectName = "يونس مكرر", DatabaseName = main.DatabaseName, ServerAddress = main.ServerAddress };
            c.Projects.Add(dup);
            await c.SaveChangesAsync();
            c.UserProjectAccesses.Add(new UserProjectAccess { GlobalUserId = admin.Id, ProjectId = dup.Id, LocalUserIdInProject = mainAccess.LocalUserIdInProject });
            await c.SaveChangesAsync();
            duplicateId = dup.Id;
        }

        var dialogs = new RecordingDialogs();
        var nav = new RecordingNavigator();
        var login = new LoginViewModel(new AuthService(_f.ControlConnection), dialogs, nav) { Username = AppFixture.AdminUser };
        await login.LoginCommand.ExecuteAsync(AppFixture.AdminPassword);
        var picker = nav.ProjectSelection!;
        var target = picker.Projects.Single(p => p.ProjectName == projectName);

        // اسم خاطئ: لا يُحذف شيء
        picker.BeginDeleteCommand.Execute(target);
        Assert.True(picker.IsDeleting);
        Assert.Contains(dbName, picker.DeletePrompt);
        picker.DeleteConfirmText = "اسم آخر";
        await picker.DeleteCommand.ExecuteAsync(null);
        Assert.Contains("اكتب اسم المشروع", picker.ErrorMessage);
        Assert.True(await DatabaseExistsAsync(dbName));

        // الاسم الصحيح: نسخة احتياطية ثم حذف القاعدة والسجل
        picker.DeleteConfirmText = projectName;
        await picker.DeleteCommand.ExecuteAsync(null);
        Assert.Null(picker.ErrorMessage);
        Assert.False(picker.IsDeleting);
        Assert.DoesNotContain(picker.Projects, p => p.ProjectName == projectName);
        Assert.Contains(dialogs.Infos, i => i.Contains("حُذف المشروع") && i.Contains("before-delete"));
        Assert.False(await DatabaseExistsAsync(dbName));
        await using (var c = Control())
            Assert.False(await c.Projects.AnyAsync(p => p.DatabaseName == dbName));

        // المكرر: يُزال سجله وتبقى قاعدة المشروع الأساسي تعمل
        picker.BeginDeleteCommand.Execute(picker.Projects.Single(p => p.ProjectId == duplicateId));
        picker.DeleteConfirmText = "يونس مكرر";
        await picker.DeleteCommand.ExecuteAsync(null);
        Assert.Null(picker.ErrorMessage);
        Assert.Contains(dialogs.Infos, i => i.Contains("أُزيل المشروع «يونس مكرر»"));
        Assert.True(await DatabaseExistsAsync(new SqlConnectionStringBuilder(_f.ProjectConnection).InitialCatalog));
        await using (var c = Control())
            Assert.False(await c.Projects.AnyAsync(p => p.Id == duplicateId));

        // غير المدير لا يحذف، والمشروع الوحيد لا يُحذف
        var clerkNav = new RecordingNavigator();
        var clerkLogin = new LoginViewModel(new AuthService(_f.ControlConnection), new RecordingDialogs(), clerkNav) { Username = AppFixture.ClerkUser };
        await clerkLogin.LoginCommand.ExecuteAsync(AppFixture.ClerkPassword);
        var clerkPicker = clerkNav.ProjectSelection!;
        var only = clerkPicker.Projects.Single();
        clerkPicker.BeginDeleteCommand.Execute(only);
        clerkPicker.DeleteConfirmText = only.ProjectName;
        await clerkPicker.DeleteCommand.ExecuteAsync(null);
        Assert.Contains("المشروع الوحيد", clerkPicker.ErrorMessage);
        Assert.True(await DatabaseExistsAsync(new SqlConnectionStringBuilder(_f.ProjectConnection).InitialCatalog));
    }
}
