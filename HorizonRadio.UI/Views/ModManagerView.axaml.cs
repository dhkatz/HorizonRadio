using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HorizonRadio.Core.ModInstall;
using HorizonRadio.UI.ViewModels;

namespace HorizonRadio.UI.Views;

public partial class ModManagerView : UserControl
{
    public ModManagerView()
    {
        InitializeComponent();
    }

    /// <summary>Open the native folder picker so users with non-Steam
    /// installs (Xbox/Game Pass, manual installs) can still target
    /// the right directory.</summary>
    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ModManagerViewModel vm) return;

        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title         = "Pick your Forza Horizon 6 install folder",
            AllowMultiple = false,
        });
        var picked = folders.FirstOrDefault();
        if (picked != null) vm.GamePath = picked.Path.LocalPath;
    }

}
