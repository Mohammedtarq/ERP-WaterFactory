using System.Net;
using ERP.Data.Setup;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>منطق فحص التحديث التلقائي: اسم السيرفر من الاتصال، وقراءة version.json، والمقارنة، وعدم منع العمل عند تعذّر الاتصال.</summary>
public class UpdateServiceTests
{
    [Theory]
    [InlineData(@"Server=SERVER-PC\SQLEXPRESS;Database=x;", "SERVER-PC")]
    [InlineData(@"Server=tcp:192.168.1.5,1433;Database=x;", "192.168.1.5")]
    [InlineData(@"Data Source=.\SQLEXPRESS;Initial Catalog=x;", "localhost")]
    [InlineData(@"Server=(local);Database=x;", "localhost")]
    [InlineData(@"Server=(localdb)\MSSQLLocalDB;Database=x;", null)]
    [InlineData("", null)]
    [InlineData("ليس اتصالًا", null)]
    public void Server_host_comes_from_the_control_connection(string cs, string? expected) => Assert.Equal(expected, UpdateService.ServerHost(cs));

    [Fact]
    public void Offer_is_parsed_and_bad_json_is_ignored()
    {
        var o = UpdateService.ParseOffer("""{"version":"1.0.451.0","mandatory":true,"publishedAt":"x"}""");
        Assert.Equal((new Version(1, 0, 451, 0), true), (o!.Version, o.Mandatory));
        Assert.False(UpdateService.ParseOffer("""{"version":"1.0.1"}""")!.Mandatory);
        Assert.Null(UpdateService.ParseOffer("not json"));
        Assert.Null(UpdateService.ParseOffer("""{"version":"abc"}"""));
        Assert.Equal("http://server-pc:5080/update/", UpdateService.BaseUri("SERVER-PC").ToString());   // Uri يوحّد حالة الأحرف في اسم الجهاز
    }

    private sealed class Fake(Func<HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(reply());
    }

    [Fact]
    public async Task Check_offers_only_a_newer_version_and_never_throws()
    {
        var baseUri = UpdateService.BaseUri("srv");
        static HttpResponseMessage Json(string v, bool m = false) => new(HttpStatusCode.OK) { Content = new StringContent($$"""{"version":"{{v}}","mandatory":{{m.ToString().ToLower()}}}""") };

        using var newer = new HttpClient(new Fake(() => Json("1.0.451.0", true)));
        var offer = await UpdateService.CheckAsync(newer, baseUri, new Version(1, 0, 450, 0));
        Assert.Equal((new Version(1, 0, 451, 0), true), (offer!.Version, offer.Mandatory));

        using var same = new HttpClient(new Fake(() => Json("1.0.450.0")));
        Assert.Null(await UpdateService.CheckAsync(same, baseUri, new Version(1, 0, 450, 0)));
        using var older = new HttpClient(new Fake(() => Json("1.0.449.0")));
        Assert.Null(await UpdateService.CheckAsync(older, baseUri, new Version(1, 0, 450, 0)));
        using var missing = new HttpClient(new Fake(() => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Null(await UpdateService.CheckAsync(missing, baseUri, new Version(1, 0, 450, 0)));
        using var down = new HttpClient(new Fake(() => throw new HttpRequestException("no route")));
        Assert.Null(await UpdateService.CheckAsync(down, baseUri, new Version(1, 0, 450, 0)));
    }
}
