using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HorizonRadio.UI.ViewModels;

/// <summary>A toggle chip above the search results that includes/excludes one source.
/// Toggling re-runs the search scoped to the enabled chips.</summary>
public sealed partial class SourceFilterChipViewModel : ViewModelBase
{
    private readonly Action _onToggled;

    public string Id { get; }
    public string DisplayName { get; }

    [ObservableProperty] private bool isEnabled = true;

    public ICommand ToggleCommand { get; }

    public SourceFilterChipViewModel(string id, string displayName, Action onToggled)
    {
        Id = id;
        DisplayName = displayName;
        _onToggled = onToggled;
        ToggleCommand = new RelayCommand(() => { IsEnabled = !IsEnabled; _onToggled(); });
    }
}
