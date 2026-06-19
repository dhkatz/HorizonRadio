namespace HorizonRadio.Core.Tools;

/// <summary>
/// Identity of an external tool the app provisions: its <see cref="Id"/> (the kind string a source
/// tags its <see cref="Sources.Config.ToolField"/> with), the on-disk <see cref="FileName"/> once
/// installed, and whether it's a data file (a model) rather than an executable. This is the
/// path/identity half of a tool; the install/freshness behaviour lives with the tool's installer.
/// </summary>
public sealed record ToolDescriptor(string Id, string FileName, bool IsData = false);
