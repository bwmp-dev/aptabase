using Aptabase.Features.Sentry;
using Xunit;

namespace Aptabase.UnitTests.Features.Sentry;

public class SentryIngestionKeyTests
{
    private static SentryProject Project(string app = "app-a", long id = 1, bool debug = false, string key = "A-EU-1234567890") =>
        new() { AppId = app, Id = id, IsDebug = debug, AppKey = key };

    [Fact]
    public void Produces_Stable_Alphanumeric_Dsn_Key_And_Accepts_Legacy_Key()
    {
        var project = Project();
        var key = SentryIngestionKey.Create(project);
        Assert.Matches("^[a-f0-9]{64}$", key);
        Assert.Equal(key, SentryIngestionKey.Create(Project()));
        Assert.True(SentryIngestionKey.Matches(project, key));
        Assert.True(SentryIngestionKey.Matches(project, project.AppKey));
        Assert.False(SentryIngestionKey.Matches(project, key.ToUpperInvariant()));
        Assert.False(SentryIngestionKey.Matches(project, key[..63]));
    }

    [Theory]
    [InlineData("app-b", 1, false, "A-EU-1234567890")]
    [InlineData("app-a", 2, false, "A-EU-1234567890")]
    [InlineData("app-a", 1, true, "A-EU-1234567890")]
    [InlineData("app-a", 1, false, "A-EU-0987654321")]
    public void Derived_Credential_Is_Bound_To_App_Project_Mode_And_Current_Key(string app, long id, bool debug, string key)
    {
        var issued = SentryIngestionKey.Create(Project());
        Assert.False(SentryIngestionKey.Matches(Project(app, id, debug, key), issued));
    }

    [Fact]
    public void Rotation_Revokes_Both_Previous_Representations()
    {
        var old = Project();
        var rotated = Project(key: "A-EU-0987654321");
        Assert.False(SentryIngestionKey.Matches(rotated, old.AppKey));
        Assert.False(SentryIngestionKey.Matches(rotated, SentryIngestionKey.Create(old)));
        Assert.True(SentryIngestionKey.Matches(rotated, SentryIngestionKey.Create(rotated)));
    }
}
