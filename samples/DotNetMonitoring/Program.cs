using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

// Runs offline unless --send is explicitly supplied. Replace the sample workload with an app operation.
var startedAt = DateTime.UtcNow;
var watch = Stopwatch.StartNew();
await Task.Delay(20);
watch.Stop();
var payload = new {
    eventId = Guid.NewGuid().ToString("N"),
    name = "app.startup",
    operation = "ui.load",
    timestamp = startedAt,
    durationMs = watch.Elapsed.TotalMilliseconds,
    status = "ok",
    release = "1.0.0",
    environment = "development",
    platform = "dotnet",
    isDebug = true,
    spans = Array.Empty<object>()
};
if (!args.Contains("--send"))
{
    Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
var endpoint = Environment.GetEnvironmentVariable("APTABASE_COLLECTOR_URL")
    ?? throw new InvalidOperationException("Set APTABASE_COLLECTOR_URL before opting into sending.");
var appKey = Environment.GetEnvironmentVariable("APTABASE_APP_KEY")
    ?? throw new InvalidOperationException("Set APTABASE_APP_KEY before opting into sending.");
var address = new Uri(endpoint.TrimEnd('/') + "/api/v0/transaction");
if (address.Scheme != "https" && !(address.Scheme == "http" && address.IsLoopback))
    throw new InvalidOperationException("Use HTTPS, or loopback HTTP for local testing.");
using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
client.DefaultRequestHeaders.Add("App-Key", appKey);
for (var attempt = 0; attempt < 3; attempt++)
{
    try
    {
        using var response = await client.PostAsJsonAsync(address, payload);
        if (response.IsSuccessStatusCode) { Console.WriteLine("Transaction accepted."); return; }
        if (response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500)
            throw new InvalidOperationException($"Collector rejected the report: {(int)response.StatusCode}");
        if (attempt == 2) throw new InvalidOperationException("Collector unavailable after bounded retries.");
    }
    catch (Exception ex) when (attempt < 2 && ex is HttpRequestException or TaskCanceledException)
    {
        Console.Error.WriteLine("Transport failed; retrying the same event ID.");
    }
    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
}
