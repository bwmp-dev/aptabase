using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using Aptabase.Features.Sentry;
using Sentry;
using Xunit;

public class HttpFailureSemanticsTests
{
    [Theory]
    [InlineData(500, true, false)]
    [InlineData(500, true, true)]
    [InlineData(503, true, false)]
    [InlineData(503, true, true)]
    [InlineData(429, false, false)]
    public async Task Http_Loss_Reports_And_RetryAfter_Suppression_Follow_Actual_Sdk_Semantics(int status, bool reportsLoss, bool rejectFirstReport)
    {
        using var cache = new TestCache();
        var handler = new ResponseHandler(status) { RejectFirstReport = rejectFirstReport };
        SentryId rejected;
        SentryId next;
        using (SentrySdk.Init(o => { Configure(o, cache.Path, handler); o.SendClientReports = true; }))
        {
            rejected = SentrySdk.CaptureMessage("Synthetic rejected delivery");
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(3));
            // Cache transport can queue the flush-generated report after its own flush finishes.
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(3));
            if (reportsLoss)
                Assert.Contains(handler.Attempts, a => a.Envelope.ClientReport is not null);
            handler.Status = 200;
            next = SentrySdk.CaptureMessage("Synthetic following delivery");
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(3));
        }
        Assert.NotEqual(SentryId.Empty, rejected);
        Assert.NotEqual(SentryId.Empty, next);
        Assert.Single(handler.Attempts, a => a.Envelope.EventId == Guid.Parse(rejected.ToString()).ToString() && a.Status == status);
        if (reportsLoss)
        {
            Assert.Contains(next.ToString(), handler.EventIds);
            Assert.Single(handler.Attempts, a => a.Envelope.EventId == Guid.Parse(next.ToString()).ToString() && a.Status == 200);
            var rejectedReports = handler.Attempts.Where(a => a.Status >= 400 && a.Envelope.ClientReport is not null).ToArray();
            Assert.Equal(rejectFirstReport ? 1 : 0, rejectedReports.Length);
            var delivered = handler.Attempts.Where(a => a.Status == 200 && a.Envelope.ClientReport is not null)
                .SelectMany(a => a.Envelope.ClientReport!.Counters).OrderBy(c => c.Category).ToArray();
            // Sentry 6.8 restores a rejected report's counters and counts that report as internal/send_error.
            var expected = rejectFirstReport
                ? new[] { new SentryDiscardCounter("send_error", "error", 1), new SentryDiscardCounter("send_error", "internal", 1) }
                : new[] { new SentryDiscardCounter("send_error", "error", 1) };
            Assert.Equal(expected, delivered);
        }
        else
        {
            Assert.DoesNotContain(next.ToString(), handler.EventIds);
            Assert.All(handler.Parsed, envelope => Assert.Null(envelope.ClientReport));
        }
        Assert.Empty(Directory.GetFiles(cache.Path, "*.envelope", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(429)]
    [InlineData(403)]
    [InlineData(413)]
    [InlineData(422)]
    public async Task Http_Rejection_Is_Not_Durable_Replay_Even_With_Disk_Cache(int status)
    {
        using var cache = new TestCache();
        var rejected = new ResponseHandler(status);
        SentryId id;
        using (SentrySdk.Init(o => Configure(o, cache.Path, rejected)))
        {
            id = SentrySdk.CaptureMessage("Synthetic rejected delivery");
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(3));
        }
        Assert.Single(rejected.EventIds, value => value == id.ToString());
        Assert.Empty(Directory.GetFiles(cache.Path, "*.envelope", SearchOption.AllDirectories));
        var recovered = new ResponseHandler(200);
        SentryId canary;
        using (SentrySdk.Init(o => Configure(o, cache.Path, recovered)))
        {
            canary = SentrySdk.CaptureMessage("Synthetic post-reinitialization canary");
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(3));
        }
        Assert.Contains(canary.ToString(), recovered.EventIds);
        Assert.DoesNotContain(id.ToString(), recovered.EventIds);
    }

    [Fact]
    public async Task Http_Transport_Exception_Retains_The_Original_Event_For_Replay()
    {
        using var cache = new TestCache();
        var disconnected = new ResponseHandler(null);
        SentryId id;
        using (SentrySdk.Init(o => Configure(o, cache.Path, disconnected)))
        {
            id = SentrySdk.CaptureMessage("Synthetic transport interruption");
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(2));
        }
        Assert.Contains(id.ToString(), disconnected.EventIds);
        Assert.NotEmpty(Directory.GetFiles(cache.Path, "*.envelope", SearchOption.AllDirectories));
        var recovered = new ResponseHandler(200);
        using (SentrySdk.Init(o => Configure(o, cache.Path, recovered)))
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(3));
        Assert.Contains(id.ToString(), recovered.EventIds);
        Assert.Empty(Directory.GetFiles(cache.Path, "*.envelope", SearchOption.AllDirectories));
    }

    private static void Configure(SentryOptions options, string cache, ResponseHandler handler)
    {
        options.Dsn = "http://synthetic@127.0.0.1:1/1";
        options.Release = "synthetic-http-reliability";
        options.Environment = "synthetic";
        options.CacheDirectoryPath = cache;
        options.InitCacheFlushTimeout = TimeSpan.FromSeconds(2);
        options.ShutdownTimeout = TimeSpan.FromSeconds(2);
        options.SendClientReports = false;
        options.AutoSessionTracking = false;
        options.SendDefaultPii = false;
        options.IsEnvironmentUser = false;
        options.ReportAssembliesMode = ReportAssembliesMode.None;
        options.ServerName = "";
        options.DisableDiagnosticSourceIntegration();
        options.DisableSystemDiagnosticsMetricsIntegration();
        options.SetBeforeSend(e => { e.User = new SentryUser(); e.Contexts.Clear(); e.ServerName = null; return e; });
        options.CreateHttpMessageHandler = () => handler;
    }

    private sealed class ResponseHandler(int? status) : HttpMessageHandler
    {
        public int? Status { get; set; } = status;
        public bool RejectFirstReport { get; init; }
        private int reportRequests;
        public ConcurrentBag<(SentryEnvelope Envelope, int? Status)> Attempts { get; } = new();
        public ConcurrentBag<string> EventIds { get; } = new();
        public ConcurrentBag<SentryEnvelope> Parsed { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Real SDK HTTP serialization and cache processing; this terminal handler cannot open sockets.
            await using var input = await request.Content!.ReadAsStreamAsync(cancellationToken);
            await using var gzip = request.Content.Headers.ContentEncoding.Contains("gzip") ? new GZipStream(input, CompressionMode.Decompress) : null;
            await using var decoded = new MemoryStream();
            await (gzip ?? (Stream)input).CopyToAsync(decoded, cancellationToken);
            Assert.InRange(decoded.Length, 1, SentryEnvelopeParser.MaxBytes);
            var envelope = SentryEnvelopeParser.Parse(decoded.ToArray());
            Parsed.Add(envelope);
            if (envelope.EventId is not null) EventIds.Add(Guid.Parse(envelope.EventId).ToString("N"));
            var responseStatus = Status;
            if (envelope.ClientReport is not null && envelope.EventId is null)
                responseStatus = RejectFirstReport && Interlocked.Increment(ref reportRequests) == 1 ? 500 : 200;
            Attempts.Add((envelope, responseStatus));
            if (responseStatus is null) throw new HttpRequestException("Synthetic transport failure before a response");
            var response = new HttpResponseMessage((HttpStatusCode)responseStatus.Value) { Content = new StringContent("{}") };
            if (responseStatus is 429 or 503) response.Headers.RetryAfter = new(TimeSpan.FromSeconds(60));
            return response;
        }
    }

    internal sealed class TestCache : IDisposable
    {
        private readonly string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "http-cache-probes"));
        public string Path { get; }

        public TestCache()
        {
            Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            Assert.StartsWith(root + System.IO.Path.DirectorySeparatorChar, Path);
            Assert.False(File.GetAttributes(Path).HasFlag(FileAttributes.ReparsePoint));
            Assert.All(Directory.EnumerateFileSystemEntries(Path, "*", SearchOption.AllDirectories),
                entry => Assert.False(File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint)));
            Directory.Delete(Path, recursive: true);
        }
    }
}
