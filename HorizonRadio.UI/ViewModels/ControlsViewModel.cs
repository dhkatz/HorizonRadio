using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Events;
using HorizonRadio.Core.Input;
using HorizonRadio.Core.Sources.Profiles;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// Controls tab: a control-mapping table. Each playback action has one binding
/// slot per device — a shared Keyboard/Mouse slot and a per-device Controller
/// slot (the device chosen via the picker). Click a slot to capture the next
/// matching input; Esc cancels; ✕ clears. Uses the same <see cref="EventAction"/>
/// vocabulary as the Events tab.
/// </summary>
public sealed partial class ControlsViewModel : ViewModelBase, IDisposable
{
    private readonly InputBindingStore _store;
    private readonly InputBindingService? _service;
    private readonly SourceProfileStore? _profiles;

    /// <summary>Playback action rows (Play/Pause, Next/Previous/Restart Track).</summary>
    public ObservableCollection<ControlBindingRow> Rows { get; } = new();
    /// <summary>Profile-switch rows: Next/Previous Profile + one per saved profile.</summary>
    public ObservableCollection<ControlBindingRow> ProfileRows { get; } = new();
    public ObservableCollection<string> Activity { get; } = new();

    /// <summary>Connected controllers to choose between (gamepad, wheel, …).</summary>
    public ObservableCollection<string> Controllers { get; } = new();

    /// <summary>The controller whose bindings the Controller column shows/edits.
    /// Bindings for other connected controllers stay active — this only scopes
    /// what you're viewing and capturing.</summary>
    [ObservableProperty] private string? selectedController;

    public bool HasControllers => Controllers.Count > 0;

    // The playback actions a user can bind, in display order.
    private static readonly IReadOnlyList<(string Name, string Description, EventAction Action)> Bindable = new[]
    {
        ("Play / Pause", "Toggle playback on or off.", new EventAction(EventActionType.TogglePause)),
        ("Next Track", "Skip to the next track.", new EventAction(EventActionType.NextTrack)),
        ("Previous Track", "Go back to the previous track.", new EventAction(EventActionType.PreviousTrack)),
        ("Restart Track", "Restart the current track from the beginning.", new EventAction(EventActionType.RestartTrack)),
    };

    // Profile-switch actions that don't depend on a specific profile.
    private static readonly IReadOnlyList<(string Name, string Description, EventAction Action)> FixedProfileActions = new[]
    {
        ("Next Profile", "Cycle to the next saved profile.", new EventAction(EventActionType.NextProfile)),
        ("Previous Profile", "Cycle to the previous saved profile.", new EventAction(EventActionType.PreviousProfile)),
    };

    // Design-time / fallback ctor.
    public ControlsViewModel() : this(new InputBindingStore(), null) { }

    public ControlsViewModel(InputBindingStore store, InputBindingService? service, SourceProfileStore? profiles = null)
    {
        _store = store;
        _service = service;
        _profiles = profiles;

        foreach (var (name, description, action) in Bindable)
            Rows.Add(new ControlBindingRow(name, description, action, store, service));

        // Fixed profile-switch rows live for the VM's lifetime; the per-profile
        // rows below them are synced in place as profiles change.
        foreach (var (name, description, action) in FixedProfileActions)
            ProfileRows.Add(new ControlBindingRow(name, description, action, store, service));

        if (service != null)
        {
            service.Triggered += OnTriggered;
            service.ControllerDevicesChanged += OnDevicesChanged;
        }
        if (_profiles != null) _profiles.Changed += OnProfilesChanged;
        RefreshControllers();
        RefreshProfileRows();
    }

    private void OnProfilesChanged() => Dispatcher.UIThread.Post(RefreshProfileRows);

