using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sentry;
using Sentry.Protocol;
using Sentry.Protocol.Envelopes;
using Xunit;

public class CollectorReplayTests
{
    [CollectorFact]
    public async Task Sdk68_Cached_Unhandled_Envelope_And_Crashed_Session_Replay_Atomically_Without_Duplicating()
    {
        var origin = new Uri(Environment.GetEnvironmentVariable("APTABASE_SDK_TEST_COLLECTOR")!);
        Assert.Equal("http://127.0.0.1:55450/", origin.AbsoluteUri);
        using var api = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) {
            BaseAddress = origin, Timeout = TimeSpan.FromSeconds(5)
        };
        (await api.PostAsync("test/login/owner", null)).EnsureSuccessStatusCode();
        var projectsResponse = await api.PostAsync("api/v0/apps/test-app/sentry-projects", null);
        projectsResponse.EnsureSuccessStatusCode();
        var projects = JsonDocument.Parse(await projectsResponse.Content.ReadAsStringAsync()).RootElement;
        var dsn = projects.GetProperty("projects").EnumerateArray().Single(p => p.GetProperty("buildMode").GetString() == "release").GetProperty("dsn").GetString()!;
        Assert.Matches("\\Ahttp://[a-f0-9]{64}@127\\.0\\.0\\.1:55450/[1-9][0-9]*\\z", dsn);
        var settings = await api.GetFromJsonAsync<JsonElement>("api/v0/apps/test-app/monitoring/settings");
        (await api.PutAsJsonAsync("api/v0/apps/test-app/monitoring/settings", new {
            captureErrors = true, capturePerformance = true, forwardRawErrors = false,
            monitoringRetentionDays = 1, localAlertsEnabled = false, alertCooldownMinutes = 60
        })).EnsureSuccessStatusCode();
        using var cache = new HttpFailureSemanticsTests.TestCache();
        var release = "sdk68-replay-" + Guid.NewGuid().ToString("N");
        var escapedRelease = Uri.EscapeDataString(release);
        var sessionId = SentryId.Create();
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var sdkEvent = new SentryEvent {
            Release = release, Environment = "synthetic", Platform = "csharp", Level = SentryLevel.Error,
            SentryExceptions = [new SentryException {
                Type = "SyntheticReplay_" + release, Value = "Synthetic failure for person@example.test",
                Mechanism = new Mechanism { Type = "synthetic.unhandled", Handled = false },
                Stacktrace = new SentryStackTrace { Frames = { new SentryStackFrame {
                    Function = "SyntheticManagedSite.ThrowSynthetic()", FileName = @"C:\synthetic-private\SyntheticManagedProbe.cs",
                    LineNumber = 353, ColumnNumber = 17, ContextLine = "private-source-marker"
                } } }
            }]
        };
        sdkEvent.Sdk.Name = "sentry.dotnet";
        sdkEvent.Sdk.Version = "6.8.0";
        SessionUpdate Session(bool init, SessionEndStatus? end, int sequence) => new(sessionId, "private-device-marker", start,
            release, "synthetic", "192.0.2.1", "private-agent-marker", 1, init, start.AddSeconds(sequence + 5), sequence, end);
        void Configure(SentryOptions o, CollectorHandler handler)
        {
            o.Dsn = dsn;
            o.CacheDirectoryPath = cache.Path;
            o.InitCacheFlushTimeout = TimeSpan.FromSeconds(2);
            o.ShutdownTimeout = TimeSpan.FromSeconds(2);
            o.AutoSessionTracking = false;
            o.SendClientReports = false;
            o.SendDefaultPii = false;
            o.IsEnvironmentUser = false;
            o.ReportAssembliesMode = ReportAssembliesMode.None;
            o.ServerName = "";
            o.DisableDiagnosticSourceIntegration();
            o.DisableSystemDiagnosticsMetricsIntegration();
            o.CreateHttpMessageHandler = () => handler;
        }
        async Task<JsonElement> Health(string mode = "release") =>
            await api.GetFromJsonAsync<JsonElement>($"api/v0/apps/test-app/monitoring/health?buildMode={mode}&release={escapedRelease}");
        try
        {
            var offline = new CollectorHandler(origin, offline: true);
            using (SentrySdk.Init(o => Configure(o, offline)))
            {
                // Explicit SDK wire fixture, not an automatic exception hook or actual process crash.
                SentrySdk.CaptureEnvelope(Envelope.FromEvent(sdkEvent, null, null, Session(false, SessionEndStatus.Crashed, 1)));
                await SentrySdk.FlushAsync(TimeSpan.FromSeconds(2));
            }
            Assert.True(offline.Attempts > 0);
            Assert.Empty(offline.Responses);
            Assert.Equal(0, (await Health()).GetProperty("sessions").GetProperty("observed").GetInt64());
            Assert.NotEmpty(Directory.GetFiles(cache.Path, "*.envelope", SearchOption.AllDirectories));

            var lostAcknowledgement = new CollectorHandler(origin, loseAcknowledgement: true);
            using (SentrySdk.Init(o => Configure(o, lostAcknowledgement)))
                await SentrySdk.FlushAsync(TimeSpan.FromSeconds(2));
            Assert.NotEmpty(lostAcknowledgement.Responses);
            Assert.All(lostAcknowledgement.Responses, status => Assert.Equal(HttpStatusCode.OK, status));
            Assert.NotEmpty(Directory.GetFiles(cache.Path, "*.envelope", SearchOption.AllDirectories));
            var partial = (await Health()).GetProperty("sessions");
            Assert.Equal(1, partial.GetProperty("observed").GetInt64());
            Assert.Equal(1, partial.GetProperty("crashed").GetInt64());
            Assert.Equal(0, partial.GetProperty("eligible").GetInt64());
            Assert.Equal(JsonValueKind.Null, partial.GetProperty("crashFreeRate").ValueKind);

            var restored = new CollectorHandler(origin);
            using (SentrySdk.Init(o => Configure(o, restored)))
            {
                await SentrySdk.FlushAsync(TimeSpan.FromSeconds(3));
                SentrySdk.CaptureSession(Session(true, null, 0));
                SentrySdk.CaptureSession(Session(false, SessionEndStatus.Exited, 2));
                await SentrySdk.FlushAsync(TimeSpan.FromSeconds(3));
            }
            Assert.NotEmpty(restored.Responses);
            Assert.All(restored.Responses, status => Assert.Equal(HttpStatusCode.OK, status));
            Assert.Empty(Directory.GetFiles(cache.Path, "*.envelope", SearchOption.AllDirectories));
            var health = await Health();
            var counts = health.GetProperty("sessions");
            Assert.Equal(1, counts.GetProperty("observed").GetInt64());
            Assert.Equal(1, counts.GetProperty("eligible").GetInt64());
            Assert.Equal(1, counts.GetProperty("crashed").GetInt64());
            Assert.Equal(0, counts.GetProperty("crashFreeRate").GetDouble());
            Assert.Equal(0, (await Health("debug")).GetProperty("sessions").GetProperty("observed").GetInt64());
            var issues = await api.GetFromJsonAsync<JsonElement>($"api/v0/apps/test-app/issues?status=all&release={escapedRelease}");
            var issue = Assert.Single(issues.GetProperty("issues").EnumerateArray());
            Assert.Equal(1, issue.GetProperty("occurrenceCount").GetInt64());
            var occurrences = await api.GetFromJsonAsync<JsonElement>($"api/v0/apps/test-app/issues/{issue.GetProperty("id").GetString()}/events?release={escapedRelease}");
            var occurrence = Assert.Single(occurrences.GetProperty("events").EnumerateArray());
            Assert.Equal(Guid.Parse(sdkEvent.EventId.ToString()), occurrence.GetProperty("eventId").GetGuid());
            Assert.Equal("unhandled", occurrence.GetProperty("kind").GetString());
            Assert.Equal("6.8.0", occurrence.GetProperty("sdkVersion").GetString());
            Assert.Equal(release, occurrence.GetProperty("release").GetString());
            Assert.Equal("synthetic", occurrence.GetProperty("environment").GetString());
            Assert.Equal("at SyntheticManagedSite.ThrowSynthetic() (SyntheticManagedProbe.cs) line 353:17", occurrence.GetProperty("stackTrace").GetString());
            foreach (var marker in new[] { "synthetic-private", "private-source-marker", "person@example.test", "private-device-marker", "private-agent-marker", "192.0.2.1" })
            {
                Assert.DoesNotContain(marker, occurrences.ToString());
                Assert.DoesNotContain(marker, health.ToString());
            }
            var acknowledged = new CollectorHandler(origin);
            using (SentrySdk.Init(o => Configure(o, acknowledged)))
                await SentrySdk.FlushAsync(TimeSpan.FromSeconds(2));
            Assert.Empty(acknowledged.Responses);
        }
        finally
        {
            (await api.PutAsJsonAsync("api/v0/apps/test-app/monitoring/settings", settings)).EnsureSuccessStatusCode();
        }
    }

    private sealed class CollectorHandler(Uri origin, bool offline = false, bool loseAcknowledgement = false)
        : DelegatingHandler(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        public int Attempts;
        public ConcurrentBag<HttpStatusCode> Responses { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(origin.GetLeftPart(UriPartial.Authority), request.RequestUri!.GetLeftPart(UriPartial.Authority));
            Assert.Matches("\\A/api/[1-9][0-9]*/envelope/\\z", request.RequestUri.AbsolutePath);
            Interlocked.Increment(ref Attempts);
            if (offline) throw new HttpRequestException("Synthetic offline before any socket");
            var response = await base.SendAsync(request, cancellationToken);
            Responses.Add(response.StatusCode);
            if (!loseAcknowledgement) return response;
            response.Dispose();
            throw new HttpRequestException("Synthetic acknowledgement lost after collector response");
        }
    }
}

public sealed class CollectorFactAttribute : FactAttribute
{
    public CollectorFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APTABASE_SDK_TEST_COLLECTOR")))
            Skip = "Requires the disposable MonitoringHost on loopback55450; never the MAUI executable.";
    }
}
