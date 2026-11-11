using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Devices;
using HorizonRadio.Core.Peers;
using HorizonRadio.Core.Settings;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// The Devices page: pair this PC with your others (show a code here, type it there) and see them,
/// so their synced settings stay in step. Follows <see cref="MyDevices"/>, whose events arrive off
/// the UI thread.
/// </summary>
public sealed partial class DevicesViewModel : ViewModelBase
{
    private readonly MyDevices _devices;
    private readonly DispatcherTimer _clock;

    public ObservableCollection<DeviceRow> Devices { get; } = new();

    [ObservableProperty] private string thisDeviceName = "";
    [ObservableProperty] private string joinCode = "";
    [ObservableProperty] private string? pairingCode;
    [ObservableProperty] private string pairingExpires = "";
    [ObservableProperty] private string? status;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isPaired;

    public bool IsShowingCode => PairingCode != null;
    public bool CanJoin => !IsPaired;
    public bool HasStatus => !string.IsNullOrEmpty(Status);

    public DevicesViewModel(MyDevices devices)
    {
        _devices = devices;
        _devices.Changed += () => Dispatcher.UIThread.Post(Refresh);
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => Refresh());
        _clock.Start();
        ThisDeviceName = devices.Name;
        Refresh();
    }

    /// <summary>Designer ctor.</summary>
    public DevicesViewModel() : this(new MyDevices(SettingsStore.CreateInMemory(),
        _ => Task.FromException<IPeerNetwork>(new FileNotFoundException("designer"))))
    {
    }

    partial void OnPairingCodeChanged(string? value) => OnPropertyChanged(nameof(IsShowingCode));

    partial void OnIsPairedChanged(bool value) => OnPropertyChanged(nameof(CanJoin));

    partial void OnStatusChanged(string? value) => OnPropertyChanged(nameof(HasStatus));

    private void Refresh()
    {
        IsPaired = _devices.IsPaired;
        Status = _devices.Status;

        if (_devices.Pairing is { } pairing && pairing.Expires > DateTimeOffset.UtcNow)
        {
            PairingCode = pairing.Code.ToString();
            var minutes = Math.Max(1, (int)Math.Ceiling((pairing.Expires - DateTimeOffset.UtcNow).TotalMinutes));
            PairingExpires = $"Good for {minutes} more minute{(minutes == 1 ? "" : "s")}, and for one try.";
        }
        else
        {
            PairingCode = null;
        }

        var rows = _devices.Devices.Select(d => new DeviceRow(d, RemoveAsync)).ToList();
        if (!rows.Select(r => r.Key).SequenceEqual(Devices.Select(r => r.Key)))
        {
            Devices.Clear();
            foreach (var row in rows) Devices.Add(row);
        }
    }

    [RelayCommand]
    private async Task ShowCodeAsync()
    {
        await RunAsync(async () =>
        {
            await _devices.StartPairingAsync();
            Refresh();
        });
    }

    [RelayCommand]
    private void HideCode()
    {
        _devices.CancelPairing();
        Refresh();
    }

    [RelayCommand]
    private async Task JoinAsync()
    {
        if (!Core.Devices.PairingCode.TryParse(JoinCode, out var code))
        {
            Status = "That isn't a pairing code: it's 12 letters and numbers, like ABCD-EFGH-JKLM.";
            return;
        }
        await RunAsync(async () =>
        {
            Status = "Looking for the device showing that code…";
            await _devices.JoinAsync(code);
            JoinCode = "";
            Refresh();
        });
    }

    [RelayCommand]
    private async Task RenameAsync() => await RunAsync(async () =>
    {
        await _devices.RenameAsync(ThisDeviceName);
        ThisDeviceName = _devices.Name;
        Refresh();
    });

    [RelayCommand]
    private async Task LeaveAsync() => await RunAsync(async () =>
    {
        await _devices.LeaveAsync();
        Refresh();
    });

    private async Task RemoveAsync(DeviceRow row) => await RunAsync(async () =>
    {
        await _devices.RemoveAsync(row.PeerId);
        Refresh();
    });

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is PairingException or PeerNetworkException or FileNotFoundException or TimeoutException or IOException)
        {
            Status = ex is FileNotFoundException
                ? "The peer-to-peer helper isn't installed with this build, so devices can't connect."
                : ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>One device in the list.</summary>
public sealed partial class DeviceRow : ViewModelBase
{
    public DeviceRow(DeviceStatus device, Func<DeviceRow, Task> remove)
    {
        PeerId = device.PeerId;
        Name = device.Name;
        IsThisDevice = device.IsThisDevice;
        State = Describe(device);
        IsOnline = device.IsThisDevice || device.Path != PeerPath.None;
        RemoveCommand = new AsyncRelayCommand(() => remove(this));
    }

    public string PeerId { get; }
    public string Name { get; }
    public bool IsThisDevice { get; }
    public bool CanRemove => !IsThisDevice;
    public string State { get; }
    public bool IsOnline { get; }
    public double IconOpacity => IsOnline ? 1.0 : 0.4;
    public IAsyncRelayCommand RemoveCommand { get; }

    /// <summary>What the list compares to see whether it changed.</summary>
    public string Key => $"{PeerId}|{Name}|{State}";

    private static string Describe(DeviceStatus d)
    {
        if (d.IsThisDevice) return "This device";
        return d.Path switch
        {
            PeerPath.Direct => "Connected",
            PeerPath.Relayed => "Connected through a relay",
            _ => d.LastSeen is { } seen ? $"Offline · last seen {Ago(seen)}" : "Offline",
        };
    }

    private static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.UtcNow - when;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} h ago";
        return when.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture);
    }
}
