using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using HorizonRadio.Core.IO;

namespace HorizonRadio.UI;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // --profile <name> keeps this instance's files apart from the default one's (AppPaths),
        // so it has to be decided before anything reads a path. A bad name stops here rather than
        // falling back to the default profile, whose data a test instance would then overwrite.
        if (!AppPaths.TryParseProfile(args, out var profile, out var error))
        {
            _ = MessageBoxW(IntPtr.Zero, error, "Horizon Radio", MessageBoxIconError);
            return 2;
        }
        AppPaths.UseProfile(profile);

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private const uint MessageBoxIconError = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string? text, string caption, uint type);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            // Inter has no CJK glyphs, so Japanese / Chinese / Korean
            // track titles render as tofu without a fallback. Add the
            // platform fonts that cover the relevant scripts; leave
            // DefaultFamilyName unset so WithInterFont's embedded
            // resource registration stays in charge of the primary
            // family (setting DefaultFamilyName = "Inter" doesn't
            // resolve — Inter is registered under a fonts: URI, not the
            // bare family name).
            .With(new FontManagerOptions
            {
                FontFallbacks = new[]
                {
                    new FontFallback { FontFamily = new FontFamily("Yu Gothic UI") },
                    new FontFallback { FontFamily = new FontFamily("Meiryo UI") },
                    new FontFallback { FontFamily = new FontFamily("Microsoft YaHei UI") },
                    new FontFallback { FontFamily = new FontFamily("Malgun Gothic") },
                    new FontFallback { FontFamily = new FontFamily("Segoe UI Symbol") },
                },
            })
            .LogToTrace();
    }
}
