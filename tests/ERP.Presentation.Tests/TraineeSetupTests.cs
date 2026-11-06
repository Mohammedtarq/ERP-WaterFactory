using ERP.Presentation.ViewModels.Shell;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>وضع المتدرب في معالج الإعداد: LocalDB، تثبيت جديد ببيانات تجريبية، وحساب متدرب معروف.</summary>
public class TraineeSetupTests
{
    [Fact]
    public void Trainee_mode_fills_a_local_demo_install()
    {
        var vm = new SetupViewModel(new RecordingNavigator(), new MemoryConfigStore(), null);
        Assert.Null(vm.TraineeInfo);
        vm.UseTraineeModeCommand.Execute(null);
        Assert.Equal((@"(localdb)\MSSQLLocalDB", true, true, true), (vm.Server, vm.UseWindowsAuth, vm.IsNewInstall, vm.DemoData));
        Assert.Equal(("ERP_Training_Control", "ERP_Training", "trainee"), (vm.ControlDatabase, vm.ProjectDatabase, vm.AdminUsername));
        Assert.Equal(SetupViewModel.TraineePassword, vm.AdminPassword);
        Assert.Equal(vm.AdminPassword, vm.AdminPasswordConfirm);
        Assert.Contains("trainee", vm.TraineeInfo);
    }
}
