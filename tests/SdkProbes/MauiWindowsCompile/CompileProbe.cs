using Microsoft.Maui.Hosting;

namespace Aptabase.SdkProbes;

// Compile-only contract: this library is never launched and creates no app or window.
public static class CompileProbe
{
    public static MauiAppBuilder Configure(MauiAppBuilder builder, string dsn) => builder.UseSentry(options => {
        options.Dsn = dsn;
        options.Release = "synthetic-maui-windows";
        options.Environment = "synthetic";
        options.SendDefaultPii = false;
        options.SendClientReports = false;
        options.AutoSessionTracking = false;
    });
}
