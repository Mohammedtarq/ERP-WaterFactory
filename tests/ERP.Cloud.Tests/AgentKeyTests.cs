using ERP.Data.Services;
using Xunit;

namespace ERP.Cloud.Tests;

/// <summary>ملف إعداد «تجربة المندوبين» يقرأ مفتاح المعمل بنفسه (ERP.SyncAgent.exe --print-key) فلا يلصقه المستخدم يدويًا.</summary>
[Collection("cloud")]
public class AgentKeyTests : CloudTestBase
{
    [Fact]
    public async Task Lab_key_is_read_from_the_projects_once_generated()
    {
        // قبل التوليد: لا مفتاح
        Assert.Empty(await CloudSyncRunner.ReadAgentKeysAsync(ControlCs));

        string generated;
        await using (var db = NewDb()) generated = await new CloudSyncService(db).GenerateKeyAsync(AdminId);

        var keys = await CloudSyncRunner.ReadAgentKeysAsync(ControlCs);
        var only = Assert.Single(keys);
        Assert.Equal(generated, only.Key);
        Assert.Equal("سحابة", only.Project);

        // مفتاح جديد يحلّ محل القديم
        string second;
        await using (var db = NewDb()) second = await new CloudSyncService(db).GenerateKeyAsync(AdminId);
        Assert.NotEqual(generated, second);
        Assert.Equal(second, Assert.Single(await CloudSyncRunner.ReadAgentKeysAsync(ControlCs)).Key);
    }
}
