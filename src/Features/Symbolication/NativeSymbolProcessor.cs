using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aptabase.Features.Symbolication;

public interface INativeSymbolProcessor
{
    bool Enabled { get; }
    Task<NativeSymbolIndex?> Process(byte[] artifact, CancellationToken ct);
}

public class NativeSymbolProcessor : INativeSymbolProcessor
{
    private volatile bool cleanupFailed;
    private readonly string? directory = Environment.GetEnvironmentVariable("NATIVE_SYMBOL_WORKER_DIRECTORY");
    private readonly string? image = Environment.GetEnvironmentVariable("NATIVE_SYMBOL_WORKER_IMAGE");
    public bool Enabled => !cleanupFailed && directory is not null && Path.IsPathFullyQualified(directory) && !directory.Contains(',') &&
        File.Exists(Path.Combine(directory, "NativeSymbolWorker.dll")) && image is not null && Regex.IsMatch(image, @"\Asha256:[a-f0-9]{64}\z");

    private readonly string? sourceImage = Environment.GetEnvironmentVariable("NATIVE_SOURCE_WORKER_IMAGE");
    private readonly Aptabase.Features.ErrorReporting.IPiiSanitizer sanitizer = new Aptabase.Features.ErrorReporting.PiiSanitizer();

    public async Task<NativeSymbolIndex?> Process(byte[] artifact, CancellationToken ct)
    {
        if (!Enabled || artifact.Length > ElfSymbols.MaxBytes) return null;
        var output = await Run(artifact, image!, new[] { "dotnet", "/worker/NativeSymbolWorker.dll" }, true, null, ct);
        NativeSymbolIndex? index;
        try { index = output is null ? null : JsonSerializer.Deserialize<NativeSymbolIndex>(output); }
        catch (JsonException) { return null; }
        if (index?.BuildId is null || !Regex.IsMatch(index.BuildId, @"\A[a-f0-9]{32,64}\z") || index.BuildId.Length % 2 != 0 ||
            index.Functions is null || index.Functions.Length is < 1 or > 65536 ||
            index.Functions.Any(f => f is null || string.IsNullOrWhiteSpace(f.Name) || f.Name.Length > 512 ||
                f.Name.Any(char.IsControl) || f.Size == 0 || f.Address > ulong.MaxValue - f.Size)) return null;
        index = index with { Sources = null, SourceStatus = "unconfigured" };
        if (sourceImage is null || !Regex.IsMatch(sourceImage, @"\Asha256:[a-f0-9]{64}\z")) return index;
        var addresses = NativeSourceOutput.Addresses(index);
        if (addresses is null) return index with { SourceStatus = "limit_exceeded" };
        var input = string.Join("\n", addresses.Select(a => $"0x{a:x}")) + "\n";
        var source = await Run(artifact, sourceImage, new[] { "--kill-after=1s", "12s", "/opt/rh/gcc-toolset-14/root/usr/bin/addr2line",
            "--basenames", "--addresses", "--functions", "--inlines", "--exe=/input/artifact.elf" }, false, input, ct);
        if (source is null) return index with { SourceStatus = "unavailable" };
        try
        {
            var locations = NativeSourceOutput.Parse(source, addresses, sanitizer);
            return index with { Sources = locations, SourceStatus = locations.Length == 0 ? "unavailable" : "available" };
        }
        catch (FormatException) { return index with { SourceStatus = "unavailable" }; }
    }

