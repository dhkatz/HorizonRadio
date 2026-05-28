using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// State of the Now Playing tab. Populated by the IPC client from
/// DLL events (`{"event":"track",...}`) and by the local
/// <see cref="SourceRunner"/> when a C#-side source publishes metadata.
///
/// Also hosts an inline source switcher that drives the same
/// <see cref="SourceRunner"/> the Sources tab uses — picking from this
/// dropdown starts the chosen source with its persisted config. If the
/// source has never been configured (or its config no longer
/// validates), the switch fails silently here and the user can fix it
/// from the Sources tab; <see cref="SwitchStatus"/> carries the message.
/// </summary>
public sealed partial class NowPlayingViewModel : ViewModelBase
{
    [ObservableProperty] private string title = "Nothing playing";
    [ObservableProperty] private string artist = "Launch Forza Horizon 6 with the mod installed";
    [ObservableProperty] private string? album;
    [ObservableProperty] private string sourceDisplay = "—";
    [ObservableProperty] private string sourceId = "";
    [ObservableProperty] private Bitmap? albumArt;
    [ObservableProperty] private bool isConnected;

    /// <summary>True once a source has actually emitted a track. The
    /// "Source: …" pill in the Now Playing card binds to this so the
    /// pill stays hidden during the empty-state placeholder, where
    /// SourceDisplay is "—" and the pill would look orphan-y.</summary>
    public bool HasActiveSource => !string.IsNullOrEmpty(SourceId);
    partial void OnSourceIdChanged(string value) => OnPropertyChanged(nameof(HasActiveSource));

    [ObservableProperty] private IAudioSourceFactory? selectedFactory;
    [ObservableProperty] private string? switchStatus;

    [ObservableProperty] private bool canPause;
    [ObservableProperty] private bool canSkipNext;
    [ObservableProperty] private bool canSkipPrevious;
    [ObservableProperty] private bool isPaused;
    [ObservableProperty] private bool hasTransport;

    private readonly SourceRunner?      _runner;
    private readonly SourceConfigStore? _store;
    private bool _suppressSwitch;
    private ITransportControls? _transport;

    public IReadOnlyList<IAudioSourceFactory> AvailableSources { get; }

    public NowPlayingViewModel(SourceRunner runner, SourceConfigStore store)
    {
        _runner          = runner;
        _store           = store;
        AvailableSources = SourceCatalog.All;

        // Keep the dropdown in sync when the runner is driven from the
        // Sources tab (or anywhere else). Suppress the resulting set so
        // we don't re-trigger a switch in a loop. Also re-bind transport
        // capability against the newly-active source.
        _runner.ActiveSourceChanged += factory =>
            Dispatcher.UIThread.Post(() =>
            {
                _suppressSwitch = true;
                SelectedFactory = factory;
                _suppressSwitch = false;
                RebindTransport();
            });
    }

    private void RebindTransport()
    {
        if (_transport != null) _transport.PausedChanged -= OnPausedChanged;
        _transport = _runner?.ActiveSource as ITransportControls;
        HasTransport     = _transport != null;
        CanPause         = _transport?.CanPause        ?? false;
        CanSkipNext      = _transport?.CanSkipNext     ?? false;
        CanSkipPrevious  = _transport?.CanSkipPrevious ?? false;
        IsPaused         = _transport?.IsPaused        ?? false;
        if (_transport != null) _transport.PausedChanged += OnPausedChanged;
    }

    private void OnPausedChanged(bool paused) =>
        Dispatcher.UIThread.Post(() => IsPaused = paused);

    [RelayCommand]
    private async Task TogglePauseAsync()
    {
        if (_transport == null) return;
        try { await _transport.TogglePauseAsync(); }
        catch (Exception ex) { Debug.WriteLine($"[hzn-now-vm] pause: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (_transport == null) return;
        try { await _transport.NextAsync(); }
        catch (Exception ex) { Debug.WriteLine($"[hzn-now-vm] next: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task PreviousAsync()
    {
        if (_transport == null) return;
        try { await _transport.PreviousAsync(); }
        catch (Exception ex) { Debug.WriteLine($"[hzn-now-vm] prev: {ex.Message}"); }
    }

    /// <summary>Designer ctor — no runner, dropdown is inert.</summary>
    public NowPlayingViewModel()
    {
        AvailableSources = SourceCatalog.All;
    }

    partial void OnSelectedFactoryChanged(IAudioSourceFactory? value)
    {
        if (_suppressSwitch || _runner == null || _store == null || value == null) return;

        // Fire-and-forget: setter must stay synchronous, and the runner
        // handles its own serialization. Errors are captured into
        // SwitchStatus so the UI can show them.
        _ = SwitchAsync(value);
    }

    private async Task SwitchAsync(IAudioSourceFactory factory)
    {
        if (_runner == null || _store == null) return;
        SwitchStatus = $"Starting {factory.DisplayName}...";
        try
        {
            var values = _store.Load(factory.Id, factory.Schema);
            await _runner.StartAsync(factory, values);
            SwitchStatus = null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[hzn-now-vm] switch failed: {ex.Message}");
            SwitchStatus = $"{factory.DisplayName}: {ex.Message}";
        }
    }

    /// <summary>
    /// Apply a new Track from an IPC event. Stays on the UI thread —
    /// caller dispatches via Avalonia's Dispatcher.UIThread.
    /// </summary>
    public void Apply(Track track)
    {
        Title          = string.IsNullOrWhiteSpace(track.Title)  ? "Unknown track"  : track.Title;
        Artist         = string.IsNullOrWhiteSpace(track.Artist) ? "Unknown artist" : track.Artist;
        Album          = track.Album;
        SourceId       = track.SourceId;
        SourceDisplay  = track.SourceDisplay;
        AlbumArt       = DecodeArt(track.AlbumArt);
    }

    public void SetConnectionState(bool connected)
    {
        IsConnected = connected;
        if (!connected)
        {
            // Reset to placeholder so stale data doesn't linger after FH6 closes.
            Title         = "Nothing playing";
            Artist        = "Launch Forza Horizon 6 with the mod installed";
            Album         = null;
            SourceDisplay = "—";
            SourceId      = "";
            AlbumArt      = null;
        }
    }

    private static Bitmap? DecodeArt(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            using var ms = new System.IO.MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch
        {
            // Album art payload may be malformed if the ID3 frame had
            // a non-image MIME type or a partial download. Better a
            // blank tile than a crashed UI.
            return null;
        }
    }
}
