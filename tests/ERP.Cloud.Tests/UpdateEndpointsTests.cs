using System.Net;
using Xunit;

namespace ERP.Cloud.Tests;

/// <summary>الخادم المحلي يقدّم ملفات تحديث البرنامج من مجلد النشر فقط، دون خروج منه.</summary>
[Collection("cloud")]
public class UpdateEndpointsTests : CloudTestBase
{
    [Fact]
    public async Task Serves_published_files_and_nothing_outside_the_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "erp-upd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "files", "الشعار"));
        File.WriteAllText(Path.Combine(root, "version.json"), """{"version":"1.0.5.0","mandatory":false}""");
        File.WriteAllText(Path.Combine(root, "files", "الشعار", "a.txt"), "مرحبا");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(root)!, "secret-" + Path.GetFileName(root) + ".txt"), "سري");
        try
        {
            Environment.SetEnvironmentVariable("Relay__UpdatesFolder", root);
            using var factory = Cloud("k-upd");
            using var http = factory.CreateClient();
            Assert.Contains("1.0.5.0", await http.GetStringAsync("/update/version.json"));
            Assert.Equal("مرحبا", await http.GetStringAsync("/update/files/" + Uri.EscapeDataString("الشعار") + "/a.txt"));
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/update/nothing.txt")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/update/%2e%2e/secret-" + Path.GetFileName(root) + ".txt")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/update/")).StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Relay__UpdatesFolder", null);
            Directory.Delete(root, true);
        }
    }
}
