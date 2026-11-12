using System.Collections.Concurrent;
using Sentry;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

public class CacheReliabilityTests
{
    [Fact]
    public async Task Offline_Event_Replays_With_Same_Id_After_Sdk_Reinitialization()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "cache-probes", Guid.NewGuid().ToString("N"));
        var offline = new Sink(true);
        SentryId id;
        using (SentrySdk.Init(o => Configure(o, directory, offline)))
        {
            id = SentrySdk.CaptureMessage("Synthetic offline durability event");
            Assert.NotEqual(SentryId.Empty, id);
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(3));
        }
        Assert.True(offline.Attempts > 0);
        Assert.NotEmpty(Directory.GetFiles(directory, "*", SearchOption.AllDirectories));
        var online = new Sink(false);
        using (SentrySdk.Init(o => Configure(o, directory, online)))
        {
            await online.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(3));
        }
        Assert.Contains(id.ToString(), online.EventIds);
        var afterAcknowledgement = new Sink(false);
        using (SentrySdk.Init(o => Configure(o, directory, afterAcknowledgement)))
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(afterAcknowledgement.EventIds);
    }

    [Fact]
    public async Task Lost_Acknowledgement_Replays_Original_Id_For_Server_Deduplication()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "cache-probes", Guid.NewGuid().ToString("N"));
        var lostAck = new Sink(true, true);
        SentryId id;
        using (SentrySdk.Init(o => Configure(o, directory, lostAck)))
        {
            id = SentrySdk.CaptureMessage("Synthetic acknowledgement lost");
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(3));
        }
        Assert.Contains(id.ToString(), lostAck.EventIds);
        var retry = new Sink(false);
        using (SentrySdk.Init(o => Configure(o, directory, retry)))
            await retry.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(12));
        Assert.Contains(id.ToString(), retry.EventIds);
    }

    [Fact]
    public void Dispose_Drains_A_Queued_Event_Without_Explicit_Flush()
    {
        var sink = new Sink(false);
        SentryId id;
        using (SentrySdk.Init(o => Configure(o, null, sink)))
            id = SentrySdk.CaptureMessage("Synthetic shutdown event");
        Assert.Contains(id.ToString(), sink.EventIds);
    }

    private static void Configure(SentryOptions o, string? cache, Sink sink)
    {
        o.Dsn = "http://synthetic@127.0.0.1:1/1";
        o.Transport = sink; // SDK envelope/cache path is real; this sink never opens a socket.
        o.CacheDirectoryPath = cache;
        o.InitCacheFlushTimeout = TimeSpan.FromSeconds(2);
        o.ShutdownTimeout = TimeSpan.FromSeconds(2);
        o.SendDefaultPii = false;
        o.SendClientReports = false;
        o.AutoSessionTracking = false;
        o.ReportAssembliesMode = ReportAssembliesMode.None;
        o.IsEnvironmentUser = false;
        o.ServerName = "";
        o.DisableDiagnosticSourceIntegration();
        o.DisableSystemDiagnosticsMetricsIntegration();
    }

    private sealed class Sink(bool offline, bool receivedBeforeFailure = false) : ITransport
    {
        public int Attempts;
        public ConcurrentBag<string> EventIds { get; } = new();
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Attempts);
            if (receivedBeforeFailure)
                EventIds.Add(envelope.Header["event_id"]?.ToString() ?? throw new InvalidOperationException("Missing event ID"));
            if (offline) throw new HttpRequestException("Synthetic offline sink");
            EventIds.Add(envelope.Header["event_id"]?.ToString() ?? throw new InvalidOperationException("Missing event ID"));
            Delivered.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
