using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using Sentry;
using Sentry.Protocol.Envelopes;
using Aptabase.Features.Sentry;
using Xunit;

public class EnvelopeInventoryTests
{
    [Fact]
    public async Task BeforeSend_Error_OptOut_Does_Not_Suppress_Sessions_Or_Client_Reports()
    {
        var sink = new RecordingHttpHandler();
        using (SentrySdk.Init(o =>
        {
            Configure(o, sink);
            o.AutoSessionTracking = true;
            Assert.True(o.SendClientReports);
            o.SetBeforeSend(_ => null);
        }))
        {
            SentrySdk.CaptureException(new InvalidOperationException("Synthetic opted-out error"));
            SentrySdk.EndSession(SessionEndStatus.Exited);
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(3));
        }
        Assert.Empty(sink.Errors);
        Assert.All(sink.Parsed, envelope => Assert.Null(envelope.Error));
        Assert.Contains(sink.Parsed, envelope => envelope.Sessions.Count > 0);
        Assert.Contains(sink.Parsed, envelope => envelope.ClientReport?.Counters.Any(c => c.Reason == "before_send" && c.Category == "error") == true);
    }

    [Fact]
    public async Task Diagnostic_Only_Configuration_Preserves_Errors_Sessions_And_Client_Reports()
    {
        var sink = new RecordingHttpHandler();
        SentryId acceptedId;
        using (SentrySdk.Init(o =>
        {
            Configure(o, sink);
            o.AutoSessionTracking = true;
            o.SendClientReports = true;
            // Diagnostic filtering has no dependency on the product-analytics preference.
            o.SetBeforeSend(e =>
            {
                if (e.Exception is null) return null;
                e.User = new SentryUser();
                e.Contexts.Clear();
                e.ServerName = null;
                return e;
            });
        }))
        {
            SentrySdk.ConfigureScope(scope => scope.User.Email = "private-identity@example.test");
            SentrySdk.CaptureMessage("Synthetic message excluded by diagnostic filter");
            acceptedId = SentrySdk.CaptureException(new InvalidOperationException("Synthetic diagnostic retained"));
            SentrySdk.EndSession(SessionEndStatus.Exited);
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(3));
        }
        Assert.Empty(sink.Errors);
        var error = Assert.Single(sink.Parsed, e => e.Error is not null).Error!;
        Assert.Equal(Guid.Parse(acceptedId.ToString()).ToString(), error.EventId);
        Assert.Equal("synthetic-envelope-inventory", error.Release);
        Assert.Equal("synthetic", error.Environment);
        var sessions = sink.Parsed.SelectMany(e => e.Sessions).ToArray();
        Assert.Contains(sessions, session => session.Init);
        Assert.Contains(sessions, session => session.Status == "exited" && session.Errors == 1);
        Assert.All(sessions, session =>
        {
            Assert.Equal(error.Release, session.Release);
            Assert.Equal(error.Environment, session.Environment);
        });
        Assert.Contains(sink.Parsed, envelope => envelope.ClientReport?.Counters.Any(c =>
            c.Reason == "before_send" && c.Category == "error" && c.Quantity == 1) == true);
        Assert.DoesNotContain("private-identity", System.Text.Json.JsonSerializer.Serialize(sink.Parsed));
    }

    [Theory]
    [InlineData(SessionEndStatus.Crashed, "crashed")]
    [InlineData(SessionEndStatus.Unhandled, "unhandled")]
    [InlineData(SessionEndStatus.Exited, "exited")]
    [InlineData(SessionEndStatus.Abnormal, "abnormal")]
    public async Task Actual_Sdk_Terminal_Session_Forms_Preserve_Cumulative_Fields_Without_Identity(SessionEndStatus end, string status)
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var update = new SessionUpdate(SentryId.Create(), "private-device", start, "synthetic@1", "synthetic",
            "192.0.2.1", "private-agent", 3, false, start.AddSeconds(5), 2, end);
        using var envelope = Envelope.FromSession(update);
        await using var stream = new MemoryStream();
        await envelope.SerializeAsync(stream, null);
        var parsed = SentryEnvelopeParser.Parse(stream.ToArray());
        var session = Assert.Single(parsed.Sessions);
        Assert.Equal(status, session.Status);
        Assert.Equal(3, session.Errors);
        Assert.Equal(2, session.Sequence);
        Assert.Equal(5, session.Duration);
        Assert.DoesNotContain("private", System.Text.Json.JsonSerializer.Serialize(parsed));
    }

    [Fact]
    public async Task Default_Client_Reports_Are_Appended_To_An_Event_After_A_Discard()
    {
        var sink = new RecordingHttpHandler();
        using (SentrySdk.Init(o =>
        {
            Configure(o, sink);
            Assert.True(o.SendClientReports);
            o.SetBeforeSend(e => e.Exception is null ? null : e);
        }))
        {
            SentrySdk.CaptureMessage("synthetic discarded");
            SentrySdk.CaptureException(new InvalidOperationException("Synthetic accepted"));
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(3));
        }
        Assert.Empty(sink.Errors);
        Assert.Contains(sink.ItemTypes, types => types.Contains("event") && types.Contains("client_report"));
        Assert.Contains(sink.Parsed, envelope => envelope.Error is not null && envelope.ClientReport is not null);
    }

    [Fact]
    public async Task Session_Tracking_Can_Combine_Session_And_Error_In_One_Envelope()
    {
        var sink = new RecordingHttpHandler();
        using (SentrySdk.Init(o =>
        {
            Configure(o, sink);
            o.SendClientReports = false;
            o.AutoSessionTracking = true;
        }))
        {
            SentrySdk.CaptureException(new InvalidOperationException("Synthetic session error"));
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(3));
        }
        Assert.Empty(sink.Errors);
        Assert.Contains(sink.ItemTypes, types => types.Contains("event") && types.Contains("session"));
        Assert.Contains(sink.Parsed, envelope => envelope.Error is not null && envelope.Sessions.Count > 0);
    }

    [Fact]
    public async Task Supported_Subset_Settings_Emit_Only_The_Error_After_A_Discard()
    {
        var sink = new RecordingHttpHandler();
        using (SentrySdk.Init(o =>
        {
            Configure(o, sink);
            o.SendClientReports = false;
            o.AutoSessionTracking = false;
            o.SetBeforeSend(e => e.Exception is null ? null : e);
        }))
        {
            SentrySdk.CaptureMessage("synthetic discarded");
            SentrySdk.CaptureException(new InvalidOperationException("Synthetic supported error"));
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(3));
        }
        Assert.Empty(sink.Errors);
        Assert.Equal(new[] { "event" }, Assert.Single(sink.ItemTypes));
    }

    private static void Configure(SentryOptions o, RecordingHttpHandler sink)
    {
        o.Dsn = "http://synthetic@127.0.0.1:1/1";
        o.Release = "synthetic-envelope-inventory";
        o.Environment = "synthetic";
        o.SendDefaultPii = false;
        o.ReportAssembliesMode = ReportAssembliesMode.None;
        o.IsEnvironmentUser = false;
        o.ServerName = "";
        o.ShutdownTimeout = TimeSpan.FromSeconds(3);
        o.DisableDiagnosticSourceIntegration();
        o.DisableSystemDiagnosticsMetricsIntegration();
        o.CreateHttpMessageHandler = () => sink;
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public ConcurrentBag<string?[]> ItemTypes { get; } = new();
        public ConcurrentBag<SentryEnvelope> Parsed { get; } = new();
        public ConcurrentBag<string> Errors { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                // Exercise SDK HTTP serialization/compression without a socket or external endpoint.
                var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                Assert.InRange(bytes.Length, 1, 262144);
                await using var body = new MemoryStream(bytes);
                await using var gzip = request.Content.Headers.ContentEncoding.Contains("gzip")
                    ? new GZipStream(body, CompressionMode.Decompress) : null;
                await using var decoded = new MemoryStream();
                await (gzip ?? (Stream)body).CopyToAsync(decoded, cancellationToken);
                Assert.InRange(decoded.Length, 1, 262144);
                Parsed.Add(SentryEnvelopeParser.Parse(decoded.ToArray()));
                decoded.Position = 0;
                using var envelope = await Envelope.DeserializeAsync(decoded, cancellationToken);
                ItemTypes.Add(envelope.Items.Select(item => item.TryGetType()).ToArray());
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }
            catch (Exception e)
            {
                Errors.Add(e.GetType().Name + ": " + e.Message);
                throw;
            }
        }
    }
}
