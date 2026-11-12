using System.Security.Claims;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.RateLimiting;
using Aptabase.Data;
using Aptabase.Features.Authentication;
using Aptabase.Features.ErrorReporting;
using Aptabase.Features.Performance;
using Aptabase.Features.ErrorReporting.Buffer;
using Dapper;
using FluentMigrator.Runner;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Xunit;

namespace Aptabase.IntegrationTests;

// Uses a dedicated disposable database supplied by the test runner, never the developer's app database.
public class IssueTrackingTests
{
    [Fact]
    public async Task Groups_Reports_And_Enforces_Lifecycle_And_Tenant_Boundaries()
    {
        var url = Environment.GetEnvironmentVariable("APTABASE_ISSUE_TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("Set APTABASE_ISSUE_TEST_DATABASE_URL to an isolated empty test database.");
        var database = "aptabase_issues_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(url);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE DATABASE {database}");
        try
        {
            url = new NpgsqlConnectionStringBuilder(url) { Database = database }.ConnectionString;
            await using var services = new ServiceCollection().AddFluentMigratorCore().ConfigureRunner(r =>
                r.AddPostgres().WithGlobalConnectionString(url).ScanIn(typeof(Program).Assembly).For.Migrations()).BuildServiceProvider();
            using var scope = services.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
            runner.MigrateUp();
            Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
            await using var source = NpgsqlDataSource.Create(url);
            var db = new DbContext(source);
            await using var connection = db.Connection;
            var owner = new UserIdentity("issue-owner", "Owner", "owner@example.test");
            var reader = new UserIdentity("issue-reader", "Reader", "reader@example.test");
            var stranger = new UserIdentity("issue-stranger", "Stranger", "stranger@example.test");
            await connection.ExecuteAsync("""
                INSERT INTO users (id, name, email) VALUES ('issue-owner', 'Owner', 'owner@example.test');
                INSERT INTO apps (id, owner_id, name, app_key, has_events) VALUES
                    ('issue-app-a', 'issue-owner', 'App A', 'issue-key-a', false),
                    ('issue-app-b', 'issue-owner', 'App B', 'issue-key-b', false);
                INSERT INTO app_shares (app_id, email) VALUES ('issue-app-a', 'reader@example.test');
                """);
            var store = new IssueStore(db);
            TrackingError Report(string app = "issue-app-a", bool debug = false, int days = 0) => new()
            {
                AppId = app, IsDebug = debug, ErrorId = Guid.NewGuid().ToString(), ErrorType = "Type",
                ErrorMessage = "Sanitized message", StackTrace = "at App.Run()",
                Timestamp = DateTime.UtcNow.Date.AddDays(days)
            };
            var id = IssueFingerprint.Create("Type", "Sanitized message", "at App.Run()");
            await store.Record(Report(), default);
            // A small concurrent batch verifies the database upsert does not lose increments.
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.Record(Report(days: -1), default)));
            await store.Record(Report(debug: true), default);
            await store.Record(Report("issue-app-b"), default);
            var issue = await store.Get("issue-app-a", false, id, default);
            Assert.Equal(5, issue!.OccurrenceCount);
            Assert.Equal(DateTime.UtcNow.Date.AddDays(-1), issue.FirstSeen);
            Assert.Equal(DateTime.UtcNow.Date, issue.LastSeen);
            Assert.Equal(1, (await store.Get("issue-app-a", true, id, default))!.OccurrenceCount);
            Assert.Equal(1, (await store.Get("issue-app-b", false, id, default))!.OccurrenceCount);
            Assert.Null(await store.Get("unknown-app", false, id, default));
            Assert.Equal(new IssueAccess(true, true), await store.GetAccess("issue-app-a", owner, default));
            Assert.Equal(new IssueAccess(true, false), await store.GetAccess("issue-app-a", reader, default));
            Assert.False((await store.GetAccess("issue-app-a", stranger, default)).CanRead);
            Assert.False((await store.GetAccess("issue-app-b", reader, default)).CanRead);
            Assert.Null(await store.SetStatus("issue-app-a", false, id, "resolved", reader.Id, default));
            var resolved = await store.SetStatus("issue-app-a", false, id, "resolved", owner.Id, default);
            Assert.NotNull(resolved!.ResolvedAt);
            Assert.Equal(resolved.ResolvedAt, (await store.SetStatus("issue-app-a", false, id, "resolved", owner.Id, default))!.ResolvedAt);
            await store.Record(Report(), default);
            Assert.Equal("resolved", (await store.Get("issue-app-a", false, id, default))!.Status);
            Assert.Equal(0, (await store.List("issue-app-a", false, "unresolved", 0, 50, default)).Total);
            Assert.Equal(1, (await store.List("issue-app-a", false, "resolved", 0, 50, default)).Total);
            var page = await store.List("issue-app-a", false, "all", 1, 1, default);
            Assert.Equal(1, page.Total);
            Assert.Empty(page.Issues);
            var reopened = await store.SetStatus("issue-app-a", false, id, "unresolved", owner.Id, default);
            Assert.Null(reopened!.ResolvedAt);
            Assert.Equal(6, reopened.OccurrenceCount);

