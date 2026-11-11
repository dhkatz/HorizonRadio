using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace HorizonRadio.Sdk.UI.Theme;

/// <summary>The app's theme: ShadUI, Material icons, Horizon Radio's color tokens and text
/// styles. Add it once, to the application's styles.</summary>
public class HorizonRadioTheme : Styles
{
    public HorizonRadioTheme(IServiceProvider? serviceProvider = null)
    {
        AvaloniaXamlLoader.Load(serviceProvider, this);
    }
}
