using System.Threading.Tasks;

namespace HorizonRadio.Core.Events;

/// <summary>
/// Runs a saved <see cref="EventAction"/>: what an event rule (the Events tab) and a key binding
/// (the Controls tab) both do with theirs. The engine's is its command list
/// (<see cref="Commands.CommandList"/>), so both share the same checks and the same Console lines.
/// </summary>
public interface IActionDispatcher
{
    Task RunAsync(EventAction action);

    /// <summary>The action in words, for an activity list or the Console.</summary>
    string Describe(EventAction action);
}
