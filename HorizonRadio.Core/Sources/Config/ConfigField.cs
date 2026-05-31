namespace HorizonRadio.Core.Sources.Config;

/// <summary>
/// Schema element describing one configurable field on an audio source.
/// Concrete subtypes carry the per-control-kind metadata (default,
/// validation hint, enum options, etc.). The UI renders one widget per
/// field type and writes user input into a <see cref="ConfigValues"/>
/// bag keyed by <see cref="Key"/>.
///
/// Adding a new field type means: subclass here, add a corresponding
/// ConfigFieldViewModel + DataTemplate in the UI, teach
/// <see cref="ConfigValues"/> the serialization rule. The factory
/// contract itself doesn't change.
/// </summary>
public abstract record ConfigField(string Key, string Label, string? Description = null)
{
    /// <summary>Default value to seed the UI control with when no
    /// persisted value exists yet. May be null.</summary>
    public abstract object? DefaultValue { get; }
}

/// <summary>Filesystem directory. UI renders a textbox + Browse button
/// that opens a native folder picker.</summary>
public sealed record DirectoryField(
    string Key,
    string Label,
    string? Default = null,
    string? Description = null)
    : ConfigField(Key, Label, Description)
{
    public override object? DefaultValue => Default;
}

/// <summary>Filesystem file. UI renders a textbox + Browse button
/// that opens a native file picker. <paramref name="ExtensionFilter"/>
/// is a list like {"mp3","flac"} (no dots) used as the picker filter.</summary>
public sealed record FileField(
    string Key,
    string Label,
    IReadOnlyList<string>? ExtensionFilter = null,
    string? Default = null,
    string? Description = null)
    : ConfigField(Key, Label, Description)
{
    public override object? DefaultValue => Default;
}

/// <summary>
/// Path to an external tool the source depends on (yt-dlp, ffmpeg,
/// librespot, …). Behaves like a <see cref="FileField"/> but tagged
/// with a <paramref name="ToolKind"/> so the UI's tool registry can
/// suggest already-installed copies in a dropdown above the manual
/// path entry. The kind is a free-form lowercase string ("yt-dlp",
/// "ffmpeg", "librespot"); both sides must agree on it. Multiple
/// sources may reference the same kind — that's the point of having
/// a separate Tools tab as the install surface.
///
/// Core stays oblivious to where managed tools land; it just emits
/// the kind. UI fills in the dropdown and resolves the path the user
/// picks back into a plain string in <see cref="ConfigValues"/>, so
/// <see cref="IAudioSourceFactory.Create"/> reads it via GetString
/// exactly like a FileField.
/// </summary>
public sealed record ToolField(
    string Key,
    string Label,
    string ToolKind,
    string? Default = null,
    string? Description = null)
    : ConfigField(Key, Label, Description)
{
    public override object? DefaultValue => Default;
}

/// <summary>Plain text. <paramref name="IsSecret"/> hints the UI to use
/// a masked input (passwords, tokens).</summary>
public sealed record TextField(
    string Key,
    string Label,
    string? Default = null,
    bool IsSecret = false,
    string? Placeholder = null,
    string? Description = null)
    : ConfigField(Key, Label, Description)
{
    public override object? DefaultValue => Default;
}

/// <summary>Boolean toggle.</summary>
public sealed record BoolField(
    string Key,
    string Label,
    bool Default = false,
    string? Description = null)
    : ConfigField(Key, Label, Description)
{
    public override object? DefaultValue => Default;
}

/// <summary>Single-select from a fixed list of string values. The
/// raw string is what gets stored in <see cref="ConfigValues"/>.</summary>
public sealed record EnumField(
    string Key,
    string Label,
    IReadOnlyList<string> Options,
    string? Default = null,
    string? Description = null)
    : ConfigField(Key, Label, Description)
{
    public override object? DefaultValue =>
        Default ?? (Options.Count > 0 ? Options[0] : null);
}
