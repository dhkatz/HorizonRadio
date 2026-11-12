using HorizonRadio.Sdk.Tools;

namespace HorizonRadio.Tools.TitleModel;

/// <summary>title-model tool plugin — optional local GGUF for title extraction. Pinned model build.</summary>
public sealed class TitleModelToolPlugin : IToolPlugin
{
    public int SortOrder => 50;

    public ToolDescriptor Descriptor { get; } = new(ToolKind.TitleModel, "title-model.gguf", IsData: true);
    public IToolInstaller Installer { get; } = new TitleModelInstaller();
}
