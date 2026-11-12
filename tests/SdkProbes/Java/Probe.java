import io.sentry.*;
import java.net.URI;

public final class Probe {
    public static void main(String[] args) {
        String dsn = System.getenv("APTABASE_PROBE_DSN");
        URI uri = URI.create(dsn == null ? "" : dsn);
        if (!"http".equals(uri.getScheme()) || !"127.0.0.1".equals(uri.getHost()) || uri.getPort() != 55440
                || uri.getUserInfo() == null || !uri.getUserInfo().matches("[a-f0-9]{64}") || !uri.getPath().matches("/[0-9]+")) {
            throw new IllegalArgumentException("Only the disposable MonitoringHost DSN is allowed");
        }
        String release = System.getenv("APTABASE_PROBE_RELEASE");
        if (release == null || !release.matches("jvm-probe-[a-f0-9]{32}")) throw new IllegalArgumentException("Missing probe release");
        Sentry.init(options -> {
            options.setDsn(dsn);
            options.setEnableExternalConfiguration(false);
            options.setRelease(release);
            options.setEnvironment("sdk-acceptance");
            var privacy = options.getDataCollection();
            privacy.setUserInfo(false);
            privacy.setFilePaths(false);
            privacy.setDatabaseQueryData(false);
            privacy.setHttpBodies(java.util.Set.of());
            privacy.setCookies(KeyValueCollectionBehavior.off());
            privacy.setUrlQueryParams(KeyValueCollectionBehavior.off());
            privacy.getHttpHeaders().setRequest(KeyValueCollectionBehavior.off());
            privacy.getHttpHeaders().setResponse(KeyValueCollectionBehavior.off());
            privacy.getGraphql().setDocument(false);
            privacy.getGraphql().setVariables(false);
            options.setEnableAutoSessionTracking(false);
            options.setSendClientReports(false);
            options.setTracesSampleRate(1.0);
            options.setMaxBreadcrumbs(0);
            options.setAttachThreads(false);
            options.setConnectionTimeoutMillis(5000);
            options.setReadTimeoutMillis(5000);
            options.getIntegrations().removeIf(integration -> !(integration instanceof UncaughtExceptionHandlerIntegration)
                && !(integration instanceof ShutdownHookIntegration));
            options.setTransportFactory((configured, request) -> {
                var transport = new AsyncHttpTransportFactory().create(configured, request);
                return new io.sentry.transport.ITransport() {
                    public void send(SentryEnvelope envelope, Hint hint) throws java.io.IOException {
                        try (var output = java.nio.file.Files.newOutputStream(java.nio.file.Path.of("bin", envelope.getHeader().getEventId() + ".envelope"))) {
                            configured.getSerializer().serialize(envelope, output);
                        } catch (Exception error) { throw new java.io.IOException("Cannot save synthetic probe envelope", error); }
                        transport.send(envelope, hint);
                    }
                    public void flush(long timeout) { transport.flush(timeout); }
                    public io.sentry.transport.RateLimiter getRateLimiter() { return transport.getRateLimiter(); }
                    public void close() throws java.io.IOException { transport.close(); }
                    public void close(boolean restart) throws java.io.IOException { transport.close(restart); }
                };
            });
        });
        if (args.length == 1 && args[0].equals("unhandled")) {
            throw new UnsupportedOperationException("JVM uncaught probe owner@example.test");
        }
        try {
            Sentry.configureScope(scope -> scope.setExtra("discard-probe", "private-probe-context"));
            try { throw new IllegalStateException("JVM probe owner@example.test"); }
            catch (IllegalStateException error) { Sentry.captureException(error); }
            ITransaction transaction = Sentry.startTransaction("jvm.startup", "app.startup");
            ISpan span = transaction.startChild("file.read", "private-probe-description");
            span.finish(SpanStatus.OK);
            transaction.finish(SpanStatus.OK);
            Sentry.flush(10000);
        } finally { Sentry.close(); }
        System.out.println("JVM SDK capture flushed; the runner must verify stored records.");
    }
}
