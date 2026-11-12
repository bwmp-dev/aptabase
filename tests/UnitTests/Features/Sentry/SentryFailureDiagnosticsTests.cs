using Aptabase.Features.Ingestion;
using Aptabase.Features.Sentry;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Aptabase.UnitTests.Features.Sentry;

public class SentryFailureDiagnosticsTests
{
    [Fact]
    public async Task Records_Server_Failure_Without_Swallowing_Or_Logging_Exception_Content()
    {
        var stats = new RejectionStats();
        var action = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        var before = new ActionExecutingContext(action, new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());
        var error = new InvalidOperationException("private diagnostic content");
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => new SentryFailureDiagnostics(stats)
            .OnActionExecutionAsync(before, () => throw error));
        Assert.Same(error, thrown);
        Assert.Equal(new RejectionKey("sentry_backend_failure", "", "", ""), Assert.Single(stats.Snapshot().Entries).Key);
    }
    [Theory]
    [InlineData(400, "malformed")]
    [InlineData(403, "credentials_or_policy")]
    [InlineData(409, "metadata_conflict")]
    [InlineData(413, "too_large")]
    [InlineData(415, "encoding")]
    [InlineData(422, "unsupported")]
    [InlineData(503, "backend_failure")]
    [InlineData(200, null)]
    public async Task Counts_Only_Bounded_Reasons_Without_Request_Identity(int status, string? reason)
    {
        var stats = new RejectionStats();
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Sentry-Auth"] = "private-credential";
        http.Request.Headers.UserAgent = "private-agent";
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var filters = new List<IFilterMetadata>();
        var controller = new object();
        var before = new ActionExecutingContext(action, filters, new Dictionary<string, object?>(), controller);
        await new SentryFailureDiagnostics(stats).OnActionExecutionAsync(before,
            () => Task.FromResult(new ActionExecutedContext(action, filters, controller)
            {
                Result = new StatusCodeResult(status)
            }));
        var snapshot = stats.Snapshot();
        if (reason is null)
        {
            Assert.Equal(0, snapshot.Total);
            return;
        }
        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal(new RejectionKey("sentry_" + reason, "", "", ""), entry.Key);
        Assert.Equal(1, entry.Value);
    }
}
