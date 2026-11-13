using HorizonRadio.Core.Events;

namespace HorizonRadio.Core.Commands;

/// <summary>What a command's one argument names, so a front end can offer the right choices.</summary>
public enum CommandArgument
{
    /// <summary>The command takes no argument.</summary>
    None,

    /// <summary>A source's id (only sources that drive themselves: Spotify Connect, the test tone).</summary>
    Source,

    /// <summary>A saved mix's id.</summary>
    Mix,

    /// <summary>A level from 0 to 1, written with a dot ("0.3").</summary>
    Volume,
}

/// <summary>
/// A named thing the user can have the engine do: from a key binding, an event rule, and (later)
/// the control endpoint and <c>hzr</c>. <see cref="Name"/> is what's typed or sent
/// (<c>playback.next</c>); <see cref="Title"/> and <see cref="Description"/> are what's shown.
/// A binding or a rule keeps its command as an <see cref="EventAction"/> of type
/// <see cref="Action"/>, the form they've always been saved in.
/// </summary>
public sealed record HorizonCommand(
    string Name,
    string Title,
    string Description,
    EventActionType Action,
    CommandArgument Argument = CommandArgument.None)
{
    /// <summary>The saved form of this command with <paramref name="argument"/>.</summary>
    public EventAction ToAction(string? argument = null) =>
        new(Action, Argument == CommandArgument.None ? null : argument);
}

/// <summary>What running a command came to: <see cref="Done"/>, or why not (<see cref="Message"/>,
/// in words for the Console or a remote caller).</summary>
public sealed record CommandResult(bool Done, string? Message = null)
{
    public static readonly CommandResult Ok = new(true);

    public static CommandResult NotDone(string message) => new(false, message);
}
