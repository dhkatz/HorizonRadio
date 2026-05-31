using System;

namespace HorizonRadio.Core.Events;

/// <summary>
/// A producer of <see cref="GameEvent"/>s. Both the DLL IPC client (memory
/// poller) and the Forza Data Out telemetry listener implement this, so the
/// executor can treat them uniformly and we can add more sources later.
/// Handlers may be invoked on background threads.
/// </summary>
public interface IGameEventSource
{
    event Action<GameEvent>? GameEventReceived;
}
