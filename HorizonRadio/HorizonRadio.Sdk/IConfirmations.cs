namespace HorizonRadio.Sdk;

/// <summary>A question for the user before something that can't be undone: what's about to happen
/// (<paramref name="Title"/>, <paramref name="Message"/>) and the button that does it
/// (<paramref name="ConfirmLabel"/>, drawn as dangerous when <paramref name="Destructive"/>).</summary>
public sealed record Confirmation(string Title, string Message, string ConfirmLabel, bool Destructive = true);

/// <summary>Asks the user to confirm an action. The app shows a dialog; a host with no one to ask
/// (tests, a headless run) answers for them.</summary>
public interface IConfirmations
{
    /// <summary>True when the user chose to go ahead, false when they backed out.</summary>
    Task<bool> ConfirmAsync(Confirmation confirmation);
}

/// <summary>Goes ahead without asking: for hosts with no one to ask.</summary>
public sealed class ProceedWithoutAsking : IConfirmations
{
    public static readonly ProceedWithoutAsking Instance = new();

    public Task<bool> ConfirmAsync(Confirmation confirmation) => Task.FromResult(true);
}
