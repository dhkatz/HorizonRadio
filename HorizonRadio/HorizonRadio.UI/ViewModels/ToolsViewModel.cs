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
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Tools;
using HorizonRadio.Sdk.UI.Theme;

namespace HorizonRadio.UI.ViewModels;

public sealed partial class ToolsViewModel : ViewModelBase
{
    private readonly ToolInstalls _installs;

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

    public string CheckLabel => IsChecking ? "Checking…" : "Check for Updates";

    public ToolsViewModel(ToolRegistry registry, ToolInstalls installs, IConfirmations? confirmations = null)
    {
        _installs = installs;
        foreach (var installer in installs.Installers)
            Items.Add(new ToolItemViewModel(installer, registry, installs, confirmations));
    }

    /// <summary>Designer ctor: no tools (the app's come from the plugins it loads).</summary>
    public ToolsViewModel() : this(DesignerRegistry, new ToolInstalls(DesignerRegistry, IPluginLog.None)) { }

    private static readonly ToolRegistry DesignerRegistry = new();

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
            var freshness = await _installs.CheckAsync(ct).ConfigureAwait(true);
            foreach (var item in Items)
                item.Freshness = freshness.GetValueOrDefault(item.Kind, ToolFreshness.Unknown);
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
    private readonly ToolInstalls _installs;
    private readonly IConfirmations _confirm;

    private CancellationTokenSource? _cts;

    public string Kind => _installer.Kind;
    public string DisplayName => _installer.DisplayName;
    public string Description => _installer.Description;

    /// <summary>The installer backing this card — the freshness checker
    /// reads its expected-hash baseline.</summary>
    public IToolInstaller Installer => _installer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(StatusTone))]
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
    [NotifyPropertyChangedFor(nameof(StatusTone))]
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

    // Neutral = not installed, warning = update available, success = up to date.
    public Tone StatusTone =>
        !IsInstalled ? Tone.Neutral
        : Freshness == ToolFreshness.UpdateAvailable ? Tone.Warning
        : Tone.Success;

    public string InstallButtonLabel =>
        IsWorking ? "Installing…"
        : !IsInstalled ? "Install"
        : Freshness == ToolFreshness.UpdateAvailable ? "Update"
        : "Reinstall";

    public bool CanInstall => !IsWorking;
    public bool CanUninstall => IsInstalled && !IsWorking;

    public ToolItemViewModel(IToolInstaller installer, ToolRegistry registry, ToolInstalls installs,
        IConfirmations? confirmations = null)
    {
        _installer = installer;
        _registry = registry;
        _installs = installs;
        _confirm = confirmations ?? ProceedWithoutAsking.Instance;

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

        var progress = new Progress<ToolInstallProgress>(p =>
        {
            ProgressText = p.Status;
            ProgressIsIndeterminate = p.Fraction is null;
            ProgressFraction = p.Fraction ?? 0;
        });

        // ToolInstalls tells the Console how it goes; this card shows it.
        try
        {
            await _installs.InstallAsync(_installer.Kind, progress, _cts.Token).ConfigureAwait(true);
            // We just installed exactly what the app expects (latest for
            // latest-policy tools, the manifest pin for librespot), so the
            // sidecar now matches the baseline — mark fresh without a
            // round-trip.
            Freshness = ToolFreshness.UpToDate;
            ProgressText = "Done.";
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Cancelled.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            ProgressText = "Failed.";
        }
        finally
        {
            IsWorking = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private async Task UninstallAsync()
    {
        if (IsWorking || !IsInstalled) return;
        if (!await _confirm.ConfirmAsync(new Confirmation(
                $"Uninstall {DisplayName}?",
                "Sources that use it stop working until you install it again.",
                "Uninstall")))
            return;
        try
        {
            _installs.Uninstall(_installer.Kind);
            ProgressText = "Uninstalled.";
            ErrorMessage = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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
