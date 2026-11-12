using System.Globalization;
using System.Text.RegularExpressions;
namespace Aptabase.Features.Symbolication;

public record NativeFrame(string BuildId, string Address)
{
    public bool IsValid() => BuildId is not null && Regex.IsMatch(BuildId, @"\A[a-f0-9]{32,64}\z") && BuildId.Length % 2 == 0 &&
        Address is not null && Regex.IsMatch(Address, @"\A0x[0-9a-f]{1,16}\z");
    [System.Text.Json.Serialization.JsonIgnore]
    public ulong Offset => ulong.Parse(Address.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    [System.Text.Json.Serialization.JsonIgnore]
    public string RawLine => $"at native {BuildId}+0x{Offset:x}";
}
