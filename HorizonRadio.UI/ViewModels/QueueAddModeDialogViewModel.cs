using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Sources.Queue;
using ShadUI;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// Content for the "replace or add to queue" prompt shown when the user starts a
/// mix while the queue already has content. The queue's tail is a single context,
/// so the choice is real: replace the station the queue draws from, or stack one
/// lap of this mix ahead of what's already playing. Both close with success; the
/// caller reads <see cref="Mode"/>. Rendered by the ViewLocator as
/// <c>QueueAddModeDialogView</c>.
/// </summary>
public sealed partial class QueueAddModeDialogViewModel : ViewModelBase
{
    private readonly DialogManager _dialogs;

    public string MixName { get; }

    /// <summary>The user's choice, read by the caller's success callback.</summary>
    public QueueAddMode Mode { get; private set; } = QueueAddMode.Replace;

    public QueueAddModeDialogViewModel(DialogManager dialogs, string mixName)
    {
        _dialogs = dialogs;
        MixName = mixName;
    }

    /// <summary>Designer-only ctor.</summary>
    public QueueAddModeDialogViewModel() : this(new DialogManager(), "This Mix") { }

    [RelayCommand]
    private void Replace()
    {
        Mode = QueueAddMode.Replace;
        _dialogs.Close(this, new CloseDialogOptions { Success = true });
    }

    [RelayCommand]
    private void AddToQueue()
    {
        Mode = QueueAddMode.Add;
        _dialogs.Close(this, new CloseDialogOptions { Success = true });
    }

    [RelayCommand]
    private void Cancel() => _dialogs.Close(this);
}
