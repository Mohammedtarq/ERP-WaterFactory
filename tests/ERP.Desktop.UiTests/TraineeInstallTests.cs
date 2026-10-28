using ERP.Data.Services;
using ERP.Presentation.ViewModels.Shell;
using Xunit;
using Xunit.Abstractions;

namespace ERP.Desktop.UiTests;

/// <summary>جهاز المتدرب على LocalDB الحقيقي (Windows): المعالج يثبّت قاعدة تدريب ببيانات تجريبية، ويدخل المتدرب بحسابه.</summary>
[Collection("ui")]
public class TraineeInstallTests
{
    private readonly ITestOutputHelper _out;
    public TraineeInstallTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Trainee_setup_installs_a_demo_project_on_localdb_and_trainee_can_log_in()
    {
        if (Environment.GetEnvironmentVariable("ERP_UI_SQL_CONNECTION") is null) { _out.WriteLine("ERP_UI_SQL_CONNECTION غير معيّن — تخطي"); return; }
        var nav = new CapturingNavigator();
        var config = new MemoryConfig();
        var vm = new SetupViewModel(nav, config, null);
        vm.UseTraineeMode();
        await vm.FinishCommand.ExecuteAsync();
        Assert.True(vm.ErrorMessage is null, vm.ErrorMessage);
        Assert.NotNull(nav.UsedControlConnection);
        Assert.Equal(nav.UsedControlConnection, config.Value);
        Assert.Contains("MSSQLLocalDB", config.Value);

        var auth = new AuthService(config.Value!);
        var login = await auth.LoginAsync(SetupViewModel.TraineeUsername, SetupViewModel.TraineePassword);
        Assert.True(login.Success, login.ErrorMessage);
        var project = Assert.Single(login.Projects);
        var (session, openError) = await auth.OpenProjectAsync(project);
        Assert.True(session is not null, openError);
    }
}