    private async Task<string?> Run(byte[] artifact, string workerImage, string[] command, bool managed, string? input, CancellationToken ct)
    {
        var name = "aptabase-symbol-" + Guid.NewGuid().ToString("N");
        var scratch = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(scratch);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var started = false;
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(scratch, "artifact.elf"), artifact, deadline.Token);
            var arguments = new List<string> { "run", "--pull", "never", "--rm", "--name", name,
                "--network", "none", "--read-only", "--cpus", "0.5", "--memory", "128m", "--memory-swap", "128m",
                "--pids-limit", "32", "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--user", "65534:65534",
                "-e", "DOTNET_EnableDiagnostics=0", "-e", "DEBUGINFOD_URLS=",
                "--mount", $"type=bind,source={scratch},target=/input,readonly" };
            if (managed) arguments.AddRange(new[] { "--mount", $"type=bind,source={directory},target=/worker,readonly" });
            else arguments.AddRange(new[] { "--interactive", "--entrypoint", "/usr/bin/timeout" });
            arguments.Add(workerImage);
            arguments.AddRange(command);
            using var process = new Process { StartInfo = Command(arguments.ToArray()) };
            process.StartInfo.RedirectStandardInput = input is not null;
            started = process.Start();
            try
            {
                var output = ReadBounded(process.StandardOutput, 16 * 1024 * 1024, deadline);
                var errors = ReadBounded(process.StandardError, 65536, deadline);
                var write = input is null ? Task.CompletedTask : WriteInput(process, input, deadline.Token);
                await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, errors, write);
                return process.ExitCode == 0 ? await output : null;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or JsonException or System.ComponentModel.Win32Exception)
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
        finally
        {
            // Killing Docker's client does not necessarily stop its container.
            try
            {
                if (started) await RemoveContainer(name);
            }
            catch
            {
                // Do not create another worker when we cannot confirm the previous one stopped.
                cleanupFailed = true;
                throw;
            }
            finally
            {
                // Only these two generated paths are ours; no recursive deletion or uploaded paths.
                File.Delete(Path.Combine(scratch, "artifact.elf"));
                Directory.Delete(scratch);
            }
        }
    }

    private static async Task WriteInput(Process process, string input, CancellationToken ct)
    {
        await process.StandardInput.WriteAsync(input.AsMemory(), ct);
        process.StandardInput.Close();
    }

    private static async Task RemoveContainer(string name)
    {
        using var cleanup = new Process { StartInfo = Command("rm", "--force", name) };
        var started = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            started = cleanup.Start();
            var output = ReadBounded(cleanup.StandardOutput, 65536, deadline);
            var errors = ReadBounded(cleanup.StandardError, 65536, deadline);
            await Task.WhenAll(cleanup.WaitForExitAsync(deadline.Token), output, errors);
            if (cleanup.ExitCode != 0 && !(await errors).Contains("No such container", StringComparison.OrdinalIgnoreCase))
            {
                // --rm can race explicit removal. Only verified absence makes that failure harmless.
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    if (await ContainerAbsent(name, deadline)) return;
                    await Task.Delay(100, deadline.Token);
                }
                throw new IOException("Native symbol container cleanup failed");
            }
        }
        finally
        {
            if (started && !cleanup.HasExited) cleanup.Kill(entireProcessTree: true);
        }
    }

    private static async Task<bool> ContainerAbsent(string name, CancellationTokenSource deadline)
    {
        using var inspect = new Process { StartInfo = Command("container", "inspect", "--format", "{{.Id}}", name) };
        inspect.Start();
        try
        {
            var output = ReadBounded(inspect.StandardOutput, 65536, deadline);
            var errors = ReadBounded(inspect.StandardError, 65536, deadline);
            await Task.WhenAll(inspect.WaitForExitAsync(deadline.Token), output, errors);
            return inspect.ExitCode != 0 && ((await errors).Contains("No such container", StringComparison.OrdinalIgnoreCase) ||
                (await errors).Contains("No such object", StringComparison.OrdinalIgnoreCase));
        }
        finally { if (!inspect.HasExited) inspect.Kill(entireProcessTree: true); }
    }

    private static ProcessStartInfo Command(params string[] arguments)
    {
        var info = new ProcessStartInfo("docker") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private static async Task<string> ReadBounded(StreamReader reader, int max, CancellationTokenSource deadline)
    {
        var text = new StringBuilder();
        var chunk = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(chunk, deadline.Token)) > 0)
        {
            if (text.Length + read > max)
            {
                deadline.Cancel();
                throw new IOException("Worker output limit exceeded");
            }
            text.Append(chunk, 0, read);
        }
        return text.ToString();
    }
}

public class NativeSymbolJob(NativeSymbolStore store, ILogger<NativeSymbolJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await store.Cleanup(stoppingToken);
                await store.ProcessOne(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError("Native symbol processing failed ({ErrorType})", error.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
