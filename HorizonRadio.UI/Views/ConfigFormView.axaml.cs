using System.Collections;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HorizonRadio.Core.Tools;
using HorizonRadio.UI.Tools;
using HorizonRadio.UI.ViewModels;

namespace HorizonRadio.UI.Views;

/// <summary>
/// Renders a source's config form from a collection of
/// <see cref="ConfigFieldViewModel"/> (one widget per field type) and owns the
/// native file/folder pickers, used by the Sources tab to configure a source's
/// engine fields.
/// </summary>
public partial class ConfigFormView : UserControl
{
    private static readonly string[] ExePatterns = ["*.exe"];

    /// <summary>The config-field view models to render.</summary>
    public static readonly StyledProperty<IEnumerable?> FieldsProperty =
        AvaloniaProperty.Register<ConfigFormView, IEnumerable?>(nameof(Fields));

    public IEnumerable? Fields
    {
        get => GetValue(FieldsProperty);
        set => SetValue(FieldsProperty, value);
    }

    public ConfigFormView()
    {
        InitializeComponent();
    }

    private void ToolPick_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.Tag is not ToolFieldViewModel vm) return;
        if (combo.SelectedItem is InstalledTool tool) vm.PickInstalled(tool);
    }

    private async void BrowseTool_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ToolFieldViewModel vm) return;

        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Pick file: {vm.Label}",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(vm.Label) { Patterns = ExePatterns },
            },
        });
        var picked = files.Count > 0 ? files[0] : null;
        if (picked != null) vm.Path = picked.Path.LocalPath;
    }

    private async void BrowseDirectory_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not DirectoryFieldViewModel vm) return;

        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Pick folder: {vm.Label}",
            AllowMultiple = false,
        });
        var picked = folders.Count > 0 ? folders[0] : null;
        if (picked != null) vm.Path = picked.Path.LocalPath;
    }

    private async void BrowseFile_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not FileFieldViewModel vm) return;

        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

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
            Title = $"Pick file: {vm.Label}",
            AllowMultiple = false,
            FileTypeFilter = filters,
        });
        var picked = files.Count > 0 ? files[0] : null;
        if (picked != null) vm.Path = picked.Path.LocalPath;
    }
}
