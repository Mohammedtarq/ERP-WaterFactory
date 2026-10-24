using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ERP.Cloud.Contracts;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Cloud.Tests;

/// <summary>
/// لوحة المدير على الإنترنت من طرف إلى طرف: حاسبة المعمل ترفع الملخص والحساب، والمدير يدخل ويقرأ فقط،
/// والدخول الخاطئ يُقفل، وحذف الحساب في المعمل يُبطل جلسته، ولا شيء من الرواتب يصل السحابة.
/// </summary>
[Collection("cloud")]
public class ManagerDashboardTests : CloudTestBase
{
    private const string BaseUrl = "http://localhost/";

    private static async Task<string> LoginAsync(HttpClient http, string user, string password)
    {
        var r = await http.PostAsJsonAsync("/" + ManagerRoutes.Login, new ManagerLoginRequest(user, password));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ManagerLoginResponse>())!.Token;
    }

    private static HttpRequestMessage Get(string token) =>
        new(HttpMethod.Get, "/" + ManagerRoutes.Summary) { Headers = { { "Authorization", "Bearer " + token } } };

    [Fact]
    public async Task Manager_reads_the_factory_summary_online_read_only_and_with_a_locked_login()
    {
        ERP.Cloud.Api.ManagerEndpoints.ResetLocks();
        await SeedRepAsync("مدير");
        var agentKey = await EnableSyncAsync(BaseUrl);
        await using var cloud = Cloud(agentKey);
        using var agent = CloudSyncService.CreateClient(BaseUrl, agentKey, cloud.Server.CreateHandler());
        using var http = cloud.CreateClient();
        var state = new CloudSyncState();

        // ---- حساب المدير في المعمل: كلمة مرور قصيرة ترفض، والصحيحة تُحفظ مجزّأة ----
        await using (var db = NewDb())
        {
            var svc = new ManagerSummaryService(db);
            Assert.Contains("8 أحرف", (await svc.SaveAccountAsync("boss.cloud", "short", AdminId)).ErrorMessage);
            Assert.Contains("اسم المستخدم", (await svc.SaveAccountAsync("a", "Strong#2026", AdminId)).ErrorMessage);
            Assert.True((await svc.SaveAccountAsync("Boss.Cloud", "Strong#2026", AdminId)).Success);
            var saved = await db.ManagerAccounts.AsNoTracking().SingleAsync();
            Assert.Equal("boss.cloud", saved.Username);
            Assert.DoesNotContain("Strong#2026", saved.PasswordHash + saved.Salt);   // لا كلمة مرور نصية
        }

        // قبل أن يرفع المعمل شيئًا: لا دخول ولا ملخص
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/" + ManagerRoutes.Login, new ManagerLoginRequest("boss.cloud", "Strong#2026"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/" + ManagerRoutes.Summary)).StatusCode);

        await using (var db = NewDb()) Assert.True((await new CloudSyncService(db).RunOnceAsync(agent, "SERVER-PC", state)).Ok);

        // ---- الصفحة ثابتة بلا بيانات، والدخول ينجح بالاسم ولو بحروف كبيرة ----
        var page = await http.GetAsync("/manager/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("لوحة المدير", await page.Content.ReadAsStringAsync());
        Assert.Contains("default-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(HttpStatusCode.Redirect, (await cloud.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/manager")).StatusCode);
        foreach (var file in new[] { "app.js", "style.css", "manifest.webmanifest", "sw.js", "icon.svg" })
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/manager/{file}")).StatusCode);

        var token = await LoginAsync(http, "BOSS.cloud", "Strong#2026");
        var read = await http.SendAsync(Get(token));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("no-store", read.Headers.CacheControl!.ToString());
        var body = await read.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var summary = doc.RootElement.GetProperty("summary");
        Assert.Equal(30, summary.GetProperty("last30").GetArrayLength());
        Assert.True(summary.GetProperty("boxes").GetArrayLength() > 0);
        Assert.Contains(summary.GetProperty("reps").EnumerateArray(), r => r.GetProperty("name").GetString() == "مندوب مدير");
        Assert.True(summary.GetProperty("hr").TryGetProperty("present", out _));
        // الخصوصية: لا رواتب ولا سلف ولا مفاتيح في الملخص
        foreach (var forbidden in new[] { "salary", "advance", "payroll", "BaseSalary", "agentKey", "deviceKey" })
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);

        // ---- قراءة فقط: لا مسار كتابة للمدير ----
        var write = new HttpRequestMessage(HttpMethod.Put, "/" + ManagerRoutes.Summary) { Headers = { { "Authorization", "Bearer " + token } }, Content = JsonContent.Create(new { }) };
        Assert.True((int)(await http.SendAsync(write)).StatusCode is 404 or 405);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Get("x".PadRight(40, 'x')))).StatusCode);
        // ملخص المعمل لا يُرفع إلا بمفتاح المعمل
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PutAsJsonAsync("/" + ManagerRoutes.AgentSummary, new { })).StatusCode);

        // ---- دخول خاطئ: رسالة واحدة لا تكشف الاسم، ثم قفل مؤقت ----
        using var attacker = cloud.CreateClient();
        for (var i = 0; i < ManagerEndpoints_MaxFailures; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.PostAsJsonAsync("/" + ManagerRoutes.Login, new ManagerLoginRequest("boss.cloud", "wrong-pass-" + i))).StatusCode);
        Assert.Equal((HttpStatusCode)429, (await attacker.PostAsJsonAsync("/" + ManagerRoutes.Login, new ManagerLoginRequest("boss.cloud", "Strong#2026"))).StatusCode);

        // صاحب الحساب الحقيقي يعود بعد انتهاء القفل (هنا نمسحه بدل انتظار 15 دقيقة)
        ERP.Cloud.Api.ManagerEndpoints.ResetLocks();

        // ---- إيقاف الحساب في المعمل يُبطل جلسته في الدورة التالية، والحذف كذلك ----
        await using (var db = NewDb())
        {
            var svc = new ManagerSummaryService(db);
            var id = (await svc.AccountsAsync()).Single().Id;
            Assert.True((await svc.SetActiveAsync(id, false, AdminId)).Success);
            Assert.True((await new CloudSyncService(db).RunOnceAsync(agent, "SERVER-PC", state)).Ok);
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Get(token))).StatusCode);
            Assert.True((await svc.SetActiveAsync(id, true, AdminId)).Success);
            Assert.True((await new CloudSyncService(db).RunOnceAsync(agent, "SERVER-PC", state)).Ok);
            var again = await LoginAsync(http, "boss.cloud", "Strong#2026");
            Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Get(again))).StatusCode);
            Assert.True((await svc.DeleteAccountAsync(id, AdminId)).Success);
            Assert.True((await new CloudSyncService(db).RunOnceAsync(agent, "SERVER-PC", state)).Ok);
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Get(again))).StatusCode);
        }
    }

    private const int ManagerEndpoints_MaxFailures = ERP.Cloud.Api.ManagerEndpoints.MaxFailures;
}
