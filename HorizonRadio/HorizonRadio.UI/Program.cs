using System;
using Avalonia;
using Avalonia.Media;
using HorizonRadio.Core.Tools;
using HorizonRadio.UI.Tools;

namespace HorizonRadio.UI;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        // Seed the tool catalog before any Avalonia/App code runs: App setup constructs source
        // factories that probe for their tool (e.g. Spotify → librespot) via the catalog, and this is
        // the one path both the runtime (Main) and the visual designer go through. Not Avalonia/
        // third-party, so it's safe to run here ahead of AppMain (see the note above).
        ToolCatalog.Initialize(ToolPlugins.Descriptors);

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
