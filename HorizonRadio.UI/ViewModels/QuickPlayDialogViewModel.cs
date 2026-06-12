using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Sources;
using ShadUI;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// Content for the player-bar "quick play" dialog: picking a content source from
/// the picker pops this up to take a one-off locator (URL / folder / M3U / file)
/// to play right now, without saving a mix. Rendered by the ViewLocator as
/// <c>QuickPlayDialogView</c>. Play/Cancel close the dialog through the
/// <see cref="DialogManager"/>; the caller's success callback reads
/// <see cref="Locator"/>.
/// </summary>
public sealed partial class QuickPlayDialogViewModel : ViewModelBase
{
    private readonly DialogManager _dialogs;

    public string SourceName { get; }
    public string Hint { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    private string locator = "";

    public QuickPlayDialogViewModel(DialogManager dialogs, IAudioSourceFactory source)
    {
        _dialogs = dialogs;
        SourceName = source.DisplayName;
        Hint = source.Id switch
        {
            "youtube" => "https://youtube.com/watch?v=… or /playlist?list=…",
            "local" => @"Folder, M3U, or file (e.g. C:\Music)",
            _ => "URL, folder, or file",
        };
    }

    /// <summary>Designer-only ctor.</summary>
    public QuickPlayDialogViewModel() : this(new DialogManager(), SourceCatalog.All[0]) { }

    private bool CanPlay => !string.IsNullOrWhiteSpace(Locator);

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Play() => _dialogs.Close(this, new CloseDialogOptions { Success = true });

    [RelayCommand]
    private void Cancel() => _dialogs.Close(this);
}
