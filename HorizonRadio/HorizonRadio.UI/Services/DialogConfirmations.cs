using System.Threading.Tasks;
using Avalonia.Threading;
using HorizonRadio.Sdk;
using ShadUI;

namespace HorizonRadio.UI.Services;

/// <summary>Asks in a dialog: the action's button (red when it destroys something) and Cancel.</summary>
public sealed class DialogConfirmations(DialogManager dialogs) : IConfirmations
{
    public Task<bool> ConfirmAsync(Confirmation confirmation)
    {
        // Not dismissible by clicking outside: the dialog only closes through a button, so the
        // answer always arrives.
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => dialogs.CreateDialog(confirmation.Title, confirmation.Message)
            .WithPrimaryButton(confirmation.ConfirmLabel, () => answer.TrySetResult(true),
                confirmation.Destructive ? DialogButtonStyle.Destructive : DialogButtonStyle.Primary)
            .WithCancelButton("Cancel", () => answer.TrySetResult(false), DialogButtonStyle.Outline)
            .WithMaxWidth(440)
            .Show());
        return answer.Task;
    }
}
