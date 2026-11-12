using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Metadata;

namespace HorizonRadio.Source.Radio;

/// <summary>
/// What a station plays with: ffmpeg (at the path the source's settings and the Tools tab resolve
/// to), the host's process launching and log, the metadata diagnostics, and the optional title model.
/// </summary>
public sealed record RadioServices(
    string FfmpegPath, IProcesses Processes, IPluginLog Log, IMetadataTrace Trace, ITitleModel TitleModel);
