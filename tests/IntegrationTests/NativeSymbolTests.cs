using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Aptabase.Data;
using Aptabase.Features;
using Aptabase.Features.Apps;
using Aptabase.Features.ErrorReporting;
using Aptabase.Features.Ingestion;
using Aptabase.Features.Stats;
using Aptabase.Features.Symbolication;
using Dapper;
using FluentMigrator.Runner;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Npgsql;
using Xunit;

namespace Aptabase.IntegrationTests;

public class NativeSymbolTests
{
    [Fact]
    public async Task Artifacts_And_Text_Reports_Are_Private_Bounded_And_App_Build_Scoped()
    {
        var url = Environment.GetEnvironmentVariable("APTABASE_ISSUE_TEST_DATABASE_URL") ?? throw new InvalidOperationException("Isolated PostgreSQL required");
        var database = "aptabase_symbols_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(url); await admin.OpenAsync(); await admin.ExecuteAsync($"CREATE DATABASE {database}");
        try
        {
            url = new NpgsqlConnectionStringBuilder(url) { Database = database }.ConnectionString;
            await using var migrations = new ServiceCollection().AddFluentMigratorCore().ConfigureRunner(r => r.AddPostgres().WithGlobalConnectionString(url).ScanIn(typeof(Program).Assembly).For.Migrations()).BuildServiceProvider();
            using var scope = migrations.CreateScope(); scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
            DefaultTypeMap.MatchNamesWithUnderscores = true;
            await using var source = NpgsqlDataSource.Create(url); var db = new DbContext(source);
            await using var connection = db.Connection;
            await connection.ExecuteAsync("""
                INSERT INTO users(id,name,email) VALUES ('owner','Owner','owner@example.test'),('other','Other','other@example.test');
                INSERT INTO apps(id,owner_id,name,app_key,has_events,error_quota) VALUES
                    ('a','owner','A','KEY-A',true,100),('b','other','B','KEY-B',true,100);
                INSERT INTO app_shares(app_id,email) VALUES ('a','reader@example.test');
                """);
            var env = (EnvSettings)Activator.CreateInstance(typeof(EnvSettings), nonPublic: true)!;
            typeof(EnvSettings).GetProperty(nameof(EnvSettings.ErrorQuotaEnabled))!.SetValue(env, true);
            var processor = new TestProcessor();
            var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(env); builder.Services.AddSingleton<IDbContext>(db);
            builder.Services.AddSingleton<IIssueStore, IssueStore>(); builder.Services.AddSingleton<IPiiSanitizer, PiiSanitizer>();
            builder.Services.AddSingleton<ErrorIngestionService>(); builder.Services.AddSingleton<IAppQueries, AppQueries>();
            builder.Services.AddSingleton<IIngestionCache, IngestionCache>(); builder.Services.AddSingleton<IRejectionStats, RejectionStats>(); builder.Services.AddMemoryCache();
            builder.Services.AddSingleton<IErrorQueryClient>(new ClickHouseErrorQueryClient(new RejectAnalyticsQueries()));
            builder.Services.AddSingleton<INativeSymbolProcessor>(processor); builder.Services.AddSingleton<NativeSymbolStore>();
            builder.Services.AddControllers().AddApplicationPart(typeof(NativeSymbolsController).Assembly);
            builder.Services.AddCors(o => o.AddPolicy("AllowAny", p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
            builder.Services.AddRateLimiter(o => o.AddFixedWindowLimiter("EventIngestion", p => { p.PermitLimit = 1000; p.Window = TimeSpan.FromMinutes(1); }));
            builder.Services.AddRateLimiter(o => o.AddFixedWindowLimiter("Stats", p => { p.PermitLimit = 1000; p.Window = TimeSpan.FromMinutes(1); }));
            await using var app = builder.Build(); app.UseRouting(); app.UseCors(); app.UseRateLimiter();
            app.Use(async (c, next) => { if (c.Request.Headers.TryGetValue("Test-Role", out var role)) c.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("id", role.ToString()), new Claim("email", role + "@example.test") }, "test")); await next(); });
            app.MapControllers(); await app.StartAsync(); using var http = app.GetTestClient(); var store = app.Services.GetRequiredService<NativeSymbolStore>();
            const string route = "/api/v0/apps/a/native-symbols";
            var bytes = new byte[64]; new byte[] { 127, 69, 76, 70, 2, 1, 1 }.CopyTo(bytes, 0);
            bytes[16] = 3; bytes[18] = 183; bytes[20] = 1; bytes[52] = 64; bytes[58] = 64;
            async Task<HttpResponseMessage> Upload(string path = route, byte[]? body = null, string type = "application/octet-stream")
            { var content = new ByteArrayContent(body ?? bytes); content.Headers.ContentType = new(type); return await http.PostAsync(path, content); }
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(route)).StatusCode);
            http.DefaultRequestHeaders.Add("Test-Role", "reader");
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(route)).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await Upload()).StatusCode);
            http.DefaultRequestHeaders.Remove("Test-Role"); http.DefaultRequestHeaders.Add("Test-Role", "owner");
            Assert.Equal(HttpStatusCode.Forbidden, (await Upload("/api/v0/apps/b/native-symbols")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Upload(route + "?buildMode=bad")).StatusCode);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await Upload(type: "application/zip")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Upload(body: new byte[64])).StatusCode);
            var core = (byte[])bytes.Clone(); core[16] = 4;
            Assert.Equal(HttpStatusCode.BadRequest, (await Upload(body: core)).StatusCode);
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM native_symbols"));
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Upload(body: new byte[ElfSymbols.MaxBytes + 1])).StatusCode);
            processor.Enabled = false; Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Upload()).StatusCode); processor.Enabled = true;
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Upload()));
            Assert.All(concurrent, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM native_symbols"));
            var id = await connection.ExecuteScalarAsync<Guid>("SELECT id FROM native_symbols");
            Assert.Equal(HttpStatusCode.NotFound, (await http.DeleteAsync(route + "/" + id + "?buildMode=debug")).StatusCode);
            var listing = await http.GetStringAsync(route); Assert.DoesNotContain("content", listing, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("symbolIndex", listing);
            var build = new string('a', 40);
            var ingestion = app.Services.GetRequiredService<ErrorIngestionService>();
            var eventId = Guid.NewGuid(); var body = new ErrorBody { EventId = eventId.ToString(), ErrorType = "SIGSEGV", ErrorMessage = "Synthetic native failure", NativeFrames = new() { new(build, "0x1004") } };
            http.DefaultRequestHeaders.Add("App-Key", "KEY-A");
            Assert.True(body.IsValid());
            Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsJsonAsync("/api/v0/error", body)).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsJsonAsync("/api/v0/error", body)).StatusCode);
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT error_count FROM apps WHERE id='a'"));
            body.NativeFrames = new() { new(build, "0x1\n") };
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/v0/error", body)).StatusCode);
            body.NativeFrames = new() { new(build, "0x1004") };
            await connection.ExecuteAsync("UPDATE apps SET error_quota=1 WHERE id='a'");
            var rejectedId = Guid.NewGuid(); body.EventId = rejectedId.ToString();
            Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("/api/v0/error", body)).StatusCode);
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_receipts WHERE event_id=@rejectedId", new { rejectedId }));
            await connection.ExecuteAsync("UPDATE apps SET error_quota=100 WHERE id='a'");
            body.EventId = eventId.ToString();
            var issue = await connection.ExecuteScalarAsync<string>("SELECT issue_id FROM error_receipts WHERE event_id=@eventId", new { eventId });
            Assert.NotNull(issue);
            var framesRoute = $"/api/v0/apps/a/issues/{issue}/events/{eventId}/native-frames";
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(framesRoute.Replace("/apps/a/", "/apps/b/"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(framesRoute.Replace(issue, new string('0', 64)))).StatusCode);
            Assert.Contains("unresolved", await http.GetStringAsync(framesRoute));
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(framesRoute + "?buildMode=debug")).StatusCode);
            processor.Index = new(build, new[] { new NativeFunction(0x1000, 16, "native_function") });
            Assert.True(await store.ProcessOne(default)); Assert.False(await store.ProcessOne(default));
            Assert.Contains("native_function", await http.GetStringAsync(framesRoute));
            Assert.True((await app.Services.GetRequiredService<IIssueStore>().Events("a", false, issue, 0, 10, default)).Events.Single().HasNativeFrames);
            // The same build ID in another app or mode must never supply this report's symbols.
            await connection.ExecuteAsync("UPDATE native_symbols SET is_debug=true WHERE id=@id", new { id });
            Assert.Contains("unresolved", await http.GetStringAsync(framesRoute));
            await connection.ExecuteAsync("UPDATE native_symbols SET is_debug=false,app_id='b' WHERE id=@id", new { id });
            Assert.Contains("unresolved", await http.GetStringAsync(framesRoute));
            await connection.ExecuteAsync("UPDATE native_symbols SET app_id='a' WHERE id=@id", new { id });
            // Distinct artifacts claiming a build ID are explicitly ambiguous.
            var alternative = (byte[])bytes.Clone(); alternative[62] = 1;
            Assert.Equal(HttpStatusCode.Accepted, (await Upload(body: alternative)).StatusCode);
            await store.ProcessOne(default);
            Assert.Contains("ambiguous", await http.GetStringAsync(framesRoute));
            await connection.ExecuteAsync("DELETE FROM native_symbols WHERE id<>@id", new { id });
            http.DefaultRequestHeaders.Remove("Test-Role"); http.DefaultRequestHeaders.Add("Test-Role", "reader");
            Assert.Contains("native_function", await http.GetStringAsync(framesRoute));
            Assert.Equal(HttpStatusCode.Forbidden, (await http.DeleteAsync(route + "/" + id)).StatusCode);
            http.DefaultRequestHeaders.Remove("Test-Role"); http.DefaultRequestHeaders.Add("Test-Role", "owner");
            await connection.ExecuteAsync("UPDATE native_symbols SET expires_at=CURRENT_TIMESTAMP-INTERVAL '1 second'");
            Assert.Contains("unresolved", await http.GetStringAsync(framesRoute)); Assert.Equal(1, await store.Cleanup(default));
            Assert.Equal(issue, await connection.ExecuteScalarAsync<string>("SELECT issue_id FROM error_receipts WHERE event_id=@eventId", new { eventId }));
            processor.Index = null; Assert.Equal(HttpStatusCode.Accepted, (await Upload()).StatusCode); await store.ProcessOne(default);
            Assert.Contains("invalid", await http.GetStringAsync(route));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT octet_length(content) FROM native_symbols"));
            await connection.ExecuteAsync("DELETE FROM native_symbols");
            // App-row locking serializes the byte budget across both modes as well as the count budget.
            var large = new byte[ElfSymbols.MaxBytes]; bytes.CopyTo(large, 0);
            for (var i = 0; i < 4; i++)
            {
                large[^1] = (byte)i;
                Assert.Equal(HttpStatusCode.Accepted, (await Upload(route + (i % 2 == 0 ? "" : "?buildMode=debug"), large)).StatusCode);
            }
            Assert.Equal(HttpStatusCode.Conflict, (await Upload()).StatusCode);
            await connection.ExecuteAsync("UPDATE native_symbols SET created_at=CURRENT_TIMESTAMP-INTERVAL '2 days'; UPDATE apps SET monitoring_retention_days=1 WHERE id='a'");
            Assert.DoesNotContain("pending", await http.GetStringAsync(route));
            Assert.Equal(4, await store.Cleanup(default));
            await connection.ExecuteAsync("UPDATE apps SET monitoring_retention_days=30 WHERE id='a'");
            Assert.Equal(HttpStatusCode.Accepted, (await Upload()).StatusCode);
            processor.Gate = new TaskCompletionSource<NativeSymbolIndex?>();
            var firstJob = store.ProcessOne(default);
            await processor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(await store.ProcessOne(default));
            processor.Gate.SetResult(null);
            Assert.True(await firstJob);
            processor.Gate = null;
            await connection.ExecuteAsync("UPDATE native_symbols SET status='pending'");
            processor.Failure = new IOException("Synthetic worker transport failure");
            await Assert.ThrowsAsync<IOException>(() => store.ProcessOne(default));
            Assert.Equal("pending", await connection.ExecuteScalarAsync<string>("SELECT status FROM native_symbols"));
            processor.Failure = null;
            Assert.True(await store.ProcessOne(default));
            var preview = await http.GetFromJsonAsync<JsonElement>("/api/v0/apps/a/monitoring/data");
            Assert.Equal(1, preview.GetProperty("counts").GetProperty("symbols").GetInt32());
            await connection.ExecuteAsync("UPDATE apps SET capture_errors=false,capture_performance=false,forward_raw_errors=false WHERE id='a'");
            using var purge = new HttpRequestMessage(HttpMethod.Delete, "/api/v0/apps/a/monitoring/data") { Content = JsonContent.Create(new { confirmAppId = "a" }) };
            Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(purge)).StatusCode);
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM native_symbols"));
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(framesRoute)).StatusCode);
            await connection.ExecuteAsync("UPDATE apps SET capture_errors=true,capture_performance=true,forward_raw_errors=true WHERE id='a'");
            var uploads = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => { var value = (byte[])bytes.Clone(); value[63] = (byte)i; return Upload(body: value); }));
            Assert.Equal(8, uploads.Count(r => r.StatusCode == HttpStatusCode.Accepted)); Assert.Equal(4, uploads.Count(r => r.StatusCode == HttpStatusCode.Conflict));
            Assert.Equal(8, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM native_symbols"));
            await connection.ExecuteAsync("UPDATE apps SET deleted_at=CURRENT_TIMESTAMP WHERE id='a'"); Assert.Equal(8, await store.Cleanup(default));
            Assert.Equal(HttpStatusCode.Forbidden, (await Upload()).StatusCode);
        }
        finally { await admin.ExecuteAsync($"DROP DATABASE {database} WITH (FORCE)"); }
    }
    private class TestProcessor : INativeSymbolProcessor
    {
        public bool Enabled { get; set; } = true;
        public NativeSymbolIndex? Index { get; set; }
        public TaskCompletionSource<NativeSymbolIndex?>? Gate { get; set; }
        public Exception? Failure { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<NativeSymbolIndex?> Process(byte[] artifact, CancellationToken ct)
        {
            if (Failure is not null) return Task.FromException<NativeSymbolIndex?>(Failure);
            if (Gate is not null) { Entered.TrySetResult(); return Gate.Task.WaitAsync(ct); }
            return Task.FromResult(Index);
        }
    }
    private class RejectAnalyticsQueries : IQueryClient
    {
        public Task<IEnumerable<T>> NamedQueryAsync<T>(string name, object args, CancellationToken ct) => throw new InvalidOperationException("No analytics reads in symbol tests");
        public Task<T> NamedQuerySingleAsync<T>(string name, object args, CancellationToken ct) where T : new() => throw new InvalidOperationException("No analytics reads in symbol tests");
        public Task<Stream> StreamResponseAsync(string query, CancellationToken ct) => throw new InvalidOperationException("No analytics reads in symbol tests");
    }
}
