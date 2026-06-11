using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Diagnostics;
using HorizonRadio.UI.Tools;

namespace HorizonRadio.UI.ViewModels;

public sealed class ToolsViewModel : ViewModelBase
{
    public ObservableCollection<ToolItemViewModel> Items { get; } = new();

    public ToolsViewModel(ToolRegistry registry, IEnumerable<IToolInstaller> installers)
    {
        foreach (var installer in installers)
            Items.Add(new ToolItemViewModel(installer, registry));
    }

    public ToolsViewModel() : this(new ToolRegistry(), new IToolInstaller[]
    {
        new YtDlpInstaller(),
        new FfmpegInstaller(),
        new LibrespotInstaller(),
    })
    { }
}

public sealed partial class ToolItemViewModel : ViewModelBase, IDisposable
{
    private readonly IToolInstaller _installer;
    private readonly ToolRegistry _registry;

    private CancellationTokenSource? _cts;

    public string Kind => _installer.Kind;
    public string DisplayName => _installer.DisplayName;
    public string Description => _installer.Description;

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
        IsInstalled ? (VersionText is null ? "Installed" : $"Installed (v{VersionText})")
                    : "Not installed";

    public string StatusBrush => IsInstalled ? "#22c55e" : "#6b7280"; // green / grey

    public string InstallButtonLabel =>
        IsWorking ? "Installing…" :
        IsInstalled ? "Reinstall" :
                         "Install";

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
