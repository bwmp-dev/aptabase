using Aptabase.Features;
using Xunit;

namespace Aptabase.UnitTests.Features;

public class ProductionConfigurationTests
{
    [Theory]
    [InlineData("https://collector.example.test/")]
    [InlineData("collector.example.test")]
    public void Normalizes_Production_Origin(string input) =>
        Assert.Equal("https://collector.example.test", EnvSettings.NormalizeBaseUrl(input, false));

    [Theory]
    [InlineData("http://collector.example.test")]
    [InlineData("https://name:secret@collector.example.test")]
    [InlineData("https://collector.example.test/subpath")]
    [InlineData("https://collector.example.test?secret=private")]
    [InlineData("https://collector.example.test#fragment")]
    [InlineData("ftp://collector.example.test")]
    [InlineData("")]
    public void Rejects_Insecure_Or_Unsupported_Production_Origins(string input)
    {
        var exception = Assert.Throws<Exception>(() => EnvSettings.NormalizeBaseUrl(input, false));
        Assert.DoesNotContain("secret=private", exception.Message);
        Assert.DoesNotContain("name:secret", exception.Message);
    }

    [Fact]
    public void Development_Keeps_Loopback_Http() =>
        Assert.Equal("http://127.0.0.1:3000", EnvSettings.NormalizeBaseUrl("http://127.0.0.1:3000/", true));

    [Fact]
    public void Key_Directory_Must_Be_Absolute_And_Is_Not_Created_By_Validation()
    {
        Assert.Equal("", EnvSettings.ValidateKeyDirectory(""));
        Assert.Throws<Exception>(() => EnvSettings.ValidateKeyDirectory("relative/keys"));
        var directory = Path.Combine(Path.GetTempPath(), "aptabase-keys-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(directory, EnvSettings.ValidateKeyDirectory(directory));
        Assert.False(Directory.Exists(directory));
    }
}