    // In-place sync of the per-profile rows: only add new profiles, drop deleted
    // ones, and replace renamed ones. Untouched rows stay — preserving any
    // in-flight binding capture on an unrelated row (a full rebuild would cancel
    // it). The fixed Next/Previous rows are never touched here.
    private void RefreshProfileRows()
    {
        if (_profiles == null) return;
        var profiles = _profiles.All;
        var reaped = false;

        for (var i = ProfileRows.Count - 1; i >= 0; i--)
        {
            var row = ProfileRows[i];
            if (row.Action.Type != EventActionType.SwitchProfile) continue;

            var p = profiles.FirstOrDefault(x => x.Id == row.Action.Param);
            if (p == null)
            {
                // Profile deleted: drop its row and reap any orphaned bindings to it.
                reaped |= _store.ClearBindingsForAction(row.Action);
                row.Dispose();
                ProfileRows.RemoveAt(i);
            }
            else if (p.Name != row.DisplayName)
            {
                // Renamed: drop and re-add below (binding keys on id, so it survives).
                row.Dispose();
                ProfileRows.RemoveAt(i);
            }
        }

        foreach (var p in profiles)
        {
            if (ProfileRows.Any(r => r.Action.Type == EventActionType.SwitchProfile && r.Action.Param == p.Id))
                continue;
            var row = new ControlBindingRow(p.Name, "Switch to this profile.",
                new EventAction(EventActionType.SwitchProfile, p.Id), _store, _service);
            row.Controller.SetDevice(SelectedController);
            ProfileRows.Add(row);
        }

        if (reaped) _store.SaveToDisk();
    }

    private void OnDevicesChanged() => Dispatcher.UIThread.Post(RefreshControllers);

    private void RefreshControllers()
    {
        var devices = _service?.ControllerDevices ?? Array.Empty<string>();
        var previous = SelectedController;

        // Sync the collection in place rather than Clear()+Add: clearing a
        // collection that's two-way bound to the ComboBox nulls SelectedController
        // through the binding, which would lose the user's selection (and blank
        // every slot) on any unrelated hot-plug. Add/remove preserves it.
        for (int i = Controllers.Count - 1; i >= 0; i--)
            if (!devices.Contains(Controllers[i])) Controllers.RemoveAt(i);
        foreach (var d in devices)
            if (!Controllers.Contains(d)) Controllers.Add(d);
        OnPropertyChanged(nameof(HasControllers));

        // Keep the prior selection if it's still connected; otherwise pick the first.
        var desired = previous != null && Controllers.Contains(previous)
            ? previous
            : Controllers.FirstOrDefault();
        if (!Equals(SelectedController, desired)) SelectedController = desired;
    }

    partial void OnSelectedControllerChanged(string? value)
    {
        foreach (var row in Rows) row.Controller.SetDevice(value);
        foreach (var row in ProfileRows) row.Controller.SetDevice(value);
    }

    private void OnTriggered(InputBinding binding, EventAction action)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:HH:mm:ss}  {binding.Label} → {Describe(action)}");
        Dispatcher.UIThread.Post(() =>
        {
            Activity.Insert(0, line);
            while (Activity.Count > 50) Activity.RemoveAt(Activity.Count - 1);
        });
    }

    private string Describe(EventAction a) => a.Type switch
    {
        EventActionType.TogglePause => "Play / Pause",
        EventActionType.NextTrack => "Next Track",
        EventActionType.PreviousTrack => "Previous Track",
        EventActionType.RestartTrack => "Restart Track",
        EventActionType.NextProfile => "Next Profile",
        EventActionType.PreviousProfile => "Previous Profile",
        EventActionType.SwitchProfile => _profiles?.Get(a.Param ?? "") is { } p ? $"Profile: {p.Name}" : "Switch Profile",
        _ => a.Type.ToString(),
    };

    public void Dispose()
    {
        if (_service != null)
        {
            _service.Triggered -= OnTriggered;
            _service.ControllerDevicesChanged -= OnDevicesChanged;
        }
        if (_profiles != null) _profiles.Changed -= OnProfilesChanged;
        foreach (var row in Rows) row.Dispose();
        foreach (var row in ProfileRows) row.Dispose();
    }
}

/// <summary>One action row in the mapping table: a name and one binding slot per
/// device column (keyboard/mouse + the selected controller).</summary>
public sealed class ControlBindingRow : IDisposable
{
    public string DisplayName { get; }
    public string Description { get; }
    public EventAction Action { get; }
    public BindingSlot Keyboard { get; }
    public BindingSlot Controller { get; }

    public ControlBindingRow(string name, string description, EventAction action,
        InputBindingStore store, InputBindingService? service)
    {
        DisplayName = name;
        Description = description;
        Action = action;
        Keyboard = new BindingSlot(InputCategory.KeyboardMouse, action, store, service);
        Controller = new BindingSlot(InputCategory.Controller, action, store, service);
    }

