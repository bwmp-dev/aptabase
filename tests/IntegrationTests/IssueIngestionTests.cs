using System.Net;
using Aptabase.Data;
using Aptabase.Features;
using Aptabase.Features.Apps;
using Aptabase.Features.ErrorReporting;
using Aptabase.Features.ErrorReporting.Buffer;
using Aptabase.Features.Ingestion;
using Aptabase.Features.Performance;
using Aptabase.Features.Stats;
using Dapper;
using FluentMigrator.Runner;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Npgsql;
using Xunit;

namespace Aptabase.IntegrationTests;

public class IssueIngestionTests
{
    [Fact]
    public async Task Http_Ingestion_Deduplicates_Atomically_And_Reopens_Only_New_Recurrences()
    {
        var url = Environment.GetEnvironmentVariable("APTABASE_ISSUE_TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("Set APTABASE_ISSUE_TEST_DATABASE_URL to an isolated test PostgreSQL instance.");
        var database = "aptabase_ingestion_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(url);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE DATABASE {database}");
        try
        {
            url = new NpgsqlConnectionStringBuilder(url) { Database = database }.ConnectionString;
            await using var migrations = new ServiceCollection().AddFluentMigratorCore().ConfigureRunner(r =>
                r.AddPostgres().WithGlobalConnectionString(url).ScanIn(typeof(Program).Assembly).For.Migrations()).BuildServiceProvider();
            using var scope = migrations.CreateScope();
            scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
            Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
            await using var source = NpgsqlDataSource.Create(url);
            var db = new DbContext(source);
            await using var connection = db.Connection;
            await connection.ExecuteAsync("""
                INSERT INTO users (id, name, email) VALUES ('owner', 'Owner', 'owner@example.test');
                INSERT INTO apps (id, owner_id, name, app_key, has_events, error_quota) VALUES
                    ('app-a', 'owner', 'App A', 'KEY-A', false, 1),
                    ('app-b', 'owner', 'App B', 'KEY-B', false, 100);
                """);
            var store = new IssueStore(db);
            var buffer = new InMemoryErrorBuffer();
            var env = (EnvSettings)Activator.CreateInstance(typeof(EnvSettings), nonPublic: true)!;
            typeof(EnvSettings).GetProperty(nameof(EnvSettings.ErrorQuotaEnabled))!.SetValue(env, true);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(env);
            builder.Services.AddSingleton<IDbContext>(db);
            builder.Services.AddSingleton<IIssueStore>(store);
            builder.Services.AddSingleton<PerformanceStore>();
            builder.Services.AddSingleton<IErrorBuffer>(buffer);
            builder.Services.AddSingleton<IPiiSanitizer, PiiSanitizer>();
            builder.Services.AddSingleton<IAppQueries, AppQueries>();
            builder.Services.AddSingleton<IIngestionCache, IngestionCache>();
            builder.Services.AddSingleton<IRejectionStats, RejectionStats>();
            builder.Services.AddMemoryCache();
            builder.Services.AddSingleton<IErrorQueryClient>(new ClickHouseErrorQueryClient(new RejectAnalyticsQueries()));
            builder.Services.AddControllers().AddApplicationPart(typeof(ErrorsController).Assembly);
            builder.Services.AddCors(options => options.AddPolicy("AllowAny", policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
            builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("EventIngestion", limiter =>
            {
                limiter.PermitLimit = 100;
                limiter.Window = TimeSpan.FromMinutes(1);
            }));
            await using var app = builder.Build();
            app.UseRouting();
            app.UseCors();
            app.UseRateLimiter();
            app.MapControllers();
            await app.StartAsync();
            using var http = app.GetTestClient();
            var eventId = Guid.NewGuid().ToString();
            object Body(string? id, DateTime? timestamp = null, bool debug = false) => new
            {
                eventId = id, errorType = "Type", errorMessage = "Failed for owner@example.test", stackTrace = "at App.Run()",
                timestamp = timestamp ?? DateTime.UtcNow, isDebug = debug, release = "1.2.0", environment = "production",
                platform = "desktop", osName = "Windows", sdkVersion = "test", sessionId = "must-not-be-in-context"
            };
            http.DefaultRequestHeaders.Add("App-Key", "INVALID");
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync("/api/v0/error", new StringContent("malformed", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
            http.DefaultRequestHeaders.Remove("App-Key");
            http.DefaultRequestHeaders.Add("App-Key", "KEY-A");
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/v0/error", Body("bad-id"))).StatusCode);
            var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => http.PostAsJsonAsync("/api/v0/error", Body(eventId))));
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
            Assert.Equal(1, buffer.Count());
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT error_count FROM apps WHERE id = 'app-a'"));
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_receipts WHERE app_id = 'app-a'"));
            var rejectedId = Guid.NewGuid().ToString();
            Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("/api/v0/error", Body(rejectedId))).StatusCode);
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_receipts WHERE app_id = 'app-a'"));
            var issue = Assert.Single((await store.List("app-a", false, "all", 0, 50, default)).Issues);
            Assert.Equal("Failed for [EMAIL_REDACTED]", issue.ErrorMessage);
            Assert.Equal(1, issue.OccurrenceCount);
            var resolved = await store.SetStatus("app-a", false, issue.Id, "resolved", "owner", default);
            // Use a fixed past boundary; Docker's database clock can differ from the Windows host clock.
            resolved!.ResolvedAt = DateTime.UtcNow.AddMinutes(-1);
            await connection.ExecuteAsync("UPDATE error_issues SET resolved_at = @at WHERE app_id = 'app-a'", new { at = resolved.ResolvedAt });
            Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsJsonAsync("/api/v0/error", Body(eventId))).StatusCode);
            Assert.Equal("resolved", (await store.Get("app-a", false, issue.Id, default))!.Status);
            await connection.ExecuteAsync("UPDATE apps SET error_quota = 100 WHERE id = 'app-a'");
            Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsJsonAsync("/api/v0/error", Body(rejectedId, resolved!.ResolvedAt!.Value.AddDays(-1)))).StatusCode);
            Assert.Equal("resolved", (await store.Get("app-a", false, issue.Id, default))!.Status);
            Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsJsonAsync("/api/v0/error", Body(Guid.NewGuid().ToString()))).StatusCode);
            var reopened = await store.Get("app-a", false, issue.Id, default);
            Assert.Equal("unresolved", reopened!.Status);
            Assert.Null(reopened.ResolvedAt);
            Assert.Equal(3, reopened.OccurrenceCount);
            Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsJsonAsync("/api/v0/error", Body(eventId, debug: true))).StatusCode);
            http.DefaultRequestHeaders.Remove("App-Key");
            http.DefaultRequestHeaders.Add("App-Key", "KEY-B");
            Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsJsonAsync("/api/v0/error", Body(eventId))).StatusCode);
            Assert.Equal(1, (await store.Get("app-b", false, issue.Id, default))!.OccurrenceCount);
            Assert.Equal(1, (await store.Get("app-a", true, issue.Id, default))!.OccurrenceCount);
            Assert.Equal(1, (await store.List("app-a", false, "all", 0, 50, default, "fAiLeD")).Total);
            Assert.Equal(0, (await store.List("app-a", false, "all", 0, 50, default, "%")).Total);
            Assert.Equal(0, (await store.List("app-a", false, "all", 0, 50, default, "' OR 1=1 --")).Total);

            var events = await store.Events("app-a", false, issue.Id, 0, 50, default, "production", "1.2.0");
            Assert.Equal(3, events.Total);
            Assert.All(events.Events, e => {
                Assert.Equal("1.2.0", e.Release);
                Assert.Equal("Windows", e.OsName);
                Assert.Equal("Failed for [EMAIL_REDACTED]", e.ErrorMessage);
            });
            Assert.Equal(0, (await store.Events("app-a", false, issue.Id, 0, 50, default, "staging")).Total);
            Assert.Equal(1, (await store.List("app-a", false, "all", 0, 50, default, environment: "production", release: "1.2.0")).Total);
            Assert.Equal(0, (await store.List("app-a", false, "all", 0, 50, default, release: "other")).Total);
            // A failure after receipt insertion and quota charging must roll back both.
            await connection.ExecuteAsync("ALTER TABLE error_issues ADD CONSTRAINT test_failure CHECK (occurrence_count < 2) NOT VALID");
            var beforeQuota = await connection.ExecuteScalarAsync<int>("SELECT error_count FROM apps WHERE id = 'app-b'");
            var retryable = new TrackingError { AppId = "app-b", ErrorId = Guid.NewGuid().ToString(), ErrorType = "Type", ErrorMessage = "message", StackTrace = "at App.Run()", Timestamp = DateTime.UtcNow };
            await Assert.ThrowsAsync<PostgresException>(() => store.Record(retryable, default, true));
            Assert.Equal(beforeQuota, await connection.ExecuteScalarAsync<int>("SELECT error_count FROM apps WHERE id = 'app-b'"));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_receipts WHERE event_id = @id", new { id = Guid.Parse(retryable.ErrorId) }));
            await connection.ExecuteAsync("ALTER TABLE error_issues DROP CONSTRAINT test_failure");
            Assert.Equal(IssueRecordResult.Accepted, await store.Record(retryable, default, true));
            await connection.ExecuteAsync("UPDATE error_receipts SET received_at = CURRENT_TIMESTAMP - INTERVAL '31 days' WHERE app_id = 'app-b'");
            Assert.Equal(2, await store.PurgeExpiredReceipts(default));
            Assert.Equal(IssueRecordResult.Accepted, await store.Record(retryable, default, true));
            Assert.True(await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_receipts WHERE app_id = 'app-a'") > 0);
            var performance = app.Services.GetRequiredService<PerformanceStore>();
            var transactionId = Guid.NewGuid().ToString();
            TransactionBody Transaction(double duration = 100) => new() {
                EventId = transactionId, Name = "app.startup", Operation = "ui.load", DurationMs = duration,
                Timestamp = DateTime.UtcNow, Release = "1.2.0", Environment = "production", Platform = "desktop",
                Spans = new() { new() { SpanId = "1111111111111111", Name = "read owner@example.test", Operation = "file.read", DurationMs = 20 } }
            };
            var transactionReplies = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => http.PostAsJsonAsync("/api/v0/transaction", Transaction())));
            Assert.All(transactionReplies, reply => Assert.Equal(HttpStatusCode.Accepted, reply.StatusCode));
            var perfFilter = new PerformanceFilter("app-b", false, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, "production", "1.2.0", null, null);
            Assert.Equal(1, (await performance.List(perfFilter, 0, 50, default)).Total);
            var detail = await performance.Get("app-b", false, Guid.Parse(transactionId), default);
            Assert.Equal("read [EMAIL_REDACTED]", Assert.Single(detail!.Spans).Name);
            Assert.Null(await performance.Get("app-a", false, Guid.Parse(transactionId), default));
            Assert.Null(await performance.Get("app-b", true, Guid.Parse(transactionId), default));
            transactionId = Guid.NewGuid().ToString();
            var second = Transaction(200); second.Status = "error";
            Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsJsonAsync("/api/v0/transaction", second)).StatusCode);
            perfFilter = perfFilter with { EndDate = DateTime.UtcNow };
            var group = Assert.Single(await performance.Summary(perfFilter, default));
            Assert.Equal(2, group.Count);
            Assert.Equal(1, group.ErrorCount);
            Assert.Equal(150, group.P50Ms);
            Assert.Equal(195, group.P95Ms);
            Assert.Equal(150, group.AvgMs);
            Assert.Empty(await performance.Summary(perfFilter with { Release = "no-match" }, default));
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/v0/transaction", Transaction(-1))).StatusCode);
            await connection.ExecuteAsync("UPDATE app_transactions SET received_at = CURRENT_TIMESTAMP - INTERVAL '31 days'");
            Assert.Equal(2, await performance.Purge(default));
            Assert.Equal(0, (await performance.List(perfFilter, 0, 50, default)).Total);
            await app.StopAsync();
        }
        finally
        {
            await admin.ExecuteAsync($"DROP DATABASE {database} WITH (FORCE)");
        }
    }

    private class RejectAnalyticsQueries : IQueryClient
    {
        public Task<IEnumerable<T>> NamedQueryAsync<T>(string name, object args, CancellationToken cancellationToken) => throw new InvalidOperationException("Analytics reads are outside this ingestion test");
        public Task<T> NamedQuerySingleAsync<T>(string name, object args, CancellationToken cancellationToken) where T : new() => throw new InvalidOperationException("Analytics reads are outside this ingestion test");
        public Task<Stream> StreamResponseAsync(string query, CancellationToken cancellationToken) => throw new InvalidOperationException("Analytics reads are outside this ingestion test");
    }
}
