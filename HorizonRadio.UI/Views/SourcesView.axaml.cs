using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HorizonRadio.UI.ViewModels;

namespace HorizonRadio.UI.Views;

public partial class SourcesView : UserControl
{
    public SourcesView()
    {
        InitializeComponent();
    }

    /// <summary>Open a native folder picker for the DirectoryField row
    /// whose Browse button was clicked. The VM is passed via the
    /// button's Tag so we don't need to walk the visual tree.</summary>
    private async void BrowseDirectory_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not DirectoryFieldViewModel vm) return;

        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title           = $"Pick folder: {vm.Label}",
            AllowMultiple   = false,
        });
        var picked = folders.FirstOrDefault();
        if (picked != null) vm.Path = picked.Path.LocalPath;
    }

    private async void BrowseFile_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not FileFieldViewModel vm) return;

        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        // Build a single FileTypeFilter from the field's extension list.
        // If none was supplied, fall back to all files.
        var filters = vm.FileField.ExtensionFilter is { Count: > 0 } exts
            ? new[]
              {
                  new FilePickerFileType(vm.Label)
                  {
                      Patterns = exts.Select(x => "*." + x).ToArray(),
                  },
              }
            : null;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = $"Pick file: {vm.Label}",
            AllowMultiple = false,
            FileTypeFilter = filters,
        });
        var picked = files.FirstOrDefault();
        if (picked != null) vm.Path = picked.Path.LocalPath;
    }
}
