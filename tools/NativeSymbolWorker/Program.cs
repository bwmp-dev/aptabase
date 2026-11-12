using System.Text.Json;
using Aptabase.Features.Symbolication;

// The isolated process reads one fixed, read-only mount. No client-controlled path,
// shell, dynamic library loading, external symbol server or source lookup exists.
// A collector crash must not leave an orphaned parser consuming CPU indefinitely.
using var lifetime = new Timer(_ => Environment.Exit(3), null, TimeSpan.FromSeconds(12), Timeout.InfiniteTimeSpan);
try
{
    const string path = "/input/artifact.elf";
    if (new FileInfo(path).Length > ElfSymbols.MaxBytes) throw new FormatException("Artifact too large");
    var index = ElfSymbols.Read(File.ReadAllBytes(path));
    Console.Write(JsonSerializer.Serialize(index));
}
catch (Exception error) when (error is FormatException or IOException or System.Text.DecoderFallbackException)
{
    Console.Error.Write("Unsupported or invalid symbol artifact");
    Environment.ExitCode = 2;
}
