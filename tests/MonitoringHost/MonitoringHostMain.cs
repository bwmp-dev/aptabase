using System.Reflection;
using System.Security.Claims;
using Aptabase.Data;
using Aptabase.Features;
using Aptabase.Features.Apps;
using Aptabase.Features.ErrorReporting;
using Aptabase.Features.ErrorReporting.Buffer;
using Aptabase.Features.Ingestion;
using Aptabase.Features.Performance;
using Aptabase.Features.Stats;
using Aptabase.Features.Sentry;
using Dapper;
using FluentMigrator.Runner;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;

namespace Aptabase.MonitoringHost;

public static class MonitoringHostMain
{
    public static async Task Main()
    {
        var url = Environment.GetEnvironmentVariable("APTABASE_ISSUE_TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("An isolated PostgreSQL test connection is required.");
        var webRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("MONITORING_TEST_WEBROOT") ?? "src/wwwroot");
        if (!File.Exists(Path.Combine(webRoot, "index.html"))) throw new InvalidOperationException("Build the webapp first.");
        var database = "aptabase_browser_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(url);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE DATABASE {database}");
        try
        {
            url = new NpgsqlConnectionStringBuilder(url) { Database = database }.ConnectionString;
            Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
            await using var source = NpgsqlDataSource.Create(url);
            var db = new DbContext(source);
            await using var connection = db.Connection;
            var env = (EnvSettings)Activator.CreateInstance(typeof(EnvSettings), nonPublic: true)!;
            typeof(EnvSettings).GetProperty(nameof(EnvSettings.SelfBaseUrl))!.SetValue(env, "http://127.0.0.1:55440");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = webRoot });
            builder.Services.AddFluentMigratorCore().ConfigureRunner(r =>
                r.AddPostgres().WithGlobalConnectionString(url).ScanIn(typeof(IssuesController).Assembly).For.Migrations());
            builder.WebHost.UseUrls("http://127.0.0.1:55440");
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Services.AddSingleton(env);
            builder.Services.AddSingleton<IDbContext>(db);
            builder.Services.AddSingleton<IIssueStore, IssueStore>();
            builder.Services.AddSingleton<PerformanceStore>();
            builder.Services.AddSingleton<IPiiSanitizer, PiiSanitizer>();
            builder.Services.AddSingleton<ErrorIngestionService>();
            builder.Services.AddSingleton<IAppQueries, AppQueries>();
            builder.Services.AddSingleton<IIngestionCache, IngestionCache>();
            builder.Services.AddSingleton<IRejectionStats, RejectionStats>();
            builder.Services.AddSingleton<IErrorQueryClient>(new ClickHouseErrorQueryClient(new RejectAnalyticsQueries()));
            builder.Services.AddMemoryCache();
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o => {
                o.Cookie.Name = "monitoring-test-session";
                o.Cookie.SameSite = SameSiteMode.Strict;
            });
            builder.Services.AddControllers().AddApplicationPart(typeof(IssuesController).Assembly)
                .ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new MonitoringControllersOnly()));
            builder.Services.AddCors(o => {
                o.AddPolicy("AllowAny", p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
                o.AddPolicy("SentryIngestion", p => p.AllowAnyOrigin().AllowAnyHeader().WithMethods("POST", "OPTIONS"));
            });
            builder.Services.AddRateLimiter(o => {
                o.AddFixedWindowLimiter("Stats", p => { p.PermitLimit = 1000; p.Window = TimeSpan.FromMinutes(1); });
                o.AddFixedWindowLimiter("EventIngestion", p => { p.PermitLimit = 1000; p.Window = TimeSpan.FromMinutes(1); });
            });
            await using var app = builder.Build();
            using (var scope = app.Services.CreateScope())
                scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
            await connection.ExecuteAsync("""
                INSERT INTO users (id, name, email) VALUES ('test-owner', 'Test owner', 'owner@example.test');
                INSERT INTO apps (id, owner_id, name, app_key, has_events) VALUES
                    ('test-app', 'test-owner', 'Sample desktop app', 'TEST-APP-KEY', true),
                    ('other-app', 'test-owner', 'Sample other app', 'OTHER-APP-KEY', true);
                INSERT INTO app_shares (app_id, email) VALUES ('test-app', 'reader@example.test');
                """);

            app.UseStaticFiles();
            app.UseRouting();
            app.UseCors();
            app.UseRateLimiter();
            app.UseAuthentication();
            app.MapControllers();
            app.MapGet("/test/health", () => Results.Ok(new { ready = true }));
            // This standalone test executable is never registered in the production application.
            app.MapPost("/test/login/{role}", async (string role, HttpContext context) => {
                if (role is not ("owner" or "reader" or "stranger")) return Results.BadRequest();
                var identity = new ClaimsIdentity(new[] { new Claim("id", "test-" + role), new Claim("name", "Test " + role),
                    new Claim("email", role + "@example.test") }, CookieAuthenticationDefaults.AuthenticationScheme);
                await context.SignInAsync(new ClaimsPrincipal(identity));
                return Results.Ok();
            });
            app.MapGet("/api/_auth/me", (HttpContext c) => !c.IsAuthenticated() ? Results.Unauthorized() : Results.Ok(new {
                id = c.GetCurrentUserIdentity().Id, name = "Test user", email = c.GetCurrentUserIdentity().Email, avatarUrl = "/test/avatar.svg"
            }));
            app.MapGet("/api/_apps", async (HttpContext c, IIssueStore issues) => {
                if (!c.IsAuthenticated()) return Results.Unauthorized();
                var apps = new List<object>();
                foreach (var id in new[] { "test-app", "other-app" }) {
                    var access = await issues.GetAccess(id, c.GetCurrentUserIdentity(), c.RequestAborted);
                    if (access.CanRead) apps.Add(new { id, name = id == "test-app" ? "Sample desktop app" : "Sample other app",
                        appKey = id == "test-app" ? "TEST-APP-KEY" : "OTHER-APP-KEY", iconPath = "", hasEvents = true, hasOwnership = access.CanManage });
                }
                return Results.Ok(apps);
            });
            app.MapGet("/api/_app-requests", (HttpContext c) => c.IsAuthenticated() ? Results.Ok(Array.Empty<object>()) : Results.Unauthorized());
            app.MapGet("/test/avatar.svg", () => Results.Text("<svg xmlns='http://www.w3.org/2000/svg' width='32' height='32'><rect width='32' height='32' fill='#888'/></svg>", "image/svg+xml"));
            app.MapFallbackToFile("index.html");
            using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            Console.WriteLine("Monitoring test host: http://127.0.0.1:55440 (15 minute maximum lifetime)");
            await app.RunAsync(lifetime.Token);
        }
        finally { await admin.ExecuteAsync($"DROP DATABASE {database} WITH (FORCE)"); }
    }

    private sealed class MonitoringControllersOnly : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            var allowed = new[] { typeof(ErrorsController), typeof(IssuesController), typeof(PerformanceController), typeof(ReleasesController), typeof(SentryController) };
            foreach (var controller in feature.Controllers.ToArray())
                if (!allowed.Contains(controller.AsType())) feature.Controllers.Remove(controller);
        }
    }

    private sealed class RejectAnalyticsQueries : IQueryClient
    {
        public Task<IEnumerable<T>> NamedQueryAsync<T>(string name, object args, CancellationToken cancellationToken) => throw new InvalidOperationException("Raw analytics sink is outside this monitoring test host");
        public Task<T> NamedQuerySingleAsync<T>(string name, object args, CancellationToken cancellationToken) where T : new() => throw new InvalidOperationException("Raw analytics sink is outside this monitoring test host");
        public Task<Stream> StreamResponseAsync(string query, CancellationToken cancellationToken) => throw new InvalidOperationException("Raw analytics sink is outside this monitoring test host");
    }
}
