using AgeDepthStudio.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgeDepthStudio.Tests.Web;

public class WebSmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public WebSmokeTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async System.Threading.Tasks.Task Root_serves_review_page_with_samples()
    {
        var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/");
        Assert.Contains("岩芯深度-年龄模型审阅台", html);
        Assert.Contains("岩芯纵剖", html);

        var ws = await client.GetStringAsync("/api/workspaces");
        Assert.Contains("A-LINE", ws);
        Assert.Contains("B-BND", ws);

        var state = await client.GetStringAsync("/api/workspaces/A-LINE");
        Assert.Contains("figureUrl", state);

        var svg = await client.GetStringAsync("/api/workspaces/A-LINE/figure");
        Assert.StartsWith("<svg", svg.TrimStart());
        Assert.Contains("data-id=", svg);
        Assert.Contains("age-depth-figure", svg);
    }

    [Fact]
    public async System.Threading.Tasks.Task Freeze_conflict_roundtrip_visible_via_api()
    {
        var client = _factory.CreateClient();
        var body = """{"modelId":"M4","expectedParamsId":"PAR-1","actor":"test-stale"}""";
        var content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/api/workspaces/A-LINE/freeze", content);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"ok\":false", json);
    }

    [Fact]
    public async System.Threading.Tasks.Task Export_import_roundtrip_through_http()
    {
        var client = _factory.CreateClient();
        var archive = await client.GetStringAsync("/api/workspaces/A-LINE/export");
        var content = new System.Net.Http.StringContent(archive, System.Text.Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/api/import", content);
        resp.EnsureSuccessStatusCode();
        var verify = await client.GetStringAsync("/api/workspaces/A-LINE/verify");
        Assert.Contains("\"valid\":true", verify);
    }
}