    public void Dispose()
    {
        Keyboard.Dispose();
        Controller.Dispose();
    }
}

/// <summary>One cell in the mapping table: the binding for a single action within
/// a single slot (keyboard/mouse, or a specific controller device).</summary>
public sealed partial class BindingSlot : ViewModelBase, IDisposable
{
    private readonly InputCategory _category;
    private readonly EventAction _action;
    private readonly InputBindingStore _store;
    private readonly InputBindingService? _service;
    private CancellationTokenSource? _cts;
    private InputBinding? _binding;
    private string? _device; // null for keyboard/mouse; device name for controller

    /// <summary>Whether this slot can capture right now — needs a backend, and for
    /// the controller column, a selected device.</summary>
    public bool CanCapture => _service != null
        && (_category == InputCategory.KeyboardMouse || _device != null);

    [ObservableProperty] private string label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGlyph), nameof(ShowGlyph), nameof(ShowText))]
    private Bitmap? glyph;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBound), nameof(ShowEmpty), nameof(ShowGlyph), nameof(ShowText))]
    private bool isBound;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBound), nameof(ShowEmpty), nameof(ShowGlyph), nameof(ShowText))]
    private bool isListening;

    public bool HasGlyph => Glyph != null;
    public bool ShowBound => IsBound && !IsListening;
    public bool ShowEmpty => !IsBound && !IsListening;
    // With a glyph we show only the glyph (the label is redundant); without one
    // (e.g. a raw wheel button) we fall back to the text label.
    public bool ShowGlyph => ShowBound && HasGlyph;
    public bool ShowText => ShowBound && !HasGlyph;

    public BindingSlot(InputCategory category, EventAction action,
        InputBindingStore store, InputBindingService? service)
    {
        _category = category;
        _action = action;
        _store = store;
        _service = service;
        label = "";
        Reload();
    }

    /// <summary>Point a controller slot at a specific device (from the picker).</summary>
    public void SetDevice(string? device)
    {
        _device = device;
        OnPropertyChanged(nameof(CanCapture));
        // A capture in flight was targeting the old device — abandon it so the
        // slot doesn't get stuck "listening" (or bind against the wrong device)
        // when the selected controller changes or is unplugged mid-capture.
        if (IsListening) _cts?.Cancel();
        Reload();
    }

    private string Slot => InputBindingStore.SlotOf(_category, _device);

    private void Reload()
    {
        _binding = _store.GetBindingForSlot(_action, Slot);
        Label = _binding?.Label ?? "";
        Glyph = InputGlyphProvider.Resolve(_binding);
        IsBound = _binding != null;
    }

    [RelayCommand]
    private async Task Listen()
    {
        if (!CanCapture) return;
        if (IsListening) { _cts?.Cancel(); return; }

        IsListening = true;
        _cts = new CancellationTokenSource();
        try
        {
            var binding = await _service!.CaptureNextAsync(_cts.Token, Accept);
            // Esc cancels without binding.
            if (binding.Kind == InputKind.Keyboard && binding.Code == KeyboardKeys.Escape)
                return;
            _store.Bind(binding, _action);
            _store.SaveToDisk();
            _binding = binding;
            Label = binding.Label;
            Glyph = InputGlyphProvider.Resolve(binding);
            IsBound = true;
        }
        catch (OperationCanceledException) { /* cancelled */ }
        finally
        {
            IsListening = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // Accept only inputs belonging to this slot — the keyboard/mouse family, or
    // the specific controller device — plus Escape (to cancel from the keyboard).
    private bool Accept(InputBinding b)
    {
        if (b.Kind == InputKind.Keyboard && b.Code == KeyboardKeys.Escape) return true;
        return _category == InputCategory.KeyboardMouse
            ? InputCategories.Of(b.Kind) == InputCategory.KeyboardMouse
            : InputCategories.Of(b.Kind) == InputCategory.Controller && b.Device == _device;
    }

    [RelayCommand]
    private void Clear()
    {
        _store.ClearSlot(_action, Slot);
        _store.SaveToDisk();
        _binding = null;
        Label = "";
        Glyph = null;
        IsBound = false;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
