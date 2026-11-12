using Sentry;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

// Suppress process-local Windows error dialogs and ambient dump creation during this synthetic crash.
NativeMethods.SetErrorMode(0x0001 | 0x0002 | 0x8000);
using var watchdog = new Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
Environment.SetEnvironmentVariable("DOTNET_DbgEnableMiniDump", "0");
Environment.SetEnvironmentVariable("COMPlus_DbgEnableMiniDump", "0");

var dsn = Environment.GetEnvironmentVariable("APTABASE_PROBE_DSN") ?? "";
if (!Regex.IsMatch(dsn, @"\Ahttp://[a-f0-9]{64}@127\.0\.0\.1:55440/[1-9][0-9]*\z")) return 2;
var mode = args.SingleOrDefault() ?? "handled";
if (mode is not ("handled" or "unhandled")) return 2;
using var sdk = SentrySdk.Init(options => {
    options.Dsn = dsn;
    options.Release = Environment.GetEnvironmentVariable("APTABASE_PROBE_RELEASE") ?? "dotnet-windows-probe";
    options.Environment = "synthetic";
    options.SendDefaultPii = false;
    options.SendClientReports = false;
    options.AutoSessionTracking = false;
    options.ReportAssembliesMode = ReportAssembliesMode.None;
    options.IsEnvironmentUser = false;
    options.ServerName = "";
    options.TracesSampleRate = 1;
    options.ShutdownTimeout = TimeSpan.FromSeconds(3);
    options.DisableDiagnosticSourceIntegration();
    options.DisableUnobservedTaskExceptionCapture();
    options.DisableSystemDiagnosticsMetricsIntegration();
    options.CreateHttpMessageHandler = () => new LoopbackOnly();
    options.SetBeforeSend(@event => {
        @event.ServerName = null;
        @event.Contexts.Clear();
        @event.User = new SentryUser();
        foreach (var exception in @event.SentryExceptions ?? [])
            foreach (var frame in exception.Stacktrace?.Frames ?? [])
            {
                frame.FileName = frame.FileName?.Replace('\\', '/').Split('/')[^1];
                frame.AbsolutePath = null;
                frame.ContextLine = null;
                frame.PreContext?.Clear();
                frame.PostContext?.Clear();
            }
        return @event;
    });
});
if (mode == "unhandled")
{
    // Keep the SDK alive while the CLR reports a genuinely unhandled worker-thread exception.
    var thread = new Thread(() => throw new InvalidOperationException("Synthetic unhandled Windows managed failure"));
    thread.Start();
    thread.Join();
    return 1;
}
try { SyntheticManagedSite.ThrowHandled(); }
catch (InvalidOperationException error) { SentrySdk.CaptureException(error); }
var transaction = SentrySdk.StartTransaction("windows.startup", "app.startup");
var span = transaction.StartChild("local.work");
span.Finish();
transaction.Finish();
await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
return 0;

sealed class LoopbackOnly : DelegatingHandler
{
    public LoopbackOnly() : base(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.Port != 55440 ||
            !Regex.IsMatch(uri.AbsolutePath, @"\A/api/[1-9][0-9]*/envelope/\z"))
            throw new InvalidOperationException("Probe transport only permits the disposable collector");
        return base.SendAsync(request, cancellationToken);
    }
}

static class NativeMethods
{
    [DllImport("kernel32.dll")]
    internal static extern uint SetErrorMode(uint mode);
}

static class SyntheticManagedSite
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static void ThrowHandled()
    {
#line 353 "SyntheticManagedProbe.cs"
                throw new InvalidOperationException("Synthetic handled Windows managed failure");
#line default
    }
}
