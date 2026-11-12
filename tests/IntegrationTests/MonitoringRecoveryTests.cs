using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aptabase.Data;
using Aptabase.Features;
using Aptabase.Features.Authentication;
using Aptabase.Features.ErrorReporting;
using Aptabase.Features.ErrorReporting.Buffer;
using Aptabase.Features.Sentry;
using Dapper;
using FluentMigrator.Runner;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Aptabase.IntegrationTests;

public class MonitoringRecoveryTests(ITestOutputHelper output)
{
    [AnalyticsRecoveryFact]
    public async Task ClickHouse_Backup_Restores_Analytics_Raw_Errors_Views_And_Ttl()
    {
        var container = Environment.GetEnvironmentVariable("APTABASE_ANALYTICS_RECOVERY_CONTAINER")!;
        Assert.Matches("\\A[a-f0-9]{64}\\z", container);
        var settings = new System.Data.Common.DbConnectionStringBuilder {
            ConnectionString = Environment.GetEnvironmentVariable("APTABASE_TEST_CLICKHOUSE_URL")!
        };
        Assert.Equal("127.0.0.1", settings["Host"]);
        using var inspected = JsonDocument.Parse(await Docker("inspect", container));
        var instance = inspected.RootElement[0];
        Assert.Equal("production-acceptance", instance.GetProperty("Config").GetProperty("Labels").GetProperty("aptabase.test").GetString());
        var binding = Assert.Single(instance.GetProperty("NetworkSettings").GetProperty("Ports").GetProperty("8123/tcp").EnumerateArray());
        Assert.Equal("127.0.0.1", binding.GetProperty("HostIp").GetString());
        Assert.Equal(settings["Port"].ToString(), binding.GetProperty("HostPort").GetString());
        var token = Guid.NewGuid().ToString("N");
        var original = "aptabase_backup_" + token;
        var restored = "aptabase_restore_" + token;
        var archive = "backups/aptabase-recovery-" + token + ".zip";
        var archiveFile = "/var/lib/clickhouse/backups/" + archive;
        await using var admin = new ClickHouse.Client.ADO.ClickHouseConnection(settings.ConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE DATABASE {original}");
        try
        {
            settings["Database"] = original;
            await using var source = new ClickHouse.Client.ADO.ClickHouseConnection(settings.ConnectionString);
            await source.OpenAsync();
            foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "etc", "clickhouse"), "*.sql").Order())
                await source.ExecuteAsync(await File.ReadAllTextAsync(path));
            await source.ExecuteAsync("""
                INSERT INTO events (app_id,timestamp,event_name,user_id,session_id,ttl)
                VALUES ('synthetic-app',now(),'startup','synthetic-user','synthetic-session',now()+INTERVAL 180 DAY)
                """);
            await source.ExecuteAsync("""
                INSERT INTO error_events (error_id,app_id,timestamp,error_message,error_type,stack_trace,severity,kind,ttl)
                VALUES ('synthetic-error','synthetic-app',now(),'Synthetic failure','ProbeError','at SyntheticProbe:353:17','error','unhandled',now()+INTERVAL 180 DAY)
                """);
            var eventsBefore = await source.ExecuteScalarAsync<string>("SELECT groupArray(tuple(*))::String FROM events");
            var errorsBefore = await source.ExecuteScalarAsync<string>("SELECT groupArray(tuple(*))::String FROM error_events");
            var backup = await admin.QuerySingleAsync<BackupStatus>($"BACKUP DATABASE {original} TO File('{archive}')");
            Assert.Equal("BACKUP_CREATED", backup.Status);
            var checksum = (await Docker("exec", container, "sha256sum", archiveFile)).Split(' ')[0];
            Assert.Matches("\\A[a-f0-9]{64}\\z", checksum);
            var restore = await admin.QuerySingleAsync<BackupStatus>($"RESTORE DATABASE {original} AS {restored} FROM File('{archive}')");
            Assert.Equal("RESTORED", restore.Status);
            settings["Database"] = restored;
            await using var recovered = new ClickHouse.Client.ADO.ClickHouseConnection(settings.ConnectionString);
            await recovered.OpenAsync();
            Assert.Equal(eventsBefore, await recovered.ExecuteScalarAsync<string>("SELECT groupArray(tuple(*))::String FROM events"));
            Assert.Equal(errorsBefore, await recovered.ExecuteScalarAsync<string>("SELECT groupArray(tuple(*))::String FROM error_events"));
            Assert.Equal(1, await recovered.ExecuteScalarAsync<long>("SELECT countMerge(events) FROM monthly_usage_v1"));
            Assert.Equal(1, await recovered.ExecuteScalarAsync<long>("SELECT countMerge(events_count) FROM sessions_live_v1"));
            foreach (var table in new[] { "events", "error_events" })
                Assert.Contains("TTL ttl", await recovered.ExecuteScalarAsync<string>($"SHOW CREATE TABLE {table}"));
            await admin.ExecuteAsync($"DROP DATABASE {original} SYNC");
            Assert.Equal(1, await recovered.ExecuteScalarAsync<long>("SELECT countMerge(events) FROM monthly_usage_v1"));
            output.WriteLine($"PASS synthetic ClickHouse archive SHA256={checksum}; analytics/raw-error rows, both views and TTL definitions restored independently.");
        }
        finally
        {
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {original} SYNC");
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {restored} SYNC");
            await Docker("exec", container, "rm", "-f", archiveFile);
        }
    }

    [MonitoringRecoveryFact]
    public async Task Postgres_Backup_Restores_Monitoring_Data_Identity_Permissions_And_Deduplication()
    {
        var container = Environment.GetEnvironmentVariable("APTABASE_RECOVERY_CONTAINER")!;
        Assert.Matches("\\A[a-f0-9]{64}\\z", container);
        var url = Environment.GetEnvironmentVariable("APTABASE_ISSUE_TEST_DATABASE_URL")!;
        var settings = new NpgsqlConnectionStringBuilder(url);
        Assert.Equal("127.0.0.1", settings.Host);
        Assert.InRange(settings.Port, 1024, 65535);
        using var inspected = JsonDocument.Parse(await Docker("inspect", container));
        var instance = inspected.RootElement[0];
        Assert.Equal("monitoring-recovery", instance.GetProperty("Config").GetProperty("Labels").GetProperty("aptabase.test").GetString());
        var binding = Assert.Single(instance.GetProperty("NetworkSettings").GetProperty("Ports").GetProperty("5432/tcp").EnumerateArray());
        Assert.Equal("127.0.0.1", binding.GetProperty("HostIp").GetString());
        Assert.Equal(settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), binding.GetProperty("HostPort").GetString());

        var token = Guid.NewGuid().ToString("N");
        var original = "aptabase_backup_" + token;
        var restored = "aptabase_restore_" + token;
        var archive = "/tmp/aptabase-recovery-" + token + ".dump";
        await using var admin = new NpgsqlConnection(url);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE DATABASE {original}");
        await admin.ExecuteAsync($"CREATE DATABASE {restored}");
        try
        {
            var originalUrl = new NpgsqlConnectionStringBuilder(url) { Database = original }.ConnectionString;
            await using var services = new ServiceCollection().AddFluentMigratorCore().ConfigureRunner(r =>
                r.AddPostgres().WithGlobalConnectionString(originalUrl).ScanIn(typeof(Program).Assembly).For.Migrations()).BuildServiceProvider();
            using (var scope = services.CreateScope()) scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
            Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
            await using var originalSource = NpgsqlDataSource.Create(originalUrl);
            var db = new DbContext(originalSource);
            await using var before = db.Connection;
            await before.ExecuteAsync("""
                INSERT INTO users (id,name,email) VALUES ('recovery-owner','Synthetic owner','owner@example.test');
                INSERT INTO apps (id,owner_id,name,app_key,has_events,forward_raw_errors,local_alerts_enabled,monitoring_retention_days)
                VALUES ('recovery-app','recovery-owner','Synthetic recovery','synthetic-key-a',false,false,true,7),
                       ('recovery-other','recovery-owner','Other synthetic app','synthetic-key-b',false,false,false,7);
                INSERT INTO app_shares (app_id,email) VALUES ('recovery-app','reader@example.test');
                INSERT INTO sentry_projects (app_id,is_debug) VALUES ('recovery-app',false),('recovery-app',true);
                INSERT INTO app_transactions
                    (app_id,is_debug,event_id,timestamp,name,operation,duration_ms,status,release,environment,platform,spans)
                VALUES ('recovery-app',false,'22222222-2222-2222-2222-222222222222',CURRENT_TIMESTAMP,
                    'synthetic.load','app.load',125,'ok','recovery@1','synthetic','csharp','[]');
                INSERT INTO native_symbols (id,app_id,is_debug,content_hash,content,size_bytes,build_id,status,symbol_index,expires_at)
                VALUES ('33333333-3333-3333-3333-333333333333','recovery-app',false,repeat('a',64),''::bytea,64,
                    repeat('b',40),'ready','{"functions":[]}',CURRENT_TIMESTAMP+INTERVAL '7 days');
                """);
            var store = new IssueStore(db);
            var report = new TrackingError {
                AppId = "recovery-app", ErrorId = "11111111-1111-1111-1111-111111111111", ErrorType = "SyntheticRecovery",
                ErrorMessage = "Synthetic restore event", StackTrace = "at SyntheticRecovery.Run (Recovery.cs) line 353:17",
                Timestamp = DateTime.UtcNow, Release = "recovery@1", Environment = "synthetic", SdkVersion = "6.8.0"
            };
            Assert.Equal(IssueRecordResult.Accepted, await store.Record(report, default));
            Assert.Equal(IssueRecordResult.Duplicate, await store.Record(report, default));
            var issueId = (await store.List("recovery-app", false, "all", 0, 10, default)).Issues.Single().Id;
            Assert.NotNull(await store.SetStatus("recovery-app", false, issueId, "resolved", "recovery-owner", default));
            var project = await before.QuerySingleAsync<SentryProject>("""
                SELECT p.id,p.app_id,p.is_debug,a.app_key FROM sentry_projects p JOIN apps a ON a.id=p.app_id WHERE p.is_debug=false
                """);
            var settingsEnv = (EnvSettings)Activator.CreateInstance(typeof(EnvSettings), nonPublic: true)!;
            var sanitizer = new PiiSanitizer();
            var healthStore = new SentryMonitoringStore(db, sanitizer, new ErrorIngestionService(store, sanitizer, settingsEnv), settingsEnv);
            var sessionTime = DateTime.UtcNow.AddMinutes(-1);
            sessionTime = new DateTime(sessionTime.Ticks / 10 * 10, DateTimeKind.Utc);
            var metadata = new SentryEnvelope(null, null, null, null,
                [new SentrySessionUpdate(Guid.NewGuid(), sessionTime, sessionTime.AddSeconds(5), 1, true, "exited", 0, 5, "recovery@1", "synthetic")],
                new SentryClientReport(sessionTime, [new SentryDiscardCounter("before_send", "error", 2)]));
            Assert.Equal(SentryWriteResult.Accepted, await healthStore.Ingest(project, metadata, default));
            var keyBefore = SentryIngestionKey.Create(project);
            var tablesBefore = await TableHashes(before);

            await Docker("exec", container, "pg_dump", "-U", "postgres", "-Fc", "--no-owner", "--no-acl", "-f", archive, original);
            var checksum = (await Docker("exec", container, "sha256sum", archive)).Split(' ')[0];
            Assert.Matches("\\A[a-f0-9]{64}\\z", checksum);
            await Docker("exec", container, "pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-acl", "-d", restored, archive);
            await using var restoredSource = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(url) { Database = restored }.ConnectionString);
            var recoveredDb = new DbContext(restoredSource);
            await using var after = recoveredDb.Connection;
            Assert.Equal(tablesBefore, await TableHashes(after));
            var recoveredStore = new IssueStore(recoveredDb);
            var issue = await recoveredStore.Get("recovery-app", false, issueId, default);
            Assert.Equal("resolved", issue!.Status);
            Assert.Equal(1, issue.OccurrenceCount);
            Assert.Equal(report.StackTrace, issue.StackTrace);
            Assert.Equal(IssueRecordResult.Duplicate, await recoveredStore.Record(report, default));
            Assert.Equal(new IssueAccess(true, true), await recoveredStore.GetAccess("recovery-app", new("recovery-owner", "Owner", "owner@example.test"), default));
            Assert.Equal(new IssueAccess(true, false), await recoveredStore.GetAccess("recovery-app", new("reader", "Reader", "reader@example.test"), default));
            Assert.False((await recoveredStore.GetAccess("recovery-other", new("reader", "Reader", "reader@example.test"), default)).CanRead);
            Assert.False((await recoveredStore.GetAccess("recovery-app", new("stranger", "Stranger", "stranger@example.test"), default)).CanRead);
            var projectAfter = await after.QuerySingleAsync<SentryProject>("""
                SELECT p.id,p.app_id,p.is_debug,a.app_key FROM sentry_projects p JOIN apps a ON a.id=p.app_id WHERE p.is_debug=false
                """);
            Assert.Equal(keyBefore, SentryIngestionKey.Create(projectAfter));
            var recoveredHealth = new SentryMonitoringStore(recoveredDb, sanitizer,
                new ErrorIngestionService(recoveredStore, sanitizer, settingsEnv), settingsEnv);
            Assert.Equal(SentryWriteResult.Accepted, await recoveredHealth.Ingest(projectAfter, metadata, default));
            Assert.Equal(1, await after.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM monitoring_sessions"));
            Assert.Equal(1, await after.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM monitoring_client_reports"));
            Assert.Equal(1, await after.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM monitoring_alerts"));
            Assert.Equal(1, await after.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM app_transactions"));
            Assert.Equal(1, await after.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM native_symbols WHERE octet_length(content)=0"));
            Assert.Equal(7, await after.ExecuteScalarAsync<int>("SELECT monitoring_retention_days FROM apps WHERE id='recovery-app'"));
            output.WriteLine($"PASS synthetic PostgreSQL archive SHA256={checksum}; {tablesBefore.Count} public tables matched; permissions, DSN identity, resolved state and deduplication retained.");
        }
        finally
        {
            await admin.ExecuteAsync($"DROP DATABASE {original} WITH (FORCE)");
            await admin.ExecuteAsync($"DROP DATABASE {restored} WITH (FORCE)");
            await Docker("exec", container, "rm", "-f", archive);
        }
    }

    private sealed class BackupStatus
    {
        public string Status { get; set; } = "";
    }

    private static async Task<SortedDictionary<string, string>> TableHashes(System.Data.Common.DbConnection connection)
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var names = await connection.QueryAsync<string>("SELECT tablename FROM pg_tables WHERE schemaname='public' ORDER BY tablename");
        foreach (var name in names)
        {
            var quoted = new NpgsqlCommandBuilder().QuoteIdentifier(name);
            var json = await connection.ExecuteScalarAsync<string>($"SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text),'[]'::jsonb)::text FROM public.{quoted} t");
            hashes.Add(name, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json!))));
        }
        return hashes;
    }

    private static async Task<string> Docker(params string[] args)
    {
        var start = new ProcessStartInfo("docker") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await stderr);
        return await stdout;
    }
}

public sealed class MonitoringRecoveryFactAttribute : FactAttribute
{
    public MonitoringRecoveryFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APTABASE_RECOVERY_CONTAINER")))
            Skip = "Requires an explicitly labelled disposable PostgreSQL container for backup/restore acceptance.";
    }
}

public sealed class AnalyticsRecoveryFactAttribute : FactAttribute
{
    public AnalyticsRecoveryFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APTABASE_ANALYTICS_RECOVERY_CONTAINER")))
            Skip = "Requires an explicitly labelled disposable ClickHouse container with local backup support.";
    }
}
