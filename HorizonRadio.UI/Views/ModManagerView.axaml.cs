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

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ModManagerViewModel vm) return;

        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Pick your Forza Horizon 6 install folder",
            AllowMultiple = false,
        });
        var picked = folders.Count > 0 ? folders[0] : null;
        if (picked != null) vm.GamePath = picked.Path.LocalPath;
    }

}
