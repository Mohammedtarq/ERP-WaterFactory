using ERP.Data.ControlDb;
using ERP.Data.ProjectDb;
using ERP.Data.Security;
using ERP.Data.Services;
using ERP.Data.Setup;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>
/// التثبيت ثم الدخول فورًا بنفس القيم — على قاعدة تحكم جديدة، وعلى قاعدة تحكم موجودة مسبقًا
/// (تثبيت ثانٍ لمشروع جديد بنفس اسم المدير)، وهي الحالة التي حجبت الدخول بعد التثبيت.
/// </summary>
public class InstallLoginTests : IAsyncLifetime
{
    private static string Master => Environment.GetEnvironmentVariable("ERP_TEST_MASTER_CONNECTION")
        ?? throw new InvalidOperationException("ERP_TEST_MASTER_CONNECTION غير معيّن");
    private readonly string _s = Guid.NewGuid().ToString("N")[..8];
    private readonly List<string> _dbs = new();

    private string Db(string name) { var n = $"ERP_{name}_{_s}"; _dbs.Add(n); return n; }
    private string ControlCs(string db) => new SqlConnectionStringBuilder(Master) { InitialCatalog = db }.ConnectionString;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var conn = new SqlConnection(Master);
        await conn.OpenAsync();
        foreach (var db in _dbs)
        {
            await using var cmd = new SqlCommand($"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task<bool> DatabaseExists(string name)
    {
        await using var conn = new SqlConnection(Master);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT DB_ID(@n)", conn);
        cmd.Parameters.AddWithValue("@n", name);
        return await cmd.ExecuteScalarAsync() is not DBNull and not null;
    }

    /// <summary>الدخول الكامل كما تفعله شاشة الدخول ثم فتح المشروع.</summary>
    private static async Task AssertCanLogin(string controlCs, string user, string password, string projectDb)
    {
        var auth = new AuthService(controlCs);
        var login = await auth.LoginAsync(user, password);
        Assert.True(login.Success, $"تعذّر دخول {user}: {login.ErrorMessage}");
        var project = Assert.Single(login.Projects, p => p.DatabaseName == projectDb);
        var (session, error) = await auth.OpenProjectAsync(project);
        Assert.True(session is not null, error);
        Assert.Equal("مدير عام", session!.RoleName);
    }

    [Theory]
    [InlineData("admin", "Basra@2026")]
    [InlineData("Ahmed.Ali", "كلمة سر عربية 2026")]
    [InlineData("مدير", "  spaces inside and around  ")]
    public async Task Fresh_install_then_immediate_login_with_the_same_values(string user, string password)
    {
        var control = Db("Ctl");
        var project = Db("Prj");
        var r = await new ProvisioningService().InstallAsync(new InstallRequest(ControlCs(control), "مصنع", project, "المدير", user, password, DemoData: true));
        Assert.True(r.Success, r.ErrorMessage);

        // السجلات الثلاثة موجودة وبنفس صيغة التجزئة التي تتحقق منها شاشة الدخول
        await using (var cdb = new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>().UseSqlServer(ControlCs(control)).Options))
        {
            var g = await cdb.GlobalUsers.SingleAsync(u => u.Username == user);
            Assert.True(PasswordHasher.Verify(password, g.PasswordHash));
            var access = await cdb.UserProjectAccesses.Include(a => a.Project).SingleAsync(a => a.GlobalUserId == g.Id);
            Assert.Equal(project, access.Project.DatabaseName);
            await using var db = new ProjectDbContext(new DbContextOptionsBuilder<ProjectDbContext>().UseSqlServer(r.ProjectConnectionString).Options);
            var local = await db.Users.SingleAsync(u => u.Id == access.LocalUserIdInProject);
            Assert.Equal(user, local.Username);
            Assert.True(PasswordHasher.Verify(password, local.PasswordHash));
        }

        await AssertCanLogin(ControlCs(control), user, password, project);
        Assert.False((await new AuthService(ControlCs(control)).LoginAsync(user, password + "x")).Success);
    }

    /// <summary>
    /// السبب الجذري: قاعدة التحكم فيها "admin" من تثبيت سابق. كان التثبيت الثاني ينجح بصمت ويربط
    /// المشروع الجديد بالحساب القديم بكلمة مروره القديمة، فتُرفض الكلمة الجديدة عند الدخول.
    /// </summary>
    [Fact]
    public async Task Second_install_into_existing_control_db_with_same_admin_name()
    {
        var control = ControlCs(Db("Ctl"));
        var first = Db("Basra");
        var second = Db("Basra_V2");
        Assert.True((await new ProvisioningService().InstallAsync(new InstallRequest(control, "البصرة", first, "المدير", "admin", "OldPass@1", true))).Success);

        // 1) كلمة مرور مختلفة دون طلب إعادة التعيين: رفض واضح قبل إنشاء أي شيء، والحساب القديم سليم
        var r = await new ProvisioningService().InstallAsync(new InstallRequest(control, "البصرة 2", second, "المدير", "admin", "NewPass@2", true));
        Assert.False(r.Success);
        Assert.Contains("موجود مسبقًا", r.ErrorMessage);
        Assert.False(await DatabaseExists(second));
        await AssertCanLogin(control, "admin", "OldPass@1", first);

        // 2) نفس كلمة المرور القديمة: يُربط المشروع الجديد بالحساب ويظهر المشروعان عند الدخول
        Assert.True((await new ProvisioningService().InstallAsync(new InstallRequest(control, "البصرة 2", second, "المدير", "ADMIN", "OldPass@1", true))).Success);
        await AssertCanLogin(control, "admin", "OldPass@1", first);
        await AssertCanLogin(control, "admin", "OldPass@1", second);

        // 3) إعادة تعيين صريحة من المعالج: الكلمة الجديدة تعمل فورًا على المشروعين، والقديمة لا
        var reset = await new ProvisioningService().InstallAsync(
            new InstallRequest(control, "البصرة 2", second, "المدير", "admin", "NewPass@2", true, ExistingAdmin: ExistingAdminPolicy.ResetPassword));
        Assert.True(reset.Success, reset.ErrorMessage);
        Assert.Contains(reset.Log, l => l.Contains("أُعيد تعيين كلمة مرور"));
        await AssertCanLogin(control, "admin", "NewPass@2", first);
        await AssertCanLogin(control, "admin", "NewPass@2", second);
        Assert.False((await new AuthService(control).LoginAsync("admin", "OldPass@1")).Success);
    }
}
