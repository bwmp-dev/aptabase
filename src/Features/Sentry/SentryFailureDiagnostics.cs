using Aptabase.Features.Ingestion;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace Aptabase.Features.Sentry;

public sealed class SentryFailureDiagnostics(IRejectionStats stats) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ActionExecutedContext result;
        try
        {
            result = await next();
        }
        catch
        {
            Record("backend_failure");
            throw;
        }

        var reason = result.Exception is not null && !result.ExceptionHandled
            ? "backend_failure"
            : (result.Result as IStatusCodeActionResult)?.StatusCode switch
            {
                400 => "malformed",
                403 => "credentials_or_policy",
                409 => "metadata_conflict",
                413 => "too_large",
                415 => "encoding",
                422 => "unsupported",
                >= 500 => "backend_failure",
                _ => null
            };
        if (reason is not null)
            Record(reason);
    }

    private void Record(string reason) => stats.Record("sentry_" + reason, null, null, null);
}
