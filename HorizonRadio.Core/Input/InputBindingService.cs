using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Events;

namespace HorizonRadio.Core.Input;

/// <summary>
/// Listens to every <see cref="IInputBackend"/>, maps each input to its bound
/// <see cref="EventAction"/> via the <see cref="InputBindingStore"/>, and runs
/// it through the shared <see cref="IActionDispatcher"/> — the same dispatch
/// path the Events tab uses. Also drives the Controls tab's "press a key to
/// bind" capture flow: while a capture is in flight the next input is handed
/// to the waiter and consumed (no action runs).
/// </summary>
public sealed class InputBindingService : IDisposable
{
    private readonly IReadOnlyList<IInputBackend> _backends;
    private readonly InputBindingStore _store;
    private readonly IActionDispatcher _dispatcher;
    private readonly IControllerDeviceSource? _deviceSource;

    /// <summary>Connected controller/wheel/joystick device names, for the UI's
    /// device picker. Empty if no enumerable backend is present.</summary>
    public IReadOnlyList<string> ControllerDevices => _deviceSource?.Devices ?? Array.Empty<string>();

    /// <summary>Raised (on a backend thread) when controllers connect/disconnect.</summary>
    public event Action? ControllerDevicesChanged;

    // Per-input debounce: collapse OS key auto-repeat and axis chatter, while
    // still letting deliberate repeated presses (e.g. mashing Next) through.
    private readonly Debouncer _debounce = new(400);

    private sealed class Capture
    {
        public required TaskCompletionSource<InputBinding> Tcs { get; init; }
        public required Func<InputBinding, bool> Accept { get; init; }
    }

    private Capture? _capture;

    /// <summary>Raised after a bound input runs its action, for the Controls
    /// tab's recent-activity list. Fires on a backend thread.</summary>
    public event Action<InputBinding, EventAction>? Triggered;

    public InputBindingService(
        IEnumerable<IInputBackend> backends,
        InputBindingStore store,
        IActionDispatcher dispatcher)
    {
        _store = store;
        _dispatcher = dispatcher;

        var list = new List<IInputBackend>();
        foreach (var b in backends)
        {
            b.InputReceived += OnInput;
            list.Add(b);
        }
        _backends = list;

        _deviceSource = list.OfType<IControllerDeviceSource>().FirstOrDefault();
        if (_deviceSource != null)
            _deviceSource.DevicesChanged += () => ControllerDevicesChanged?.Invoke();
    }

    /// <summary>The backends, so the UI can show which are available.</summary>
    public IReadOnlyList<IInputBackend> Backends => _backends;

    public void Start()
    {
        foreach (var b in _backends) b.Start();
    }

    private void OnInput(InputBinding binding)
    {
        // A capture in flight consumes inputs it accepts (the one being bound),
        // completing the capture without running an action. Inputs it does NOT
        // accept (a different device/category) fall through to normal dispatch,
        // so unrelated bound hotkeys keep working while the user binds something.
        var cap = _capture;
        if (cap != null && cap.Accept(binding))
        {
            Interlocked.CompareExchange(ref _capture, null, cap);
            cap.Tcs.TrySetResult(binding);
            return;
        }

        var action = _store.Match(binding);
        if (action.Type == EventActionType.None) return;

        if (!_debounce.ShouldFire(binding.Key)) return;

        Triggered?.Invoke(binding, action);
        _ = Task.Run(() => _dispatcher.RunAsync(action));
    }

    /// <summary>Resolve the next input the user produces that satisfies
    /// <paramref name="accept"/> (e.g. matches a device category), for binding
    /// capture. Non-matching inputs are swallowed and ignored. Cancels any
    /// capture already in flight. The returned binding is not acted upon.</summary>
    public Task<InputBinding> CaptureNextAsync(CancellationToken ct, Func<InputBinding, bool>? accept = null)
    {
        var cap = new Capture
        {
            Tcs = new TaskCompletionSource<InputBinding>(TaskCreationOptions.RunContinuationsAsynchronously),
            Accept = accept ?? (_ => true),
        };
        var prev = Interlocked.Exchange(ref _capture, cap);
        prev?.Tcs.TrySetCanceled(CancellationToken.None); // superseded by a newer capture
        ct.Register(() =>
        {
            if (Interlocked.CompareExchange(ref _capture, null, cap) == cap)
                cap.Tcs.TrySetCanceled(ct);
        });
        return cap.Tcs.Task;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _capture, null)?.Tcs.TrySetCanceled(CancellationToken.None);
        foreach (var b in _backends)
        {
            b.InputReceived -= OnInput;
            b.Dispose();
        }
    }
}
