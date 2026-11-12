using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Security.Cryptography;
using Aptabase.Data;
using Aptabase.Features;
using Aptabase.IntegrationTests.Clients;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Aptabase.IntegrationTests;

[CollectionDefinition("Production configuration", DisableParallelization = true)]
public class ProductionConfigurationCollection;

[Collection("Production configuration")]
public class ProductionAcceptanceTests
{
    [Fact]
    public async Task Production_Login_Keys_Headers_And_Storage_Readiness_Survive_Restart()
    {
        var adminUrl = Environment.GetEnvironmentVariable("APTABASE_ISSUE_TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("Isolated PostgreSQL required");
        var database = "aptabase_production_" + Guid.NewGuid().ToString("N");
        var keyDirectory = Path.Combine(Path.GetTempPath(), "aptabase-production-keys-" + Guid.NewGuid().ToString("N"));
        var restoredKeys = keyDirectory + "-restored";
        await using var admin = new NpgsqlConnection(adminUrl);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE DATABASE {database}");
        var values = new Dictionary<string, string?> {
            ["ASPNETCORE_ENVIRONMENT"] = "Production", ["DOTNET_ENVIRONMENT"] = "Production", ["REGION"] = "SH",
            ["BASE_URL"] = "https://collector.example.test", ["AUTH_SECRET"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["DATABASE_URL"] = new NpgsqlConnectionStringBuilder(adminUrl) { Database = database }.ConnectionString,
            ["CLICKHOUSE_URL"] = Environment.GetEnvironmentVariable("APTABASE_TEST_CLICKHOUSE_URL") ?? throw new InvalidOperationException("Isolated ClickHouse required"),
            ["SMTP_HOST"] = "127.0.0.1", ["SMTP_PORT"] = Environment.GetEnvironmentVariable("APTABASE_TEST_SMTP_PORT") ?? throw new InvalidOperationException("Local mail catcher required"),
            ["SMTP_FROM_ADDRESS"] = "synthetic@example.test", ["SMTP_USERNAME"] = "", ["SMTP_PASSWORD"] = "",
            ["DATA_PROTECTION_KEYS_DIRECTORY"] = keyDirectory,
            ["ConnectionStrings__postgresdb"] = null, ["ConnectionStrings__clickhousedb"] = null, ["ConnectionStrings__mailcatcher"] = null,
            ["OAUTH_GITHUB_CLIENT_ID"] = "", ["OAUTH_GITHUB_CLIENT_SECRET"] = "", ["OAUTH_GOOGLE_CLIENT_ID"] = "", ["OAUTH_GOOGLE_CLIENT_SECRET"] = ""
        };
        var previous = values.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        foreach (var (key, value) in values) Environment.SetEnvironmentVariable(key, value);
        try
        {
            string cookie;
            string link;
            using (var factory = new ProductionFactory())
            using (var client = Client(factory))
            {
                Assert.False(factory.Services.GetRequiredService<EnvSettings>().IsBillingEnabled);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/readyz")).StatusCode);
                var email = "synthetic-" + Guid.NewGuid().ToString("N") + "@example.test";
                Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/_auth/register", new { name = "Synthetic Owner", email })).StatusCode);
                link = await MailCatcher.GetLinkSentTo(email);
                Assert.StartsWith("/api/_auth/continue?token=", link);
                using var login = await client.GetAsync(link);
                Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
                Assert.Equal("https://collector.example.test/", login.Headers.Location!.ToString());
                var header = Assert.Single(login.Headers.GetValues("Set-Cookie"));
                Assert.Contains("secure", header.ToLowerInvariant());
                Assert.Contains("httponly", header.ToLowerInvariant());
                Assert.Contains("samesite=strict", header.ToLowerInvariant());
                cookie = header.Split(';')[0];
                client.DefaultRequestHeaders.Add("Cookie", cookie);
                using var me = await client.GetAsync("/api/_auth/me");
                Assert.Equal(HttpStatusCode.OK, me.StatusCode);
                Assert.True(me.Headers.CacheControl!.NoStore);
                using var shell = await client.GetAsync("/");
                Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
                Assert.Contains("max-age=", Assert.Single(shell.Headers.GetValues("Strict-Transport-Security")));
                Assert.Equal("DENY", Assert.Single(shell.Headers.GetValues("X-Frame-Options")));
                Assert.Equal("nosniff", Assert.Single(shell.Headers.GetValues("X-Content-Type-Options")));
                Assert.True(shell.Headers.CacheControl!.NoStore);
                Assert.True(shell.Headers.Contains("Content-Security-Policy"));
                using var foreign = new HttpRequestMessage(HttpMethod.Get, "/api/_auth/me");
                foreign.Headers.Add("Origin", "https://foreign.example.test");
                using var foreignResponse = await client.SendAsync(foreign);
                Assert.False(foreignResponse.Headers.Contains("Access-Control-Allow-Origin"));
                var switchable = (SwitchableDbContext)factory.Services.GetRequiredService<IDbContext>();
                switchable.Fail = true;
                using var failed = await client.GetAsync("/readyz");
                Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
                Assert.Equal("Unhealthy", await failed.Content.ReadAsStringAsync());
                Assert.True(failed.Headers.CacheControl!.NoStore);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
                using (var stalledPostgres = new TcpListener(IPAddress.Loopback, 0))
                {
                    stalledPostgres.Start();
                    switchable.FailureConnectionString = $"Host=127.0.0.1;Port={((IPEndPoint)stalledPostgres.LocalEndpoint).Port};Username=synthetic;Timeout=30;Pooling=false;SSL Mode=Disable";
                    var timer = Stopwatch.StartNew();
                    var response = client.GetAsync("/readyz");
                    using var stalledClient = await stalledPostgres.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
                    Assert.Equal(HttpStatusCode.ServiceUnavailable, (await response.WaitAsync(TimeSpan.FromSeconds(8))).StatusCode);
                    Assert.InRange(timer.Elapsed.TotalSeconds, 1, 8);
                }
                switchable.Fail = false;
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/readyz")).StatusCode);
                var env = factory.Services.GetRequiredService<EnvSettings>();
                var actualClickHouse = env.ClickHouseConnectionString;
                typeof(EnvSettings).GetProperty(nameof(EnvSettings.ClickHouseConnectionString))!.SetValue(env, "Host=127.0.0.1;Port=1;Username=synthetic");
                try
                {
                    Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/readyz")).StatusCode);
                    using var stalledAnalytics = new TcpListener(IPAddress.Loopback, 0);
                    stalledAnalytics.Start();
                    typeof(EnvSettings).GetProperty(nameof(EnvSettings.ClickHouseConnectionString))!.SetValue(env,
                        $"Host=127.0.0.1;Port={((IPEndPoint)stalledAnalytics.LocalEndpoint).Port};Username=synthetic");
                    var timer = Stopwatch.StartNew();
                    var response = client.GetAsync("/readyz");
                    using var stalledClient = await stalledAnalytics.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
                    Assert.Equal(HttpStatusCode.ServiceUnavailable, (await response.WaitAsync(TimeSpan.FromSeconds(8))).StatusCode);
                    Assert.InRange(timer.Elapsed.TotalSeconds, 1, 8);
                }
                finally { typeof(EnvSettings).GetProperty(nameof(EnvSettings.ClickHouseConnectionString))!.SetValue(env, actualClickHouse); }
            }
            var keys = Directory.GetFiles(keyDirectory, "key-*.xml");
            Assert.NotEmpty(keys);
            Directory.CreateDirectory(restoredKeys);
            foreach (var key in keys) File.Copy(key, Path.Combine(restoredKeys, Path.GetFileName(key)));
            Environment.SetEnvironmentVariable("DATA_PROTECTION_KEYS_DIRECTORY", restoredKeys);
            using (var restarted = new ProductionFactory())
            using (var client = Client(restarted))
            {
                client.DefaultRequestHeaders.Add("Cookie", cookie);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/_auth/me")).StatusCode);
                using var replay = await client.GetAsync(link);
                Assert.EndsWith("/auth?error=used", replay.Headers.Location!.ToString());
                using var logout = await client.PostAsync("/api/_auth/signout", null);
                Assert.Contains("expires=", Assert.Single(logout.Headers.GetValues("Set-Cookie")).ToLowerInvariant());
                client.DefaultRequestHeaders.Remove("Cookie");
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/_auth/me")).StatusCode);
                client.DefaultRequestHeaders.Add("Cookie", "auth-session=invalid-synthetic");
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/_auth/me")).StatusCode);
            }
        }
        finally
        {
            foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value);
            await admin.ExecuteAsync($"DROP DATABASE {database} WITH (FORCE)");
            foreach (var directory in new[] { keyDirectory, restoredKeys })
            {
                if (!Directory.Exists(directory)) continue;
                // Only this test's generated key files; never recurse through framework/profile directories.
                foreach (var file in Directory.GetFiles(directory, "key-*.xml")) File.Delete(file);
                Directory.Delete(directory);
            }
        }
    }

    private static HttpClient Client(ProductionFactory factory) => factory.CreateClient(new WebApplicationFactoryClientOptions {
        BaseAddress = new Uri("https://collector.example.test"), AllowAutoRedirect = false, HandleCookies = false
    });

    private sealed class ProductionFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureTestServices(services => services.AddSingleton<IDbContext>(sp =>
                new SwitchableDbContext(new DbContext(sp.GetRequiredService<NpgsqlDataSource>()))));
        }
    }

    private sealed class SwitchableDbContext(IDbContext inner) : IDbContext
    {
        public bool Fail { get; set; }
        public string FailureConnectionString { get; set; } = "Host=127.0.0.1;Port=1;Username=synthetic;Timeout=1;Pooling=false";
        public DbConnection Connection => Fail
            ? new NpgsqlConnection(FailureConnectionString) : inner.Connection;
    }
}
