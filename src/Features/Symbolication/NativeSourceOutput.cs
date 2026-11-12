using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aptabase.Features.ErrorReporting;

namespace Aptabase.Features.Symbolication;

public static class NativeSourceOutput
{
    public const int MaxAddresses = 65536;
    public const int MaxOutput = 16 * 1024 * 1024;

    public static ulong[]? Addresses(NativeSymbolIndex index)
    {
        var addresses = new SortedSet<ulong>();
        var inspected = 0;
        foreach (var function in index.Functions)
        {
            if (function.Address % 4 != 0 || function.Size > MaxAddresses * 4UL) return null;
            for (ulong offset = 0; offset < function.Size; offset += 4)
            {
                // Overlapping symbol ranges must not multiply preprocessing work without bound.
                if (++inspected > MaxAddresses) return null;
                addresses.Add(checked(function.Address + offset));
                if (addresses.Count > MaxAddresses) return null;
            }
        }
        return addresses.ToArray();
    }

    public static NativeSourceAddress[] Parse(string output, ulong[] expected, IPiiSanitizer sanitizer)
    {
        if (output.Length > MaxOutput || expected.Length > MaxAddresses) throw new FormatException("Source output limit exceeded");
        using var reader = new StringReader(output);
        var result = new List<NativeSourceAddress>();
        var header = reader.ReadLine();
        foreach (var address in expected)
        {
            if (header is null || !Regex.IsMatch(header, @"\A0x[0-9a-fA-F]{1,16}\z") ||
                ulong.Parse(header.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) != address)
                throw new FormatException("Source address mismatch");
            var locations = new List<NativeSourceLocation>();
            var entries = 0;
            while ((header = reader.ReadLine()) is not null && !header.StartsWith("0x", StringComparison.Ordinal))
            {
                if (++entries > 16 || header.Length is < 1 or > 512 || header.Any(char.IsControl)) throw new FormatException("Invalid source function");
                var location = reader.ReadLine();
                if (location is null || location.Length > 4096 || location.Any(char.IsControl)) throw new FormatException("Invalid source location");
                var match = Regex.Match(location, @"\A(.+):(\d+)(?: \(discriminator \d+\))?\z");
                if (location == "??:?" || location == "??:0") continue;
                if (!match.Success || !int.TryParse(match.Groups[2].Value, out var line) || line <= 0) throw new FormatException("Invalid source line");
                // Defence in depth: GNU's basename option is not the privacy boundary.
                var file = match.Groups[1].Value.Replace('\\', '/').Split('/')[^1];
                if (!Regex.IsMatch(file, @"\A[a-zA-Z0-9_.+\-]{1,128}\z") || file is "." or "..") throw new FormatException("Invalid source basename");
                locations.Add(new(sanitizer.SanitizeLabel(header, 512), sanitizer.SanitizeLabel(file, 128), line));
            }
            if (entries == 0) throw new FormatException("Missing source result");
            if (locations.Count > 0) result.Add(new(address, locations.ToArray()));
        }
        if (header is not null) throw new FormatException("Unexpected source output");
        if (JsonSerializer.SerializeToUtf8Bytes(result).Length > MaxOutput) throw new FormatException("Source index limit exceeded");
        return result.ToArray();
    }
}
