using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.ModInstall;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// Mod Manager tab: install / uninstall the version.dll proxy mod into
/// a Forza Horizon 6 install. Auto-detects Steam installs at startup
/// (Steam libraries → steamapps/common/ForzaHorizon6); the user can
/// browse manually if detection misses or they have an Xbox/Game Pass
/// copy.
///
/// Status is hash-based via <see cref="ModInstaller.Check"/>. The card
/// re-checks on every Install / Uninstall and on path change so the
/// UI never shows stale state.
/// </summary>
public sealed partial class ModManagerViewModel : ViewModelBase
{
    private readonly ModInstaller _installer = new();

    public ObservableCollection<DetectedInstall> Detected { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGamePath))]
    private string? gamePath;

    /// <summary>Selected entry in the detected-installs dropdown. Bound
    /// to ComboBox.SelectedItem so the closed-state shows the chosen
    /// install instead of a blank pill. Pre-seeded on construction
    /// when there's a single hit; thereafter only the user updates it.</summary>
    [ObservableProperty] private DetectedInstall? selectedDetected;

    public bool HasGamePath => !string.IsNullOrEmpty(GamePath);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(StatusDetail))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(CanUninstall))]
    [NotifyPropertyChangedFor(nameof(InstallButtonLabel))]
    private InstallationStatus status = InstallationStatus.PathInvalid;

    [ObservableProperty] private string? lastActionMessage;
    [ObservableProperty] private bool lastActionFailed;

    public string StatusLabel => Status switch
    {
        InstallationStatus.InstalledMatch => "Installed",
        InstallationStatus.InstalledOther => "Different version installed",
        InstallationStatus.NotInstalled => "Not installed",
        _ => "No game folder selected",
    };

    public string StatusDetail => Status switch
    {
        InstallationStatus.InstalledMatch => "Ready. FH6 will load on next launch.",
        InstallationStatus.InstalledOther => "Another version.dll is present. Install will back it up first.",
        InstallationStatus.NotInstalled => "Click Install to drop the DLL in.",
        _ => "Pick your FH6 folder above.",
    };

    public string StatusBrush => Status switch
    {
        InstallationStatus.InstalledMatch => "#22c55e", // green
        InstallationStatus.InstalledOther => "#f59e0b", // amber
        InstallationStatus.NotInstalled => "#6b7280", // grey
        _ => "#6b7280",
    };

    public bool CanInstall => HasGamePath
                              && _installer.HasBundledDll
                              && Status != InstallationStatus.PathInvalid
                              && Status != InstallationStatus.InstalledMatch;
    public bool CanUninstall => HasGamePath && Status == InstallationStatus.InstalledMatch;

    public string InstallButtonLabel =>
        Status == InstallationStatus.InstalledOther ? "Replace" : "Install";

    public bool BundledDllMissing => !_installer.HasBundledDll;

    public ModManagerViewModel()
    {
        // Run detection once at construction. If exactly one install
        // turned up, pre-select it so the user lands on a ready-to-go
        // state. Multiple hits surface as a dropdown; zero hits fall
        // back to Browse.
        try
        {
            foreach (var d in Fh6Detection.Detect()) Detected.Add(d);
        }
        catch (Exception ex) { Debug.WriteLine($"[hzn-mod-vm] detect failed: {ex.Message}"); }

        if (Detected.Count >= 1)
        {
            // Auto-pick the first hit so the dropdown's closed state
            // isn't blank. If there are several, the user can change it.
            SelectedDetected = Detected[0];
            GamePath = Detected[0].Path;
        }

        Refresh();
    }

    partial void OnGamePathChanged(string? value) => Refresh();

    partial void OnSelectedDetectedChanged(DetectedInstall? value)
    {
        if (value != null && !string.Equals(value.Path, GamePath, StringComparison.OrdinalIgnoreCase))
            GamePath = value.Path;
    }

    /// <summary>Re-check the install status of the current GamePath.
    /// Called automatically after Install/Uninstall and on path
    /// changes; also exposed as a command so the user can poke it
    /// manually after running FH6 / dropping a DLL by hand.</summary>
    [RelayCommand]
    private void Refresh()
    {
        Status = _installer.Check(GamePath ?? "");
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanUninstall));
    }

    [RelayCommand]
    private void SelectDetected(DetectedInstall? install)
    {
        if (install != null) GamePath = install.Path;
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (string.IsNullOrEmpty(GamePath)) return;
        LastActionMessage = "Installing…";
        LastActionFailed = false;
        // Run on a background thread; copy involves disk I/O that can
        // stall on slow drives or AV scans.
        var result = await Task.Run(() => _installer.Install(GamePath!));
        LastActionMessage = result.Message;
        LastActionFailed = !result.Success;
        Refresh();
    }

    [RelayCommand]
    private async Task UninstallAsync()
    {
        if (string.IsNullOrEmpty(GamePath)) return;
        LastActionMessage = "Uninstalling…";
        LastActionFailed = false;
        var result = await Task.Run(() => _installer.Uninstall(GamePath!));
        LastActionMessage = result.Message;
        LastActionFailed = !result.Success;
        Refresh();
    }

    [RelayCommand]
    private void OpenGameFolder()
    {
        if (string.IsNullOrEmpty(GamePath) || !Directory.Exists(GamePath)) return;
        try { Process.Start(new ProcessStartInfo { FileName = GamePath, UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine($"[hzn-mod-vm] open folder: {ex.Message}"); }
    }
}
