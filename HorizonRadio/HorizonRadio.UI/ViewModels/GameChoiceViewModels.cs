using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Games;
using HorizonRadio.Core.Settings;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Plugins.Abstractions;

namespace HorizonRadio.UI.ViewModels;

/// <summary>One game the user can pick (or "No Game").</summary>
public sealed record GameOption(string Id, string Name, string Description);

/// <summary>The list of games to choose from, shared by the first-run window and Settings → Game.</summary>
public sealed partial class GameChoiceViewModel : ViewModelBase
{
    public GameChoiceViewModel(IEnumerable<IGamePlugin> games, string? selectedId)
    {
        Options =
        [
            .. games.Select(g => new GameOption(g.Id, g.DisplayName,
                "Horizon Radio plays through the game's own radio, with in-game titles and events.")),
            new GameOption(NoGameIntegration.Id, "No Game",
                "Just a music player: Horizon Radio plays through your speakers."),
        ];
        selected = Options.FirstOrDefault(o => o.Id == selectedId);
    }

    /// <summary>Designer-only ctor.</summary>
    public GameChoiceViewModel() : this([], null) { }

    public IReadOnlyList<GameOption> Options { get; }

    [ObservableProperty] private GameOption? selected;
}

/// <summary>
/// First launch: which game do you play? Shown before anything game-related is built, so the
/// choice applies right away. Existing users who already set a game up never see it.
/// </summary>
public sealed partial class FirstRunViewModel : ViewModelBase
{
    private readonly SettingsStore? _settings;

    public FirstRunViewModel(IEnumerable<IGamePlugin> games, SettingsStore settings)
    {
        _settings = settings;
        Choice = new GameChoiceViewModel(games, null);
        Choice.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GameChoiceViewModel.Selected)) ContinueCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>Designer-only ctor.</summary>
    public FirstRunViewModel() => Choice = new GameChoiceViewModel();

    public GameChoiceViewModel Choice { get; }

    /// <summary>Raised with the chosen game id once saved.</summary>
    public event Action<string>? Chosen;

    private bool CanContinue => Choice.Selected != null;

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private void Continue()
    {
        if (Choice.Selected is not { } option || _settings == null) return;
        _settings.Set(GameSelection.Chosen, option.Id);
        Chosen?.Invoke(option.Id);
    }
}

/// <summary>
/// Settings → Game: switch which game Horizon Radio runs with. Everything game-related (audio
/// output, events, pages) is wired at startup, so switching saves the choice and restarts.
/// </summary>
public sealed partial class GameSettingsViewModel : ViewModelBase
{
    private readonly SettingsStore? _settings;

    public GameSettingsViewModel(IEnumerable<IGamePlugin> games, SettingsStore settings, string currentId)
    {
        _settings = settings;
        CurrentId = currentId;
        Choice = new GameChoiceViewModel(games, currentId);
        CurrentName = Choice.Options.FirstOrDefault(o => o.Id == currentId)?.Name ?? currentId;
        Choice.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(GameChoiceViewModel.Selected)) return;
            OnPropertyChanged(nameof(HasChange));
            SwitchCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>Designer-only ctor.</summary>
    public GameSettingsViewModel()
    {
        Choice = new GameChoiceViewModel();
        CurrentId = NoGameIntegration.Id;
        CurrentName = "No Game";
    }

    public GameChoiceViewModel Choice { get; }

    public string CurrentId { get; }

    public string CurrentName { get; }

    public bool HasChange => Choice.Selected != null && Choice.Selected.Id != CurrentId;

    /// <summary>Raised after the new choice is saved; the app restarts to apply it.</summary>
    public event Action? RestartRequested;

    [RelayCommand(CanExecute = nameof(HasChange))]
    private void Switch()
    {
        if (Choice.Selected is not { } option || _settings == null) return;
        _settings.Set(GameSelection.Chosen, option.Id);
        // Written now: the restart starts the new process before this one has exited.
        _settings.Flush();
        RestartRequested?.Invoke();
    }
}
