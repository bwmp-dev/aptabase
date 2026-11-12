using System.Diagnostics;
using Aptabase.Features.Symbolication;
using Xunit;

namespace Aptabase.IntegrationTests;

// These opt-in real Docker checks are isolated from environment-reading tests.
[CollectionDefinition("Native symbol worker environment", DisableParallelization = true)]
public class NativeSymbolWorkerCollection;

[Collection("Native symbol worker environment")]
public class NativeSymbolWorkerTests
{
    [NativeSymbolWorkerFact]
    public async Task Real_Isolated_Worker_Resolves_Ndk_Elf_Rejects_Malformed_And_Cleans_Up_Failures()
    {
        var worker = Required("NATIVE_SYMBOL_TEST_WORKER_DIRECTORY");
        var faultWorker = Required("NATIVE_SYMBOL_TEST_FAULT_DIRECTORY");
        var fixture = Required("NATIVE_SYMBOL_TEST_FIXTURE");
        var image = Required("NATIVE_SYMBOL_TEST_IMAGE");
        var originalDirectory = Environment.GetEnvironmentVariable("NATIVE_SYMBOL_WORKER_DIRECTORY");
        var originalImage = Environment.GetEnvironmentVariable("NATIVE_SYMBOL_WORKER_IMAGE");
        var before = Directory.GetDirectories(Path.GetTempPath(), "aptabase-symbol-*").ToHashSet();
        try
        {
            Environment.SetEnvironmentVariable("NATIVE_SYMBOL_WORKER_DIRECTORY", worker);
            Environment.SetEnvironmentVariable("NATIVE_SYMBOL_WORKER_IMAGE", image);
            var processor = new NativeSymbolProcessor();
            Assert.True(processor.Enabled);
            var bytes = await File.ReadAllBytesAsync(fixture);
            var expected = ElfSymbols.Read(bytes);
            var result = await processor.Process(bytes, default);
            Assert.NotNull(result);
            Assert.Equal(expected.BuildId, result.BuildId);
            Assert.Equal(expected.Functions, result.Functions);
            Assert.Null(await processor.Process(new byte[64], default));
            Assert.Null(await processor.Process(new byte[ElfSymbols.MaxBytes + 1], default));

            Environment.SetEnvironmentVariable("NATIVE_SYMBOL_WORKER_DIRECTORY", faultWorker);
            processor = new NativeSymbolProcessor();
            var timer = Stopwatch.StartNew();
            Assert.Null(await processor.Process(new byte[] { 1 }, default));
            Assert.InRange(timer.Elapsed.TotalSeconds, 10, 25);
            foreach (var scenario in new byte[] { 2, 3, 4, 5 })
                Assert.Null(await processor.Process(new[] { scenario }, default));
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.Process(new byte[] { 1 }, cancel.Token));
            Assert.True(processor.Enabled);
            Assert.DoesNotContain(Directory.GetDirectories(Path.GetTempPath(), "aptabase-symbol-*"), p => !before.Contains(p));
            using var docker = Process.Start(new ProcessStartInfo("docker")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                ArgumentList = { "ps", "-a", "--filter", "name=aptabase-symbol-", "--format", "{{.Names}}" }
            })!;
            var containers = await docker.StandardOutput.ReadToEndAsync();
            await docker.WaitForExitAsync();
            Assert.Equal(0, docker.ExitCode);
            Assert.DoesNotContain(containers.Split('\n', StringSplitOptions.RemoveEmptyEntries),
                name => name.Trim().Length == "aptabase-symbol-".Length + 32);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NATIVE_SYMBOL_WORKER_DIRECTORY", originalDirectory);
            Environment.SetEnvironmentVariable("NATIVE_SYMBOL_WORKER_IMAGE", originalImage);
        }
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} for the explicitly configured local worker acceptance test");
}

public class NativeSymbolWorkerFactAttribute : FactAttribute
{
    public NativeSymbolWorkerFactAttribute()
    {
        if (new[] { "NATIVE_SYMBOL_TEST_WORKER_DIRECTORY", "NATIVE_SYMBOL_TEST_FAULT_DIRECTORY", "NATIVE_SYMBOL_TEST_FIXTURE", "NATIVE_SYMBOL_TEST_IMAGE" }
            .Any(name => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))))
            Skip = "Explicit local Docker image, published workers and NDK ELF fixture required; see docs/native-symbols-api.md";
    }
}
