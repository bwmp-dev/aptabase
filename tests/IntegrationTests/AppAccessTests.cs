using System.Net;
using Xunit;
using AwesomeAssertions;
using Aptabase.Data;
using Dapper;
using System.Text.Json;

namespace Aptabase.IntegrationTests;

[Collection("Integration Tests")]
public class AppAccessTests
{
    private readonly IntegrationTestsFixture _fixture;

    public AppAccessTests(IntegrationTestsFixture fixture)
    {
        _fixture = fixture;
    }

    private static readonly string[] StatsEndpoints =
    [
        "/api/_stats/top-countries",
        "/api/_stats/top-osversions",
        "/api/_stats/top-devices",
        "/api/_stats/top-operatingsystems",
        "/api/_stats/top-regions",
        "/api/_stats/top-events",
        "/api/_stats/top-appversions",
        "/api/_stats/top-appbuildnumbers",
        "/api/_stats/metrics",
        "/api/_stats/periodic",
        "/api/_stats/top-props",
        "/api/_stats/live-geo",
        "/api/_stats/live-sessions",
        "/api/_stats/live-session-details",
        "/api/_stats/historical-sessions",
    ];

    public static IEnumerable<object[]> AllStatsEndpoints => StatsEndpoints.Select(e => new object[] { e });

    [Fact]
    public async Task Can_Read_Own_App_Stats()
    {
        var app = await _fixture.UserA.CreateApp(Guid.NewGuid().ToString());

        var response = await _fixture.UserA.Get($"/api/_stats/metrics?buildMode=release&appId={app.Id}&startDate=2026-01-01&endDate=2026-02-01");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Cannot_Read_Other_User_App_Stats()
    {
        var app = await _fixture.UserB.CreateApp(Guid.NewGuid().ToString());

        var response = await _fixture.UserA.Get($"/api/_stats/metrics?buildMode=release&appId={app.Id}&startDate=2026-01-01&endDate=2026-02-01");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Existing_Aptabase_Login_Covers_Analytics_And_Monitoring_With_Shared_App_Permissions()
    {
        var app = await _fixture.UserA.CreateApp(Guid.NewGuid().ToString());
        var readRoutes = new[] {
            $"/api/_stats/metrics?buildMode=release&appId={app.Id}&startDate=2026-01-01&endDate=2026-02-01",
            $"/api/v0/apps/{app.Id}/errors", $"/api/v0/apps/{app.Id}/issues",
            $"/api/v0/apps/{app.Id}/transactions", $"/api/v0/apps/{app.Id}/performance/summary",
            $"/api/v0/apps/{app.Id}/releases", $"/api/v0/apps/{app.Id}/monitoring/health"
        };
        var ownerRoutes = new[] {
            $"/api/v0/apps/{app.Id}/monitoring/settings", $"/api/v0/apps/{app.Id}/monitoring/data",
            $"/api/v0/apps/{app.Id}/monitoring/alerts", $"/api/v0/apps/{app.Id}/sentry-projects",
            $"/api/v0/apps/{app.Id}/native-symbols"
        };
        using var anonymous = _fixture.CreateClient();
        anonymous.DefaultRequestHeaders.Add("App-Key", app.AppKey);
        foreach (var route in readRoutes.Concat(ownerRoutes))
        {
            (await _fixture.UserA.Get(route)).StatusCode.Should().Be(HttpStatusCode.OK, route);
            (await _fixture.UserB.Get(route)).StatusCode.Should().Be(HttpStatusCode.Forbidden, route);
            (await anonymous.GetAsync(route)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, route);
        }
        using var identity = JsonDocument.Parse(await (await _fixture.UserB.Get("/api/_auth/me")).Content.ReadAsStringAsync());
        await using var db = _fixture.GetService<IDbContext>().Connection;
        await db.ExecuteAsync("INSERT INTO app_shares (app_id,email) VALUES (@appId,@email)",
            new { appId = app.Id, email = identity.RootElement.GetProperty("email").GetString() });
        try
        {
            foreach (var route in readRoutes)
                (await _fixture.UserB.Get(route)).StatusCode.Should().Be(HttpStatusCode.OK, route);
            foreach (var route in ownerRoutes)
                (await _fixture.UserB.Get(route)).StatusCode.Should().Be(HttpStatusCode.Forbidden, route);
        }
        finally { await db.ExecuteAsync("DELETE FROM app_shares WHERE app_id=@appId", new { appId = app.Id }); }
        (await _fixture.UserB.Get(readRoutes[2])).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory, MemberData(nameof(AllStatsEndpoints))]
    public async Task Stats_Forbid_Other_User_Prefixed_AppId(string endpoint)
    {
        var appA = await _fixture.UserA.CreateApp(Guid.NewGuid().ToString());
        var appB = await _fixture.UserB.CreateApp(Guid.NewGuid().ToString());

        var response = await _fixture.UserA.Get(
            $"{endpoint}?AppId={appA.Id}&body.AppId={appB.Id}&body.BuildMode=release" +
            $"&body.StartDate=2026-01-01&body.EndDate=2026-02-01&body.SessionId={Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Stats_Forbid_Other_User_Prefixed_AppId_Alone()
    {
        var app = await _fixture.UserB.CreateApp(Guid.NewGuid().ToString());

        var response = await _fixture.UserA.Get(
            $"/api/_stats/metrics?body.AppId={app.Id}&body.BuildMode=release&body.StartDate=2026-01-01&body.EndDate=2026-02-01");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Export_Forbids_Other_User_Prefixed_AppId()
    {
        var appA = await _fixture.UserA.CreateApp(Guid.NewGuid().ToString());
        var appB = await _fixture.UserB.CreateApp(Guid.NewGuid().ToString());

        var response = await _fixture.UserA.Get(
            $"/api/_export/download?AppId={appA.Id}&body.AppId={appB.Id}&body.BuildMode=release" +
            $"&body.Format=csv&body.StartDate=2026-01-01&body.EndDate=2026-02-01");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Export_Forbids_Unknown_Prefixed_AppId()
    {
        var app = await _fixture.UserA.CreateApp(Guid.NewGuid().ToString());
        var value = Uri.EscapeDataString("a'b");

        var response = await _fixture.UserA.Get(
            $"/api/_export/download?AppId={app.Id}&body.AppId={value}&body.BuildMode=release" +
            $"&body.Format=csv&body.StartDate=2026-01-01&body.EndDate=2026-02-01");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Export_Forbids_Unknown_AppId()
    {
        var value = Uri.EscapeDataString("a'b");

        var response = await _fixture.UserA.Get(
            $"/api/_export/download?appId={value}&buildMode=release&format=csv&startDate=2026-01-01&endDate=2026-02-01");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Can_Download_Own_App_Export()
    {
        var app = await _fixture.UserA.CreateApp(Guid.NewGuid().ToString());

        var response = await _fixture.UserA.Get(
            $"/api/_export/download?appId={app.Id}&appName=test&buildMode=release&format=csv&startDate=2026-01-01&endDate=2026-02-01");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Cannot_Read_Other_User_Export_Usage()
    {
        var app = await _fixture.UserB.CreateApp(Guid.NewGuid().ToString());

        var response = await _fixture.UserA.Get($"/api/_export/usage?buildMode=release&appId={app.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
