using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.UI.Tools;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// Base VM for one row of a source's config form. Subclasses carry the
/// type-specific bound value (a string for paths, a bool for toggles,
/// etc.) and any per-control commands. The view picks a DataTemplate
/// by VM type, so adding a new field kind = new subclass + new template.
/// </summary>
public abstract partial class ConfigFieldViewModel : ViewModelBase
{
    public ConfigField Field { get; }

    public string Key => Field.Key;
    public string Label => Field.Label;
    public string? Description => Field.Description;

    protected ConfigFieldViewModel(ConfigField field) { Field = field; }

    /// <summary>Read the user-supplied value out of the VM in whatever
    /// JSON-friendly form <see cref="ConfigValues"/> expects.</summary>
    public abstract object? GetValue();

    /// <summary>Apply a previously-persisted value to the VM. Called once
    /// when the form is hydrated.</summary>
    public abstract void SetValue(object? value);

    public static ConfigFieldViewModel For(ConfigField field, ToolRegistry? toolRegistry = null) => field switch
    {
        DirectoryField d => new DirectoryFieldViewModel(d),
        ToolField t => new ToolFieldViewModel(t, toolRegistry),
        FileField f => new FileFieldViewModel(f),
        TextField t => new TextFieldViewModel(t),
        BoolField b => new BoolFieldViewModel(b),
        EnumField e => new EnumFieldViewModel(e),
        _ => throw new InvalidOperationException($"Unsupported field type: {field.GetType().Name}"),
    };
}

public sealed partial class DirectoryFieldViewModel : ConfigFieldViewModel
{
    [ObservableProperty] private string? path;

    /// <summary>Raised when the user clicks Browse. The view subscribes,
    /// opens the native folder picker via its top-level StorageProvider,
    /// and writes the result back into <see cref="Path"/>. Done as an
    /// event rather than a Command-with-injection so the VM stays free
    /// of Avalonia types.</summary>
    public event Action<DirectoryFieldViewModel>? BrowseRequested;

    public DirectoryFieldViewModel(DirectoryField field) : base(field) { Path = field.Default; }

    public void RequestBrowse() => BrowseRequested?.Invoke(this);

    public override object? GetValue() => Path;
    public override void SetValue(object? value) => Path = value as string;
}

public sealed partial class FileFieldViewModel : ConfigFieldViewModel
{
    [ObservableProperty] private string? path;

    public event Action<FileFieldViewModel>? BrowseRequested;

    public FileField FileField { get; }

    public FileFieldViewModel(FileField field) : base(field)
    {
        FileField = field;
        Path = field.Default;
    }

    public void RequestBrowse() => BrowseRequested?.Invoke(this);

    public override object? GetValue() => Path;
    public override void SetValue(object? value) => Path = value as string;
}

public sealed partial class TextFieldViewModel : ConfigFieldViewModel
{
    [ObservableProperty] private string? text;

    public bool IsSecret { get; }
    public string? Placeholder { get; }

    /// <summary>Avalonia TextBox.PasswordChar takes a char; '\0' means
    /// "render plainly". Bind this directly instead of running through
    /// a converter so the view stays trivial.</summary>
    public char PasswordChar => IsSecret ? '•' : '\0';

    public TextFieldViewModel(TextField field) : base(field)
    {
        IsSecret = field.IsSecret;
        Placeholder = field.Placeholder;
        Text = field.Default;
    }

    public override object? GetValue() => Text;
    public override void SetValue(object? value) => Text = value as string;
}

public sealed partial class BoolFieldViewModel : ConfigFieldViewModel
{
    [ObservableProperty] private bool isOn;

    public BoolFieldViewModel(BoolField field) : base(field) { IsOn = field.Default; }

    public override object? GetValue() => IsOn;
    public override void SetValue(object? value) => IsOn = value is bool b && b;
}

/// <summary>
/// Renderer for a <see cref="ToolField"/>. Behaves like a FileField (a
/// path + browse button) but with a dropdown of detected installed
/// tools of the matching kind. Picking an item populates the path
/// field; the user can still type or browse for a custom path. The VM
/// subscribes to <see cref="ToolRegistry.Changed"/> so a fresh install
/// from the Tools tab shows up in the dropdown immediately.
/// </summary>
public sealed partial class ToolFieldViewModel : ConfigFieldViewModel
{
    [ObservableProperty] private string? path;

    public ToolField ToolField { get; }
    public ObservableCollection<InstalledTool> Available { get; } = new();
    public bool HasAvailable => Available.Count > 0;

    private readonly ToolRegistry? _registry;

    public event Action<ToolFieldViewModel>? BrowseRequested;

    public ToolFieldViewModel(ToolField field, ToolRegistry? registry) : base(field)
    {
        ToolField = field;
        _registry = registry;
        Path = field.Default;

        if (_registry != null)
        {
            _registry.Changed += RefreshFromRegistry;
            RefreshFromRegistry();
        }
    }

    private void RefreshFromRegistry()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Available.Clear();
            if (_registry == null) return;
            foreach (var t in _registry.ForKind(ToolField.ToolKind))
                Available.Add(t);
            OnPropertyChanged(nameof(HasAvailable));

            // If the path is empty and we just discovered an installed
            // tool, auto-fill — saves the user a click for the common case.
            if (string.IsNullOrWhiteSpace(Path) && Available.Count > 0)
                Path = Available[0].Path;
        });
    }

    /// <summary>Called from the view when the user picks an item from
    /// the dropdown. Sets <see cref="Path"/> to the tool's path so the
    /// textbox reflects the choice.</summary>
    public void PickInstalled(InstalledTool tool) => Path = tool.Path;

    public void RequestBrowse() => BrowseRequested?.Invoke(this);

    public override object? GetValue() => Path;
    public override void SetValue(object? value) => Path = value as string;
}

public sealed partial class EnumFieldViewModel : ConfigFieldViewModel
{
    [ObservableProperty] private string? selected;

    public System.Collections.Generic.IReadOnlyList<string> Options { get; }

    public EnumFieldViewModel(EnumField field) : base(field)
    {
        Options = field.Options;
        Selected = field.Default ?? (field.Options.Count > 0 ? field.Options[0] : null);
    }

    public override object? GetValue() => Selected;
    public override void SetValue(object? value) => Selected = value as string;
}
