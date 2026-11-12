using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using Aptabase.Features.Sentry;
using Sentry;
using Xunit;

public class HttpFailureSemanticsTests
{
    [Theory]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(429, false)]
    public async Task Http_Loss_Reports_And_RetryAfter_Suppression_Follow_Actual_Sdk_Semantics(int status, bool reportsLoss)
    {
        using var cache = new TestCache();
        var handler = new ResponseHandler(status);
        SentryId next;
        using (SentrySdk.Init(o => { Configure(o, cache.Path, handler); o.SendClientReports = true; }))
        {
            SentrySdk.CaptureMessage("Synthetic rejected delivery");
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(3));
            handler.Status = 200;
            next = SentrySdk.CaptureMessage("Synthetic following delivery");
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(3));
        }
        Assert.NotEqual(SentryId.Empty, next);
        if (reportsLoss)
        {
            Assert.Contains(next.ToString(), handler.EventIds);
            var counter = Assert.Single(handler.Parsed.Where(e => e.ClientReport is not null).SelectMany(e => e.ClientReport!.Counters));
            Assert.Equal(new SentryDiscardCounter("send_error", "error", 1), counter);
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
            if (Status is null) throw new HttpRequestException("Synthetic transport failure before a response");
            var response = new HttpResponseMessage((HttpStatusCode)Status.Value) { Content = new StringContent("{}") };
            if (Status is 429 or 503) response.Headers.RetryAfter = new(TimeSpan.FromSeconds(60));
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
