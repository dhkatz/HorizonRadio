using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Diagnostics;
using HorizonRadio.Core.Tools;
using HorizonRadio.UI.Tools;

namespace HorizonRadio.UI.ViewModels;

public sealed partial class ToolsViewModel : ViewModelBase
{
    private readonly ToolRegistry _registry;

    public ObservableCollection<ToolItemViewModel> Items { get; } = new();

    /// <summary>Count of installed tools with a newer build available —
    /// drives the sidebar badge and the one-time launch toast.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdates))]
    private int updatesAvailable;

    public bool HasUpdates => UpdatesAvailable > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckLabel))]
    private bool isChecking;

    public string CheckLabel => IsChecking ? "Checking…" : "Check for updates";

    public ToolsViewModel(ToolRegistry registry, IEnumerable<IToolInstaller> installers)
    {
        _registry = registry;
        foreach (var installer in installers)
            Items.Add(new ToolItemViewModel(installer, registry));
    }

    public ToolsViewModel() : this(new ToolRegistry(), ToolInstallers.CreateAll()) { }

    /// <summary>
    /// Run the provisioning-freshness check across every tool and update
    /// each card's <see cref="ToolItemViewModel.Freshness"/>. Safe to call
    /// from the UI thread; latest-policy tools hit the network (one shared
    /// HttpClient), librespot resolves offline against the manifest.
    /// Returns the number of tools with an update available. Idempotent —
    /// re-entrant calls while a check is running are ignored.
    /// </summary>
    public async Task<int> CheckFreshnessAsync(CancellationToken ct = default)
    {
        if (IsChecking) return UpdatesAvailable;
        IsChecking = true;
        try
        {
            using var http = ToolInstallerBase.CreateHttpClient(TimeSpan.FromSeconds(30));
            // Check tools concurrently — yt-dlp/ffmpeg each hit the network,
            // so serial checks would stack their latency onto every launch.
            // Each task's ConfigureAwait(true) marshals its Freshness write
            // back to the UI thread; the shared HttpClient is thread-safe.
            await Task.WhenAll(Items.Select(async item =>
            {
                var installed = _registry.PrimaryFor(item.Kind);
                item.Freshness = await ToolFreshnessChecker
                    .CheckAsync(item.Installer, installed, http, ct)
                    .ConfigureAwait(true);
            })).ConfigureAwait(true);
            UpdatesAvailable = Items.Count(i => i.UpdateAvailable);
            return UpdatesAvailable;
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private async Task CheckAsync() => await CheckFreshnessAsync().ConfigureAwait(true);
}

public sealed partial class ToolItemViewModel : ViewModelBase, IDisposable
{
    private readonly IToolInstaller _installer;
    private readonly ToolRegistry _registry;

    private CancellationTokenSource? _cts;

    public string Kind => _installer.Kind;
    public string DisplayName => _installer.DisplayName;
    public string Description => _installer.Description;

    /// <summary>The installer backing this card — the freshness checker
    /// reads its expected-hash baseline.</summary>
    public IToolInstaller Installer => _installer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(InstallButtonLabel))]
    [NotifyPropertyChangedFor(nameof(CanUninstall))]
    private bool isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private string? versionText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShaShort))]
    [NotifyPropertyChangedFor(nameof(HasSha))]
    private string? shaFull;

    /// <summary>Provisioning-freshness of this tool. Drives the status
    /// pill colour/text and whether the action button reads "Update".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(InstallButtonLabel))]
    [NotifyPropertyChangedFor(nameof(UpdateAvailable))]
    private ToolFreshness freshness = ToolFreshness.Unknown;

    public bool UpdateAvailable => Freshness == ToolFreshness.UpdateAvailable;

    public string? ShaShort => ShaFull is { Length: >= 12 } s
        ? $"{s[..8]}…{s[^4..]}"
        : ShaFull;
    public bool HasSha => !string.IsNullOrEmpty(ShaFull);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallButtonLabel))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(CanUninstall))]
    private bool isWorking;

    [ObservableProperty] private string? progressText;
    [ObservableProperty] private double progressFraction;
    [ObservableProperty] private bool progressIsIndeterminate;
    [ObservableProperty] private string? errorMessage;

    public string StatusLabel =>
        !IsInstalled ? "Not installed"
        : VersionText is null ? "Installed" : $"Installed (v{VersionText})";

    // grey = not installed, amber = update available, green = up to date.
    public string StatusBrush =>
        !IsInstalled ? "#6b7280"
        : Freshness == ToolFreshness.UpdateAvailable ? "#f59e0b"
        : "#22c55e";

    public string InstallButtonLabel =>
        IsWorking ? "Installing…"
        : !IsInstalled ? "Install"
        : Freshness == ToolFreshness.UpdateAvailable ? "Update"
        : "Reinstall";

    public bool CanInstall => !IsWorking;
    public bool CanUninstall => IsInstalled && !IsWorking;

    public ToolItemViewModel(IToolInstaller installer, ToolRegistry registry)
    {
        _installer = installer;
        _registry = registry;

        registry.Changed += OnRegistryChanged;
        RefreshFromRegistry();
    }

    private void OnRegistryChanged()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(RefreshFromRegistry);
    }

    private void RefreshFromRegistry()
    {
        var tool = _registry.PrimaryFor(_installer.Kind);
        IsInstalled = tool != null;
        VersionText = tool?.Version;
        ShaFull = tool?.Sha256;
        // Freshness is owned by the checker (and by a successful install
        // below); we only reset it to Missing when the tool disappears.
        if (tool == null)
            Freshness = ToolFreshness.Missing;
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (IsWorking) return;
        IsWorking = true;
        ErrorMessage = null;
        ProgressText = "Starting…";
        ProgressFraction = 0;
        ProgressIsIndeterminate = true;
        _cts = new CancellationTokenSource();

        var lastStatus = "";
        var progress = new Progress<ToolInstallProgress>(p =>
        {
            ProgressText = p.Status;
            ProgressIsIndeterminate = p.Fraction is null;
            ProgressFraction = p.Fraction ?? 0;
            // De-dupe: percentage ticks repeat the same status string.
            if (p.Status != lastStatus)
            {
                lastStatus = p.Status;
                ProcessConsole.Append(_installer.Kind, $"install: {p.Status}");
            }
        });

        ProcessConsole.Append(_installer.Kind, "install: starting…");
        try
        {
            await _installer.InstallAsync(progress, _cts.Token).ConfigureAwait(true);
            _registry.Rescan();
            // We just installed exactly what the app expects (latest for
            // latest-policy tools, the manifest pin for librespot), so the
            // sidecar now matches the baseline — mark fresh without a
            // round-trip.
            Freshness = ToolFreshness.UpToDate;
            ProgressText = "Done.";
            ProcessConsole.Append(_installer.Kind, "install: done");
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Cancelled.";
            ProcessConsole.Append(_installer.Kind, "install: cancelled");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[hzn-tools-vm] install {_installer.Kind}: {ex}");
            ErrorMessage = ex.Message;
            ProgressText = "Failed.";
            ProcessConsole.Append(_installer.Kind, $"install failed: {ex.Message}");
        }
        finally
        {
            IsWorking = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Uninstall()
    {
        if (IsWorking || !IsInstalled) return;
        try
        {
            var dir = ToolsPaths.DirectoryFor(_installer.Kind);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            _registry.Rescan();
            ProgressText = "Uninstalled.";
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[hzn-tools-vm] uninstall {_installer.Kind}: {ex}");
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    public void Dispose()
    {
        _registry.Changed -= OnRegistryChanged;
        _cts?.Dispose();
    }
}
