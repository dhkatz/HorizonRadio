using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace HorizonRadio.Sdk.UI.Controls;

/// <summary>What a <see cref="PathPicker"/> picks.</summary>
public enum PathKind
{
    Folder,
    File,
}

/// <summary>A path you can type or paste, with a Browse button that opens the system's folder or
/// file picker (starting where the current path points).</summary>
public class PathPicker : TemplatedControl
{
    public static readonly StyledProperty<string?> PathProperty =
        AvaloniaProperty.Register<PathPicker, string?>(nameof(Path), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<PathKind> KindProperty =
        AvaloniaProperty.Register<PathPicker, PathKind>(nameof(Kind));

    public static readonly StyledProperty<string?> PlaceholderProperty =
        AvaloniaProperty.Register<PathPicker, string?>(nameof(Placeholder));

    public static readonly StyledProperty<string?> DialogTitleProperty =
        AvaloniaProperty.Register<PathPicker, string?>(nameof(DialogTitle));

    public static readonly StyledProperty<IReadOnlyList<string>?> FileExtensionsProperty =
        AvaloniaProperty.Register<PathPicker, IReadOnlyList<string>?>(nameof(FileExtensions));

    private Button? _browse;

    /// <summary>The chosen path; typed, pasted or picked.</summary>
    public string? Path
    {
        get => GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    public PathKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public string? Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>The picker window's title ("Pick your Forza Horizon 6 install folder").</summary>
    public string? DialogTitle
    {
        get => GetValue(DialogTitleProperty);
        set => SetValue(DialogTitleProperty, value);
    }

    /// <summary>For files: the extensions the picker offers, without the dot ("exe"); any file when empty.</summary>
    public IReadOnlyList<string>? FileExtensions
    {
        get => GetValue(FileExtensionsProperty);
        set => SetValue(FileExtensionsProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_browse != null) _browse.Click -= OnBrowse;
        _browse = e.NameScope.Find<Button>("PART_Browse");
        if (_browse != null) _browse.Click += OnBrowse;
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var start = await StartFolderAsync(storage);

        IStorageItem? picked;
        if (Kind == PathKind.Folder)
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = DialogTitle ?? "Pick a folder",
                SuggestedStartLocation = start,
            });
            picked = folders.Count > 0 ? folders[0] : null;
        }
        else
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = DialogTitle ?? "Pick a file",
                SuggestedStartLocation = start,
                FileTypeFilter = FileExtensions is { Count: > 0 } exts
                    ? [new FilePickerFileType(DialogTitle ?? "Files") { Patterns = [.. exts.Select(x => "*." + x)] }]
                    : null,
            });
            picked = files.Count > 0 ? files[0] : null;
        }

        if (picked?.TryGetLocalPath() is { } local) Path = local;
    }

    // The folder the current path is in (or is), so Browse opens where the user already is.
    private async Task<IStorageFolder?> StartFolderAsync(IStorageProvider storage)
    {
        var path = Path;
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var folder = Directory.Exists(path) ? path : System.IO.Path.GetDirectoryName(path);
            return folder is not null && Directory.Exists(folder)
                ? await storage.TryGetFolderFromPathAsync(folder)
                : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
