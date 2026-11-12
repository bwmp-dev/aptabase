using System.Net;
using System.Text;
using System.Text.Json;
using System.Security.Claims;
using Aptabase.Features.Sentry;
using Aptabase.Features.Monitoring;
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
            var env = (EnvSettings)Activator.CreateInstance(typeof(EnvSettings), nonPublic: true)!;
            typeof(EnvSettings).GetProperty(nameof(EnvSettings.SelfBaseUrl))!.SetValue(env, "http://localhost");
            typeof(EnvSettings).GetProperty(nameof(EnvSettings.ErrorQuotaEnabled))!.SetValue(env, true);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(env);
            builder.Services.AddSingleton<IDbContext>(db);
            builder.Services.AddSingleton<IIssueStore>(store);
            builder.Services.AddSingleton<PerformanceStore>();
            builder.Services.AddSingleton<IPiiSanitizer, PiiSanitizer>();
            builder.Services.AddSingleton<ErrorIngestionService>();
            builder.Services.AddSingleton<IAppQueries, AppQueries>();
            builder.Services.AddSingleton<IIngestionCache, IngestionCache>();
            builder.Services.AddSingleton<IRejectionStats, RejectionStats>();
            builder.Services.AddMemoryCache();
            builder.Services.AddSingleton<IErrorQueryClient>(new ClickHouseErrorQueryClient(new RejectAnalyticsQueries()));
            builder.Services.AddControllers().AddApplicationPart(typeof(ErrorsController).Assembly);
            builder.Services.AddCors(options => {
                options.AddPolicy("AllowAny", policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
                options.AddPolicy("SentryIngestion", policy => policy.AllowAnyOrigin().AllowAnyHeader().WithMethods("POST", "OPTIONS"));
            });
            builder.Services.AddRateLimiter(o => o.AddFixedWindowLimiter("Stats", p => { p.PermitLimit = 100; p.Window = TimeSpan.FromMinutes(1); }));
            builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("EventIngestion", limiter =>
            {
                limiter.PermitLimit = 100;
                limiter.Window = TimeSpan.FromMinutes(1);
            }));
            await using var app = builder.Build();
            app.UseRouting();
            app.UseCors();
            app.UseRateLimiter();
            app.Use(async (context, next) => {
                if (context.Request.Headers.ContainsKey("Test-Owner"))
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("id", "owner"), new Claim("email", "owner@example.test") }, "test"));
                if (context.Request.Headers.ContainsKey("Test-Reader"))
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("id", "reader"), new Claim("email", "reader@example.test") }, "test"));
                await next();
            });
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
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_outbox"));
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
            var pending = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_outbox");
            Assert.True(pending > 0);
            var sink = new LocalErrorSink { FailAfterAccepting = true };
            var outbox = new ErrorOutbox(db, sink);
            await Assert.ThrowsAsync<IOException>(() => outbox.Flush());
            Assert.Equal(pending, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_outbox"));
            sink.FailAfterAccepting = false;
            sink.PartialAcknowledgement = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() => outbox.Flush());
            Assert.Equal(pending, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_outbox"));
            sink.PartialAcknowledgement = false;
            Assert.Equal(pending, (await Task.WhenAll(outbox.Flush(), outbox.Flush())).Sum());
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_outbox"));
            Assert.Equal(pending, sink.Received.Distinct().Count());
            Assert.True(sink.Received.Count > pending); // Lost acknowledgements permit raw sink replay; reads deduplicate IDs.
            await CheckControls(http, connection, db, store, performance);
            await CheckSentry(http, connection, app.GetTestServer());
            await store.PurgeExpiredReceipts(default); await performance.Purge(default);
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_issues WHERE app_id='app-b'"));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_outbox WHERE app_id='app-b'"));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM app_transactions WHERE app_id='app-b'"));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sentry_projects WHERE app_id='app-b'"));
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_issues WHERE app_id='app-d'"));
            await app.StopAsync();
        }
        finally
        {
            await admin.ExecuteAsync($"DROP DATABASE {database} WITH (FORCE)");
        }
    }

    private static async Task CheckControls(HttpClient http, System.Data.Common.DbConnection connection, IDbContext db, IssueStore store, PerformanceStore performance)
    {
        await connection.ExecuteAsync("""
            INSERT INTO apps (id, owner_id, name, app_key, error_quota, has_events) VALUES ('app-c','owner','C','KEY-C',100,false), ('app-d','owner','D','KEY-D',100,false);
            INSERT INTO app_shares (app_id,email) VALUES ('app-c','reader@example.test');
            """);
        const string route = "/api/v0/apps/app-c/monitoring";
        var settings = new MonitoringSettings { LocalAlertsEnabled = true };
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(route + "/settings")).StatusCode);
        http.DefaultRequestHeaders.Add("Test-Reader", "true");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PutAsJsonAsync(route + "/settings", settings)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(route + "/alerts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(route + "/data")).StatusCode);
        http.DefaultRequestHeaders.Remove("Test-Reader");
        http.DefaultRequestHeaders.Add("Test-Owner", "true");
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync(route + "/settings", new { captureErrors = true })).StatusCode);
        settings.MonitoringRetentionDays = 31;
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync(route + "/settings", settings)).StatusCode);
        settings.MonitoringRetentionDays = 1;
        Assert.Equal(HttpStatusCode.OK, (await http.PutAsJsonAsync(route + "/settings", settings)).StatusCode);
        http.DefaultRequestHeaders.Remove("App-Key"); http.DefaultRequestHeaders.Add("App-Key", "KEY-C");
        var eventId = Guid.NewGuid();
        await connection.ExecuteAsync("ALTER TABLE monitoring_alerts ADD CONSTRAINT test_alert_failure CHECK (kind <> 'new_issue') NOT VALID");
        await Assert.ThrowsAsync<PostgresException>(() => store.Record(new TrackingError {
            AppId="app-c", ErrorId=eventId.ToString(), ErrorType="Local alert", ErrorMessage="sanitized", Timestamp=DateTime.UtcNow
        }, default, true));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT error_count FROM apps WHERE id='app-c'"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_receipts WHERE app_id='app-c'"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_issues WHERE app_id='app-c'"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_outbox WHERE app_id='app-c'"));
        await connection.ExecuteAsync("ALTER TABLE monitoring_alerts DROP CONSTRAINT test_alert_failure");
        var expandingEnvironment = string.Concat(Enumerable.Repeat("a@b.co ", 7));
        object Error(Guid id, string type = "Local alert") => new { eventId = id, errorType = type, errorMessage = "sanitized", timestamp = DateTime.UtcNow, environment = expandingEnvironment };
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => http.PostAsJsonAsync("/api/v0/error", Error(eventId))));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM monitoring_alerts WHERE app_id='app-c'"));
        await connection.ExecuteAsync("UPDATE error_issues SET status='resolved',resolved_at=CURRENT_TIMESTAMP - INTERVAL '1 day' WHERE app_id='app-c'");
        Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsJsonAsync("/api/v0/error", Error(Guid.NewGuid()))).StatusCode);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM monitoring_alerts WHERE app_id='app-c'"));
        await connection.ExecuteAsync("""
            UPDATE error_issues SET status='resolved',resolved_at=CURRENT_TIMESTAMP - INTERVAL '1 day' WHERE app_id='app-c';
            UPDATE monitoring_alert_state SET last_notified_at=CURRENT_TIMESTAMP - INTERVAL '2 hours' WHERE app_id='app-c';
            """);
        Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsJsonAsync("/api/v0/error", Error(Guid.NewGuid()))).StatusCode);
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM monitoring_alerts WHERE app_id='app-c'"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM monitoring_alerts WHERE app_id='app-c' AND kind='regression'"));
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(route + "/alerts")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync(route + "/alerts?limit=101")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PatchAsync(route + $"/alerts/{eventId}/read?buildMode=debug", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await http.PatchAsync(route + $"/alerts/{eventId}/read", null)).StatusCode);
        var readAt = await connection.ExecuteScalarAsync<DateTime>("SELECT read_at FROM monitoring_alerts WHERE app_id='app-c' AND event_id=@eventId", new { eventId });
        Assert.Equal(HttpStatusCode.NoContent, (await http.PatchAsync(route + $"/alerts/{eventId}/read", null)).StatusCode);
        Assert.Equal(readAt, await connection.ExecuteScalarAsync<DateTime>("SELECT read_at FROM monitoring_alerts WHERE app_id='app-c' AND event_id=@eventId", new { eventId }));
        settings.LocalAlertsEnabled = false; settings.ForwardRawErrors = false;
        Assert.Equal(HttpStatusCode.OK, (await http.PutAsJsonAsync(route + "/settings", settings)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsJsonAsync("/api/v0/error", Error(Guid.NewGuid(), "Other issue"))).StatusCode);
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM monitoring_alerts WHERE app_id='app-c'"));
        var sink = new LocalErrorSink();
        await new ErrorOutbox(db, sink).Flush();
        Assert.DoesNotContain(sink.Received, r => r.StartsWith("app-c:"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_outbox WHERE app_id='app-c'"));
        var transaction = new TransactionBody { EventId = Guid.NewGuid().ToString(), Name = "app.startup", Operation = "ui.load", Timestamp = DateTime.UtcNow, DurationMs = 100, Environment = expandingEnvironment };
        Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsJsonAsync("/api/v0/transaction", transaction)).StatusCode);
        var storedEnvironment = await connection.QuerySingleAsync<string>("SELECT environment FROM app_transactions WHERE app_id='app-c'");
        Assert.InRange(storedEnvironment.Length, 1, 50);
        Assert.DoesNotContain("a@b.co", storedEnvironment);
        var filter = "?environment=" + Uri.EscapeDataString(storedEnvironment);
        using var filteredTransactions = await http.GetAsync("/api/v0/apps/app-c/transactions" + filter);
        Assert.Equal(HttpStatusCode.OK, filteredTransactions.StatusCode);
        using var transactionJson = JsonDocument.Parse(await filteredTransactions.Content.ReadAsStringAsync());
        Assert.Equal(1, transactionJson.RootElement.GetProperty("pagination").GetProperty("total").GetInt32());
        using var filteredIssues = await http.GetAsync("/api/v0/apps/app-c/issues" + filter);
        Assert.Equal(HttpStatusCode.OK, filteredIssues.StatusCode);
        using var issueJson = JsonDocument.Parse(await filteredIssues.Content.ReadAsStringAsync());
        Assert.Equal(2, issueJson.RootElement.GetProperty("pagination").GetProperty("total").GetInt32());
        await performance.Record("app-d", transaction, default);
        await store.Record(new TrackingError { AppId="app-d", ErrorId=Guid.NewGuid().ToString(), ErrorType="Other", ErrorMessage="Other", Timestamp=DateTime.UtcNow }, default);
        await connection.ExecuteAsync("UPDATE error_receipts SET received_at=CURRENT_TIMESTAMP - INTERVAL '2 days' WHERE app_id IN ('app-c','app-d'); UPDATE app_transactions SET received_at=CURRENT_TIMESTAMP - INTERVAL '2 days' WHERE app_id IN ('app-c','app-d')");
        await connection.ExecuteAsync("UPDATE error_outbox SET created_at=CURRENT_TIMESTAMP - INTERVAL '31 days' WHERE app_id='app-d'");
        await store.PurgeExpiredReceipts(default); await performance.Purge(default);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_outbox WHERE app_id='app-d'"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_receipts WHERE app_id='app-c'"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_receipts WHERE app_id='app-d'"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM app_transactions WHERE app_id='app-c'"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM app_transactions WHERE app_id='app-d'"));
        async Task<HttpResponseMessage> Clear(string confirm) => await http.SendAsync(new HttpRequestMessage(HttpMethod.Delete, route + "/data") { Content = JsonContent.Create(new { confirmAppId = confirm }) });
        using var preview = await http.GetAsync(route + "/data");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using var previewJson = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.True(previewJson.RootElement.GetProperty("excludesDeliveredRawLogs").GetBoolean());
        Assert.Equal(2, previewJson.RootElement.GetProperty("counts").GetProperty("issues").GetInt32());
        using var alertList = await http.GetAsync(route + "/alerts");
        using var alertJson = JsonDocument.Parse(await alertList.Content.ReadAsStringAsync());
        Assert.Equal(2, alertJson.RootElement.GetProperty("pagination").GetProperty("total").GetInt32());
        Assert.Contains(alertJson.RootElement.GetProperty("alerts").EnumerateArray(), a => a.GetProperty("kind").GetString() == "new_issue");
        Assert.Equal(HttpStatusCode.Conflict, (await Clear("app-c")).StatusCode);
        settings.CaptureErrors = false; settings.CapturePerformance = false;
        Assert.Equal(HttpStatusCode.OK, (await http.PutAsJsonAsync(route + "/settings", settings)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/v0/error", Error(Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/v0/transaction", transaction)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Clear("app-d")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Clear("app-c")).StatusCode);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_issues WHERE app_id='app-c'"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM monitoring_alerts WHERE app_id='app-c'"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_issues WHERE app_id='app-d'"));
        await connection.ExecuteAsync("UPDATE monitoring_alerts SET created_at=CURRENT_TIMESTAMP - INTERVAL '31 days'; UPDATE monitoring_alert_state SET last_notified_at=CURRENT_TIMESTAMP - INTERVAL '31 days'");
        await store.PurgeExpiredReceipts(default);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM monitoring_alerts"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM monitoring_alert_state"));
        http.DefaultRequestHeaders.Remove("Test-Owner");
    }

    private static async Task CheckSentry(HttpClient http, System.Data.Common.DbConnection connection, TestServer server)
    {
        const string route = "/api/v0/apps/app-b/sentry-projects";
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync(route, null)).StatusCode);
        http.DefaultRequestHeaders.Add("Test-Owner", "true");
        using var provision = await http.PostAsync(route, null);
        Assert.Equal(HttpStatusCode.OK, provision.StatusCode);
        using var projects = JsonDocument.Parse(await provision.Content.ReadAsStringAsync());
        var entries = projects.RootElement.GetProperty("projects").EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync(route, null)).StatusCode);
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sentry_projects"));
        http.DefaultRequestHeaders.Remove("Test-Owner");
        var projectId = entries[0].GetProperty("id").GetString();
        var dsn = entries[0].GetProperty("dsn").GetString();
        var endpoint = $"/api/{projectId}/envelope/";
        var eventId = Guid.NewGuid().ToString("N");
        string Envelope(string type = "event", string? extra = null) => JsonSerializer.Serialize(new { dsn, event_id = eventId }) + "\n" +
            JsonSerializer.Serialize(new { type }) + "\n" + JsonSerializer.Serialize(new {
                event_id = eventId, message = "Sentry owner@example.test", timestamp = DateTime.UtcNow,
                release = "2.0.0", environment = "desktop", user = new { email = "discard@example.test" },
                extra = new { secret = "must-not-store" }
            }) + "\n" + extra;
        Task<HttpResponseMessage> Send(string data, string suffix = "") => http.PostAsync(endpoint + suffix, new StringContent(data, Encoding.UTF8, "application/x-sentry-envelope"));
        var before = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_receipts");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(Envelope(extra: "{\"type\":\"attachment\"}\nprivate"))).StatusCode);
        Assert.Equal(before, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_receipts"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(Envelope("session"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(Envelope(), "?sentry_key=KEY-A")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send("{}\n{\"type\":\"event\",\"length\":\"wrong\"}\n{}" )).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(Envelope())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(Envelope())).StatusCode);
        Assert.Equal(before + 1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_receipts"));
        var retained = await connection.ExecuteScalarAsync<string>("SELECT row_to_json(r)::text FROM error_receipts r WHERE event_id=@id", new { id = Guid.Parse(eventId) });
        Assert.Contains("[EMAIL_REDACTED]", retained);
        Assert.DoesNotContain("must-not-store", retained);
        Assert.DoesNotContain("discard@example.test", retained);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_receipts WHERE app_id='app-a' AND event_id=@id", new { id = Guid.Parse(eventId) }));
        var transactionId = Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.Serialize(new { event_id = transactionId, type = "transaction", transaction = "app.startup", start_timestamp = 1800000000.0, timestamp = 1800000000.5,
            contexts = new { trace = new { span_id = "1111111111111111", op = "ui.load", status = "ok" } },
            spans = new[] { new { span_id = "2222222222222222", parent_span_id = "1111111111111111", op = "file.read", description = "private path", start_timestamp = 1800000000.1, timestamp = 1800000000.2 } } });
        var transactionEnvelope = JsonSerializer.Serialize(new { dsn }) + "\n{\"type\":\"transaction\"}\n" + payload;
        Assert.Equal(HttpStatusCode.OK, (await Send(transactionEnvelope)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(transactionEnvelope)).StatusCode);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM app_transactions WHERE event_id=@id", new { id = Guid.Parse(transactionId) }));
        using var zipped = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(zipped, System.IO.Compression.CompressionMode.Compress, true))
            gzip.Write(Encoding.UTF8.GetBytes(transactionEnvelope));
        using var content = new ByteArrayContent(zipped.ToArray());
        content.Headers.ContentEncoding.Add("gzip");
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync(endpoint, content)).StatusCode);
        var sdkResponses = new System.Collections.Concurrent.ConcurrentQueue<HttpStatusCode>();
        using (global::Sentry.SentrySdk.Init(options => {
            options.Dsn = dsn;
            options.Release = "sdk-probe-6.10.0";
            options.Environment = "test";
            options.SendDefaultPii = false;
            options.SendClientReports = false;
            options.AutoSessionTracking = false;
            options.TracesSampleRate = 1;
            options.DisableDiagnosticSourceIntegration();
            options.DisableAppDomainUnhandledExceptionCapture();
            options.DisableUnobservedTaskExceptionCapture();
            options.DisableAppDomainProcessExitFlush();
            // Actual SDK serialization/compression/auth, exclusively routed to the in-memory HTTP server.
            options.CreateHttpMessageHandler = () => new SdkResponseHandler(server.CreateHandler(), sdkResponses);
        }))
        {
            global::Sentry.SentrySdk.CaptureException(new InvalidOperationException("SDK test owner@example.test"));
            var transaction = global::Sentry.SentrySdk.StartTransaction("sdk.startup", "ui.load");
            var span = transaction.StartChild("file.read");
            span.Finish();
            transaction.Finish();
            await global::Sentry.SentrySdk.FlushAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(2, sdkResponses.Count);
        Assert.All(sdkResponses, status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_receipts WHERE release='sdk-probe-6.10.0'"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM app_transactions WHERE release='sdk-probe-6.10.0'"));
        await connection.ExecuteAsync("UPDATE apps SET capture_errors=false,capture_performance=false WHERE id='app-b'");
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(Envelope())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(transactionEnvelope)).StatusCode);
        using var huge = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(huge, System.IO.Compression.CompressionMode.Compress, true))
            gzip.Write(new byte[SentryEnvelopeParser.MaxBytes + 1]);
        using var hugeContent = new ByteArrayContent(huge.ToArray()); hugeContent.Headers.ContentEncoding.Add("gzip");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await http.PostAsync(endpoint, hugeContent)).StatusCode);
        await connection.ExecuteAsync("UPDATE apps SET deleted_at=CURRENT_TIMESTAMP WHERE id='app-b'");
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(Envelope())).StatusCode);
    }

    private sealed class SdkResponseHandler(HttpMessageHandler inner, System.Collections.Concurrent.ConcurrentQueue<HttpStatusCode> statuses) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("localhost", request.RequestUri!.Host);
            var response = await base.SendAsync(request, cancellationToken);
            statuses.Enqueue(response.StatusCode);
            return response;
        }
    }

    private class LocalErrorSink : IErrorIngestionClient
    {
        public bool FailAfterAccepting { get; set; }
        public bool PartialAcknowledgement { get; set; }
        public List<string> Received { get; } = new();
        public Task<long> BulkSendErrorAsync(IEnumerable<ErrorRow> rows, CancellationToken ct = default)
        {
            var batch = rows.ToArray();
            Received.AddRange(batch.Select(r => r.AppId + ":" + r.ErrorId));
            if (FailAfterAccepting) throw new IOException("Simulated lost sink acknowledgement");
            return Task.FromResult((long)(PartialAcknowledgement ? batch.Length - 1 : batch.Length));
        }
    }

    private class RejectAnalyticsQueries : IQueryClient
    {
        public Task<IEnumerable<T>> NamedQueryAsync<T>(string name, object args, CancellationToken cancellationToken) => throw new InvalidOperationException("Analytics reads are outside this ingestion test");
        public Task<T> NamedQuerySingleAsync<T>(string name, object args, CancellationToken cancellationToken) where T : new() => throw new InvalidOperationException("Analytics reads are outside this ingestion test");
        public Task<Stream> StreamResponseAsync(string query, CancellationToken cancellationToken) => throw new InvalidOperationException("Analytics reads are outside this ingestion test");
    }
}
