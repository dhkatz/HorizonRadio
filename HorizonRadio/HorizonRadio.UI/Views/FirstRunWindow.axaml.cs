using HorizonRadio.Core.IO;

namespace HorizonRadio.UI.Views;

public partial class FirstRunWindow : ShadUI.Window
{
    public FirstRunWindow()
    {
        InitializeComponent();
        Title = AppPaths.Label(Title ?? "Horizon Radio");
    }
}
