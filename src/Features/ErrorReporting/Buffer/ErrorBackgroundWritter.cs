namespace Aptabase.Features.ErrorReporting.Buffer;

public class ErrorBackgroundWritter(ErrorOutbox outbox, ILogger<ErrorBackgroundWritter> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await FlushErrors(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task FlushErrors(CancellationToken cancellationToken = default)
    {
        try
        {
            var count = await outbox.Flush(cancellationToken);
            if (count > 0) logger.LogInformation("Delivered {Count} queued errors", count);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Raw error delivery failed; events remain in the durable outbox");
        }
    }
}