            IssuesController Controller(UserIdentity user) => new(store)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("id", user.Id), new Claim("email", user.Email) }, "test")) } }
            };
            var controller = Controller(owner);
            Assert.IsType<OkObjectResult>(await controller.List("issue-app-a"));
            Assert.IsType<BadRequestObjectResult>(await controller.List("issue-app-a", limit: 101));
            Assert.IsType<BadRequestObjectResult>(await controller.List("issue-app-a", offset: -1));
            Assert.IsType<BadRequestObjectResult>(await controller.List("issue-app-a", status: "invalid"));
            Assert.IsType<BadRequestObjectResult>(await controller.Get("issue-app-a", id, "invalid"));
            Assert.IsType<NotFoundResult>(await controller.Get("issue-app-a", "missing"));
            Assert.IsType<BadRequestObjectResult>(await controller.Update("issue-app-a", id, new IssueStatusBody { Status = "ignored" }));
            Assert.IsType<OkObjectResult>(await controller.Update("issue-app-a", id, new IssueStatusBody { Status = "resolved" }));
            Assert.Equal(403, Assert.IsType<StatusCodeResult>(await Controller(reader).Update("issue-app-a", id, new IssueStatusBody { Status = "resolved" })).StatusCode);
            Assert.Equal(403, Assert.IsType<StatusCodeResult>(await Controller(stranger).Get("issue-app-a", id)).StatusCode);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IIssueStore>(store);
            builder.Services.AddSingleton<IPiiSanitizer, PiiSanitizer>();
            builder.Services.AddSingleton<IDbContext>(db);
            builder.Services.AddSingleton<PerformanceStore>();
            builder.Services.AddControllers().AddApplicationPart(typeof(IssuesController).Assembly);
            builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("Stats", limiter =>
            {
                limiter.PermitLimit = 100;
                limiter.Window = TimeSpan.FromMinutes(1);
            }));
            await using var app = builder.Build();
            app.UseRouting();
            app.UseRateLimiter();
            app.Use(async (context, next) =>
            {
                if (context.Request.Headers.ContainsKey("Test-Owner"))
                    context.User = Controller(owner).HttpContext.User;
                else if (context.Request.Headers.ContainsKey("Test-Reader"))
                    context.User = Controller(reader).HttpContext.User;
                else if (context.Request.Headers.ContainsKey("Test-Stranger"))
                    context.User = Controller(stranger).HttpContext.User;
                await next(context);
            });
            app.MapControllers();
            await app.StartAsync();
            using var http = app.GetTestClient();
            var route = $"/api/v0/apps/issue-app-a/issues/{id}";
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(route)).StatusCode);
            http.DefaultRequestHeaders.Add("Test-Owner", "true");
            var response = await http.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(route + "/events")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync(route + "/events?limit=101")).StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(json.RootElement.GetProperty("canManage").GetBoolean());
            Assert.Equal(6, json.RootElement.GetProperty("issue").GetProperty("occurrenceCount").GetInt64());
            Assert.EndsWith("Z", json.RootElement.GetProperty("issue").GetProperty("firstSeen").GetString());
            Assert.Equal(HttpStatusCode.OK, (await http.PatchAsJsonAsync(route, new { status = "unresolved" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PatchAsJsonAsync(route, new { status = "invalid" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/v0/apps/issue-app-a/issues?limit=invalid")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/v0/apps/issue-app-a/issues/missing")).StatusCode);
            var performance = app.Services.GetRequiredService<PerformanceStore>();
            var eventId = Guid.NewGuid();
            await performance.Record("issue-app-a", new TransactionBody {
                EventId = eventId.ToString(), Timestamp = DateTime.UtcNow, Name = "app.startup", Operation = "ui.load", DurationMs = 100
            }, default);
            var transactionRoute = $"/api/v0/apps/issue-app-a/transactions/{eventId}";
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(transactionRoute)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v0/apps/issue-app-a/releases")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v0/apps/issue-app-a/transactions")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v0/apps/issue-app-a/performance/summary")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/v0/apps/issue-app-a/transactions?limit=101")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(transactionRoute + "?buildMode=debug")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/v0/apps/issue-app-b/transactions/{eventId}")).StatusCode);
            http.DefaultRequestHeaders.Remove("Test-Owner");
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(transactionRoute)).StatusCode);
            http.DefaultRequestHeaders.Add("Test-Stranger", "true");
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(transactionRoute)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(route + "/events")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/v0/apps/issue-app-a/releases")).StatusCode);
            http.DefaultRequestHeaders.Remove("Test-Stranger");
            http.DefaultRequestHeaders.Add("Test-Reader", "true");
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(transactionRoute)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/v0/apps/issue-app-b/performance/summary")).StatusCode);
            await app.StopAsync();
            await connection.ExecuteAsync("UPDATE apps SET deleted_at = CURRENT_TIMESTAMP WHERE id = 'issue-app-a'");
            Assert.False((await store.GetAccess("issue-app-a", owner, default)).CanRead);
            Assert.Null(await store.SetStatus("issue-app-a", false, id, "unresolved", owner.Id, default));
            await connection.ExecuteAsync("UPDATE users SET lock_reason = 'T' WHERE id = 'issue-owner'");
            Assert.False((await store.GetAccess("issue-app-b", owner, default)).CanRead);
            await connection.ExecuteAsync("DELETE FROM app_shares WHERE app_id = 'issue-app-a'; DELETE FROM apps WHERE id IN ('issue-app-a', 'issue-app-b')");
            Assert.Null(await store.Get("issue-app-a", false, id, default));
            runner.MigrateDown(14);
            runner.MigrateUp();
        }
        finally
        {
            await admin.ExecuteAsync($"DROP DATABASE {database} WITH (FORCE)");
        }
    }
}
