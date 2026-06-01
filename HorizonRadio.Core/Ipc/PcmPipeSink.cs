using System;
using HorizonRadio.Core.Sources;

namespace HorizonRadio.Core.Ipc;

/// <summary>
/// <see cref="IPcmSink"/> backed by <see cref="PcmPipeClient"/>. Just a
/// thin façade — sources hold an IPcmSink rather than a PcmPipeClient
/// directly so we can swap in a fake for tests.
/// </summary>
public sealed class PcmPipeSink(PcmPipeClient client) : IPcmSink
{
    public bool Send(ReadOnlySpan<short> samples) => client.Send(samples);
}
