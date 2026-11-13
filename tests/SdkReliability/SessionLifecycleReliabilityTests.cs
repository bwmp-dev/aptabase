using System.Collections.Concurrent;
using Aptabase.Features.Sentry;
using Sentry;
using Sentry.Extensibility;
using Sentry.Protocol;
using Sentry.Protocol.Envelopes;
using Xunit;

public class SessionLifecycleReliabilityTests
{
    [Fact]
    public async Task FlushAloneDoesNotFinalizeSessionButExplicitShutdownDoes()
    {
        using var cache = new HttpFailureSemanticsTests.TestCache();
        var sink = new Sink(false);
        using (SentrySdk.Init(o => Configure(o, cache.Path, sink, true)))
        {
            SentrySdk.CaptureException(new InvalidOperationException("Synthetic handled lifecycle"));
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(2));
            Assert.NotEmpty(sink.Sessions);
            Assert.All(sink.Sessions, session => Assert.Equal("ok", session.Status));
            Assert.Single(Directory.GetFiles(cache.Path, ".session", SearchOption.AllDirectories));
            SentrySdk.EndSession(SessionEndStatus.Exited);
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(2));
            Assert.Contains(sink.Sessions, session => session.Status == "exited");
            Assert.Empty(Directory.GetFiles(cache.Path, ".session", SearchOption.AllDirectories));
        }
    }

    [Theory]
    [InlineData(SessionEndStatus.Exited, "exited")]
    [InlineData(SessionEndStatus.Unhandled, "unhandled")]
    [InlineData(SessionEndStatus.Crashed, "crashed")]
    public async Task ExplicitTerminalSessionAndErrorSurviveOfflineTransportAndReplayOnce(SessionEndStatus end, string status)
    {
        using var cache = new HttpFailureSemanticsTests.TestCache();
        var offline = new Sink(true);
        SentryId eventId;
        using (SentrySdk.Init(o => Configure(o, cache.Path, offline, true)))
        {
            eventId = SentrySdk.CaptureException(new InvalidOperationException("Synthetic offline lifecycle"));
            SentrySdk.EndSession(end); // SDK lifecycle test, not an automatic platform crash hook.
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(2));
        }
        Assert.NotEmpty(Directory.GetFiles(cache.Path, "*.envelope", SearchOption.AllDirectories));
        var online = new Sink(false);
        using (SentrySdk.Init(o => Configure(o, cache.Path, online, false)))
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(3));
        Assert.Contains(Guid.Parse(eventId.ToString()), online.EventIds);
        var terminal = Assert.Single(online.Sessions, session => session.Status == status);
        Assert.Contains(online.Sessions, session => session.Id == terminal.Id && session.Init);
        Assert.Empty(Directory.GetFiles(cache.Path, "*.envelope", SearchOption.AllDirectories));
        var acknowledged = new Sink(false);
        using (SentrySdk.Init(o => Configure(o, cache.Path, acknowledged, false)))
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(acknowledged.EventIds);
        Assert.Empty(acknowledged.Sessions);
    }

    [Theory]
    [InlineData(true, "crashed")]
    [InlineData(false, "unhandled")]
    public async Task ActualSdkDerivesTerminalSessionFromMechanismAndReplaysIt(bool terminal, string expectedStatus)
    {
        using var cache = new HttpFailureSemanticsTests.TestCache();
        var offline = new Sink(true);
        var exception = new InvalidOperationException("Synthetic mechanism fixture, not a platform hook");
        exception.Data[Mechanism.HandledKey] = false;
        exception.Data[Mechanism.TerminalKey] = terminal;
        exception.Data[Mechanism.MechanismKey] = "synthetic.lifecycle";
        var error = new SentryEvent(exception);
        using (SentrySdk.Init(o => Configure(o, cache.Path, offline, true)))
        {
            Assert.NotEqual(SentryId.Empty, SentrySdk.CaptureEvent(error));
            // Exited cannot turn a pending nonterminal unhandled error into a healthy session.
            SentrySdk.EndSession(SessionEndStatus.Exited);
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(2));
        }
        var online = new Sink(false);
        using (SentrySdk.Init(o => Configure(o, cache.Path, online, false)))
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(3));
        Assert.Contains(Guid.Parse(error.EventId.ToString()), online.EventIds);
        Assert.Single(online.Sessions, session => session.Status == expectedStatus);
        Assert.DoesNotContain(online.Sessions, session => session.Status == "exited");
        Assert.Empty(Directory.GetFiles(cache.Path, "*.envelope", SearchOption.AllDirectories));
    }

    private static void Configure(SentryOptions options, string cache, Sink sink, bool sessions)
    {
        options.Dsn = "http://synthetic@127.0.0.1:1/1";
        options.Release = "synthetic-lifecycle@1";
        options.Environment = "synthetic";
        options.Transport = sink; // Terminal sink: no sockets, profile reads or real collector.
        options.CacheDirectoryPath = cache;
        options.InitCacheFlushTimeout = TimeSpan.FromSeconds(2);
        options.ShutdownTimeout = TimeSpan.FromSeconds(2);
        options.AutoSessionTracking = sessions;
        options.IsGlobalModeEnabled = true;
        options.SendClientReports = false;
        options.SendDefaultPii = false;
        options.IsEnvironmentUser = false;
        options.ServerName = "";
        options.ReportAssembliesMode = ReportAssembliesMode.None;
        options.DisableDiagnosticSourceIntegration();
        options.DisableSystemDiagnosticsMetricsIntegration();
    }

    private sealed class Sink(bool offline) : ITransport
    {
        public ConcurrentBag<SentrySessionUpdate> Sessions { get; } = new();
        public ConcurrentBag<Guid> EventIds { get; } = new();
        public async Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            if (offline) throw new HttpRequestException("Synthetic offline transport");
            await using var stream = new MemoryStream();
            await envelope.SerializeAsync(stream, null, cancellationToken);
            var parsed = SentryEnvelopeParser.Parse(stream.ToArray());
            foreach (var session in parsed.Sessions) Sessions.Add(session);
            if (parsed.EventId is not null) EventIds.Add(Guid.Parse(parsed.EventId));
        }
    }
}
