using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Aptabase.Data;
using Aptabase.Features;
using Aptabase.Features.ErrorReporting;
using Aptabase.Features.Ingestion;
using Aptabase.Features.Sentry;
using Dapper;
using FluentMigrator.Runner;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Npgsql;
using Xunit;

namespace Aptabase.IntegrationTests;

public class SentrySessionTests
{
    [Fact]
    public async Task Mixed_Envelopes_Preserve_Errors_And_Session_Health_Atomically_With_Bounded_Private_Storage()
    {
        var adminUrl = Environment.GetEnvironmentVariable("APTABASE_ISSUE_TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("An isolated PostgreSQL test instance is required");
        var database = "aptabase_sessions_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(adminUrl);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE DATABASE {database}");
        try
        {
            var url = new NpgsqlConnectionStringBuilder(adminUrl) { Database = database }.ConnectionString;
            await using var migrations = new ServiceCollection().AddFluentMigratorCore().ConfigureRunner(r =>
                r.AddPostgres().WithGlobalConnectionString(url).ScanIn(typeof(Program).Assembly).For.Migrations()).BuildServiceProvider();
            using (var scope = migrations.CreateScope()) scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
            Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
            await using var source = NpgsqlDataSource.Create(url);
            var db = new DbContext(source);
            await using var connection = db.Connection;
            await connection.ExecuteAsync("""
                INSERT INTO users(id,name,email) VALUES ('owner','Owner','owner@example.test');
                INSERT INTO apps(id,owner_id,name,app_key,has_events,forward_raw_errors,error_quota)
                VALUES ('session-a','owner','A','SYNTHETIC-A',false,false,100),('session-b','owner','B','SYNTHETIC-B',false,false,100);
                INSERT INTO app_shares(app_id,email) VALUES ('session-a','reader@example.test');
                INSERT INTO sentry_projects(app_id,is_debug) VALUES ('session-a',false),('session-a',true),('session-b',false);
                """);
            var projects = (await connection.QueryAsync<SentryProject>("SELECT p.id,p.app_id,p.is_debug,a.app_key FROM sentry_projects p JOIN apps a ON a.id=p.app_id")).ToArray();
            var release = projects.Single(p => p.AppId == "session-a" && !p.IsDebug);
            var debug = projects.Single(p => p.AppId == "session-a" && p.IsDebug);
            var other = projects.Single(p => p.AppId == "session-b");
            var env = (EnvSettings)Activator.CreateInstance(typeof(EnvSettings), nonPublic: true)!;
            typeof(EnvSettings).GetProperty(nameof(EnvSettings.ErrorQuotaEnabled))!.SetValue(env, true);
            // Other production/login fixtures mutate process environment; keep exception assertions independent of test order.
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(env);
            builder.Services.AddSingleton<IDbContext>(db);
            builder.Services.AddSingleton<IIssueStore, IssueStore>();
            builder.Services.AddSingleton<IPiiSanitizer, PiiSanitizer>();
            builder.Services.AddSingleton<ErrorIngestionService>();
            builder.Services.AddSingleton<SentryMonitoringStore>();
            builder.Services.AddSingleton<IRejectionStats, RejectionStats>();
            builder.Services.AddControllers().AddApplicationPart(typeof(SentryController).Assembly);
            builder.Services.AddCors(o => o.AddPolicy("SentryIngestion", p => p.AllowAnyOrigin().AllowAnyHeader().WithMethods("POST")));
            builder.Services.AddRateLimiter(o => {
                o.AddFixedWindowLimiter("EventIngestion", p => { p.PermitLimit = 1000; p.Window = TimeSpan.FromSeconds(1); });
                o.AddFixedWindowLimiter("Stats", p => { p.PermitLimit = 1000; p.Window = TimeSpan.FromSeconds(1); });
            });
            await using var app = builder.Build();
            app.UseRouting(); app.UseCors(); app.UseRateLimiter();
            app.Use(async (context, next) => {
                if (context.Request.Headers.TryGetValue("Test-User", out var role))
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("id", role.ToString()),
                        new Claim("email", role + "@example.test") }, "test"));
                await next();
            });
            app.MapControllers(); await app.StartAsync();
            using var http = app.GetTestClient();
            http.DefaultRequestHeaders.Add("Test-User", "owner");
            var started = DateTime.UtcNow.AddMinutes(-5);
            object Session(Guid id, string status = "ok", bool init = false, long seq = 0, long errors = 0, string version = "release@1") => new {
                sid = id.ToString("N"), started, timestamp = started.AddSeconds(seq), seq, init, status, errors, duration = (double)seq,
                did = "private-device", attrs = new { release = version, environment = "synthetic", ip_address = "192.0.2.1", user_agent = "private-agent" }
            };
            object Error(Guid id) => new { event_id = id.ToString("N"), message = "Synthetic mixed event", release = "release@1", environment = "synthetic" };
            object Report(int seconds = 0) => new { timestamp = started.AddSeconds(seconds), discarded_events = new[] {
                new { reason = "before_send", category = "error", quantity = 3 }, new { reason = "queue_overflow", category = "error", quantity = 2 }
            } };
            async Task<HttpResponseMessage> Send(SentryProject project, params (string Type, object Body)[] items)
            {
                var wire = "{}\n" + string.Concat(items.Select(i => {
                    var payload = JsonSerializer.Serialize(i.Body);
                    return JsonSerializer.Serialize(new { type = i.Type, length = Encoding.UTF8.GetByteCount(payload) }) + "\n" + payload + "\n";
                }));
                return await http.PostAsync($"/api/{project.Id}/envelope/?sentry_key={SentryIngestionKey.Create(project)}", new StringContent(wire, Encoding.UTF8, "application/x-sentry-envelope"));
            }
            async Task<JsonElement> Health(string mode = "release")
            {
                var response = await http.GetAsync("/api/v0/apps/session-a/monitoring/health?buildMode=" + mode);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
            }

            var crashed = Guid.NewGuid(); var eventId = Guid.NewGuid();
            Assert.Equal(HttpStatusCode.OK, (await Send(release, ("event", Error(eventId)), ("session", Session(crashed, "crashed", false, 2, 1)), ("client_report", Report()))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Send(release, ("event", Error(eventId)), ("session", Session(crashed, "crashed", false, 2, 1)), ("client_report", Report()))).StatusCode);
            var incomplete = (await Health()).GetProperty("sessions");
            Assert.Equal(1, incomplete.GetProperty("observed").GetInt64());
            Assert.Equal(JsonValueKind.Null, incomplete.GetProperty("crashFreeRate").ValueKind);
            // Late init and duplicate updates must not reset terminal crash evidence or cumulative errors.
            var retries = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Send(release, ("session", Session(crashed, init: true)))));
            Assert.All(retries, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            Assert.Equal(HttpStatusCode.OK, (await Send(release, ("session", Session(crashed, "exited", false, 3)))).StatusCode);
            var exited = Guid.NewGuid(); var abnormal = Guid.NewGuid(); var active = Guid.NewGuid();
            Assert.Equal(HttpStatusCode.OK, (await Send(release, ("session", Session(exited, "exited", true, 1)),
                ("session", Session(abnormal, "abnormal", true, 1)), ("session", Session(active, init: true)))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Send(release, ("session", Session(abnormal, "exited", false, 10)),
                ("session", Session(exited, "ok", false, 10)))).StatusCode);
            var health = await Health(); var counts = health.GetProperty("sessions");
            Assert.Equal(4, counts.GetProperty("observed").GetInt64());
            Assert.Equal(2, counts.GetProperty("eligible").GetInt64());
            Assert.Equal(2, counts.GetProperty("incomplete").GetInt64());
            Assert.Equal(1, counts.GetProperty("crashed").GetInt64());
            Assert.Equal(50, counts.GetProperty("crashFreeRate").GetDouble());
            Assert.Equal(1, health.GetProperty("clientReports").GetProperty("reports").GetInt64());
            Assert.Equal(5, health.GetProperty("clientReports").GetProperty("discardedEvents").GetInt64());
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_receipts"));
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT occurrence_count FROM error_issues"));
            var stored = await connection.ExecuteScalarAsync<string>("SELECT jsonb_agg(to_jsonb(s))::text FROM monitoring_sessions s");
            Assert.DoesNotContain(crashed.ToString("N"), stored!);
            Assert.DoesNotContain("private", stored!);
            Assert.DoesNotContain("192.0.2.1", stored!);
            Assert.DoesNotContain("sessionKey", health.ToString());
            foreach (var project in new[] { debug, other }) Assert.Equal(HttpStatusCode.OK, (await Send(project, ("session", Session(crashed, "exited", true, 1)))).StatusCode);
            var forged = new SentryProject { Id = other.Id, AppId = other.AppId, IsDebug = false, AppKey = release.AppKey };
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(forged, ("session", Session(Guid.NewGuid(), init: true)))).StatusCode);
            Assert.Equal(100, (await Health("debug")).GetProperty("sessions").GetProperty("crashFreeRate").GetDouble());
            Assert.Equal(3, await connection.ExecuteScalarAsync<int>("SELECT COUNT(DISTINCT session_key) FROM monitoring_sessions WHERE saw_init AND status IN ('crashed','exited') AND session_key NOT IN (SELECT session_key FROM monitoring_sessions WHERE errors=0 AND app_id='session-a' AND is_debug=false)"));

            var rejectedError = Guid.NewGuid();
            Assert.Equal(HttpStatusCode.Conflict, (await Send(release, ("event", Error(rejectedError)), ("session", Session(crashed, version: "conflicting@2")))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await Send(release, ("session", Session(Guid.NewGuid(), init: true)),
                ("session", Session(crashed, version: "conflicting@2")), ("client_report", Report(1)))).StatusCode);
            Assert.Equal(5, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM monitoring_sessions WHERE app_id='session-a'"));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_receipts WHERE event_id=@id", new { id = rejectedError }));
            Assert.Equal(HttpStatusCode.BadRequest, (await Send(release, ("event", Error(rejectedError)), ("session", new { sid = "bad" }))).StatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(release, ("event", Error(rejectedError)), ("attachment", new { secret = "private" }))).StatusCode);
            await connection.ExecuteAsync("""
                CREATE FUNCTION synthetic_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic failure'; END; $$;
                CREATE TRIGGER synthetic_failure BEFORE INSERT ON error_receipts FOR EACH ROW EXECUTE FUNCTION synthetic_failure();
                """);
            try
            {
                await Assert.ThrowsAsync<PostgresException>(() => Send(release, ("event", Error(rejectedError)),
                    ("session", Session(Guid.NewGuid(), init: true)), ("client_report", Report(1))));
            }
            finally { await connection.ExecuteAsync("DROP TRIGGER synthetic_failure ON error_receipts; DROP FUNCTION synthetic_failure()"); }
            Assert.Equal(5, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM monitoring_sessions WHERE app_id='session-a'"));
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM monitoring_client_reports"));
            Assert.Contains(app.Services.GetRequiredService<IRejectionStats>().Snapshot().Entries, e => e.Key.Reason == "sentry_backend_failure");
            await connection.ExecuteAsync("UPDATE apps SET error_quota=error_count WHERE id='session-a'");
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(release, ("event", Error(rejectedError)), ("session", Session(Guid.NewGuid(), init: true)), ("client_report", Report(1)))).StatusCode);
            Assert.Equal(5, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM monitoring_sessions WHERE app_id='session-a'"));
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM monitoring_client_reports"));
            await connection.ExecuteAsync("UPDATE apps SET error_quota=100 WHERE id='session-a'");

            http.DefaultRequestHeaders.Remove("Test-User");
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/v0/apps/session-a/monitoring/health")).StatusCode);
            http.DefaultRequestHeaders.Add("Test-User", "stranger");
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/v0/apps/session-a/monitoring/health")).StatusCode);
            http.DefaultRequestHeaders.Remove("Test-User"); http.DefaultRequestHeaders.Add("Test-User", "reader");
            await Health();
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/v0/apps/session-b/monitoring/health")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/v0/apps/session-a/monitoring/data")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/v0/apps/session-a/monitoring/health?buildMode=invalid")).StatusCode);
            http.DefaultRequestHeaders.Remove("Test-User"); http.DefaultRequestHeaders.Add("Test-User", "owner");

            await CheckQuotaAndRetention(connection, http, release, Send, Session, Error, Report, Health, db, eventId);
            await connection.ExecuteAsync("UPDATE apps SET capture_errors=true WHERE id='session-a'");
            var firstReportEvent = Guid.NewGuid(); var secondReportEvent = Guid.NewGuid();
            foreach (var id in new[] { firstReportEvent, secondReportEvent })
                Assert.Equal(HttpStatusCode.OK, (await Send(release, ("event", Error(id)), ("client_report", Report(6)))).StatusCode);
            // Distinct accompanying event IDs disambiguate otherwise identical report content.
            Assert.Equal(3, (await Health()).GetProperty("clientReports").GetProperty("reports").GetInt64());
            var beforeReplay = await connection.ExecuteScalarAsync<string>("SELECT jsonb_agg(to_jsonb(r) ORDER BY report_key)::text FROM monitoring_client_reports r");
            var repeatedReports = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Send(release, ("event", Error(firstReportEvent)), ("client_report", Report(6)))));
            Assert.All(repeatedReports, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            Assert.Equal(beforeReplay, await connection.ExecuteScalarAsync<string>("SELECT jsonb_agg(to_jsonb(r) ORDER BY report_key)::text FROM monitoring_client_reports r"));
            Assert.Equal(HttpStatusCode.OK, (await Send(release, ("client_report", Report(6)))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Send(release, ("client_report", Report(6)))).StatusCode);
            Assert.Equal(4, (await Health()).GetProperty("clientReports").GetProperty("reports").GetInt64());
            Assert.Equal(20, (await Health()).GetProperty("clientReports").GetProperty("discardedEvents").GetInt64());

            var rotatedSession = Guid.NewGuid();
            Assert.Equal(HttpStatusCode.OK, (await Send(release, ("session", Session(rotatedSession, init: true)))).StatusCode);
            await connection.ExecuteAsync("UPDATE apps SET app_key='ROTATED-SYNTHETIC' WHERE id='session-a'");
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(release, ("session", Session(rotatedSession, "exited", false, 1)))).StatusCode);
            var rotatedProject = new SentryProject { Id = release.Id, AppId = release.AppId, IsDebug = false, AppKey = "ROTATED-SYNTHETIC" };
            Assert.Equal(HttpStatusCode.OK, (await Send(rotatedProject, ("session", Session(rotatedSession, "exited", false, 1)))).StatusCode);
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM monitoring_sessions WHERE app_id='session-a'"));
            Assert.Equal(100, (await Health()).GetProperty("sessions").GetProperty("crashFreeRate").GetDouble());
            var transactionId = Guid.NewGuid(); var transactionSession = Guid.NewGuid();
            object Transaction(Guid id) => new { event_id = id.ToString("N"), type = "transaction", transaction = "synthetic.startup",
                start_timestamp = started, timestamp = started.AddMilliseconds(100), release = "release@1", environment = "synthetic",
                contexts = new { trace = new { span_id = "1111111111111111", op = "app.startup", status = "ok" } } };
            var transactionBatch = new[] { ("transaction", Transaction(transactionId)), ("session", Session(transactionSession, "exited", true, 1)), ("client_report", Report(7)) };
            foreach (var _ in Enumerable.Range(0, 2))
                Assert.Equal(HttpStatusCode.OK, (await Send(rotatedProject, transactionBatch)).StatusCode);
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM app_transactions WHERE event_id=@transactionId", new { transactionId }));
            Assert.Equal(2, (await Health()).GetProperty("sessions").GetProperty("observed").GetInt64());
            Assert.Equal(5, (await Health()).GetProperty("clientReports").GetProperty("reports").GetInt64());
            var rejectedTransaction = Guid.NewGuid();
            Assert.Equal(HttpStatusCode.Conflict, (await Send(rotatedProject, ("transaction", Transaction(rejectedTransaction)),
                ("session", Session(transactionSession, version: "conflicting@2")), ("client_report", Report(8)))).StatusCode);
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM app_transactions WHERE event_id=@rejectedTransaction", new { rejectedTransaction }));
            Assert.Equal(5, (await Health()).GetProperty("clientReports").GetProperty("reports").GetInt64());
            await connection.ExecuteAsync("""
                INSERT INTO monitoring_sessions(app_id,is_debug,session_key,started_at,updated_at,sequence,saw_init,status,errors,duration_seconds,release,environment)
                SELECT 'session-a',false,md5('group-' || g::text),@started,@started,1,true,'exited',0,1,'group-' || g::text,'synthetic'
                FROM generate_series(1,100) g;
                """, new { started });
            var truncated = await Health();
            Assert.True(truncated.GetProperty("groupsTruncated").GetBoolean());
            Assert.Equal(100, truncated.GetProperty("groups").GetArrayLength());
            Assert.Equal(102, truncated.GetProperty("sessions").GetProperty("observed").GetInt64());
            Assert.Equal(100, truncated.GetProperty("sessions").GetProperty("crashFreeRate").GetDouble());
            await app.StopAsync();
        }
        finally { await admin.ExecuteAsync($"DROP DATABASE {database} WITH (FORCE)"); }
    }

    private static async Task CheckQuotaAndRetention(System.Data.Common.DbConnection connection, HttpClient http, SentryProject project,
        Func<SentryProject, (string Type, object Body)[], Task<HttpResponseMessage>> send,
        Func<Guid, string, bool, long, long, string, object> session, Func<Guid, object> error, Func<int, object> report,
        Func<string, Task<JsonElement>> health, IDbContext db, Guid originalEventId)
    {
        await connection.ExecuteAsync("""
            INSERT INTO monitoring_sessions(app_id,is_debug,session_key,started_at,updated_at,sequence,saw_init,status,errors,duration_seconds,release,environment)
            SELECT 'session-a',false,md5(g::text),CURRENT_TIMESTAMP,CURRENT_TIMESTAMP,0,false,'ok',0,0,'quota','synthetic'
            FROM generate_series(1,10000-(SELECT COUNT(*)::integer FROM monitoring_sessions WHERE app_id='session-a')) g;
            """);
        var eventId = Guid.NewGuid();
        var full = await send(project, [("event", error(eventId)), ("session", session(Guid.NewGuid(), "ok", true, 0, 0, "release@1"))]);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, full.StatusCode);
        Assert.Equal("60", full.Headers.GetValues("Retry-After").Single());
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_receipts WHERE event_id=@eventId", new { eventId }));
        await connection.ExecuteAsync("DELETE FROM monitoring_sessions WHERE release='quota'");
        await connection.ExecuteAsync("""
            INSERT INTO monitoring_client_reports(app_id,is_debug,report_key,reported_at,release,environment,counters)
            SELECT 'session-a',false,md5(g::text),CURRENT_TIMESTAMP,'quota','synthetic','[]'
            FROM generate_series(1,9999) g;
            """);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await send(project, [("event", error(eventId)), ("client_report", report(2))])).StatusCode);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_receipts WHERE event_id=@eventId", new { eventId }));
        Assert.Equal(HttpStatusCode.OK, (await send(project, [("event", error(originalEventId)), ("client_report", report(0))])).StatusCode);
        await connection.ExecuteAsync("DELETE FROM monitoring_client_reports WHERE release='quota'");
        await connection.ExecuteAsync("""
            UPDATE apps SET monitoring_retention_days=1 WHERE id='session-a';
            UPDATE monitoring_sessions SET received_at=CURRENT_TIMESTAMP-INTERVAL '2 days' WHERE app_id='session-a';
            UPDATE monitoring_client_reports SET received_at=CURRENT_TIMESTAMP-INTERVAL '2 days' WHERE app_id='session-a';
            """);
        Assert.Equal(0, (await health("release")).GetProperty("sessions").GetProperty("observed").GetInt64());
        await new IssueStore(db).PurgeExpiredReceipts(default);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM monitoring_sessions WHERE app_id='session-a'"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM monitoring_sessions WHERE app_id='session-b'"));
        Assert.Equal(HttpStatusCode.OK, (await send(project, [("session", session(Guid.NewGuid(), "exited", true, 1, 0, "release@1")), ("client_report", report(3))])).StatusCode);
        var preview = JsonDocument.Parse(await http.GetStringAsync("/api/v0/apps/session-a/monitoring/data"));
        Assert.Equal(1, preview.RootElement.GetProperty("counts").GetProperty("sessions").GetInt64());
        Assert.Equal(1, preview.RootElement.GetProperty("counts").GetProperty("clientReports").GetInt64());
        await connection.ExecuteAsync("UPDATE apps SET capture_errors=false,capture_performance=false,forward_raw_errors=false WHERE id='session-a'");
        Assert.Equal(HttpStatusCode.Forbidden, (await send(project, [("client_report", report(4))])).StatusCode);
        var purge = new HttpRequestMessage(HttpMethod.Delete, "/api/v0/apps/session-a/monitoring/data") {
            Content = new StringContent("{\"confirmAppId\":\"session-a\"}", Encoding.UTF8, "application/json")
        };
        Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(purge)).StatusCode);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM monitoring_client_reports WHERE app_id='session-a'"));
        Assert.Null(await connection.ExecuteScalarAsync<byte[]?>("SELECT monitoring_session_salt FROM apps WHERE id='session-a'"));
        await connection.ExecuteAsync("UPDATE apps SET capture_performance=true WHERE id='session-a'");
        Assert.Equal(HttpStatusCode.OK, (await send(project, [("client_report", report(5))])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await send(project, [("session", session(Guid.NewGuid(), "ok", true, 0, 0, "release@1"))])).StatusCode);
        Assert.Equal(1, (await health("release")).GetProperty("clientReports").GetProperty("reports").GetInt64());
        var filtered = JsonDocument.Parse(await http.GetStringAsync("/api/v0/apps/session-a/monitoring/health?release=release%401"));
        Assert.Equal(0, filtered.RootElement.GetProperty("clientReports").GetProperty("reports").GetInt64());
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/v0/apps/session-a/monitoring/health?startDate=2026-01-01&endDate=2026-04-01")).StatusCode);
    }
}
