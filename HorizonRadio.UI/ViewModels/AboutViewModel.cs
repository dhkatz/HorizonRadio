using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// The About tab: app identity (version/channel/commit), the per-channel
/// update check + action, and links. Home for the self-updater; alerts are
/// surfaced elsewhere (footer badge + one-time launch toast).
/// </summary>
public sealed partial class AboutViewModel : ViewModelBase
{
    private readonly string _repoUrl = "https://github.com/dhkatz/HorizonRadio";

    private readonly BuildInfo _build = BuildInfo.Current;
    private AppUpdateResult? _result;

    public string Version => _build.Version;
    public string Channel => _build.Channel.ToString().ToLowerInvariant();

    public string? CommitShort => _build.CommitSha is { Length: > 0 } sha
        ? sha[..Math.Min(7, sha.Length)]
        : null;

    /// <summary>"v0.2.0 · stable · a1b2c3d" — the one-line identity.</summary>
    public string VersionLine =>
        $"v{Version} · {Channel}" + (CommitShort is null ? "" : $" · {CommitShort}");

    /// <summary>Dev builds can't update — hide the check/update affordances.</summary>
    public bool CanCheck => !_build.IsDev;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckLabel))]
    private bool isChecking;

    public string CheckLabel => IsChecking ? "Checking…" : "Check for Updates";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateButtonVisible))]
    private bool updateAvailable;

    public bool UpdateButtonVisible => UpdateAvailable && CanCheck;

    [ObservableProperty] private string statusText = "";

    public AboutViewModel()
    {
        StatusText = _build.IsDev ? "Development build — updates disabled." : "";
    }

    /// <summary>
    /// Check GitHub for a newer build on this channel and update state.
    /// Returns true if an update is available (the caller uses this to fire
    /// the one-time launch toast). Failure-silent: offline → "couldn't
    /// check", never an error dialog.
    /// </summary>
    public async Task<bool> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        if (IsChecking || !CanCheck) return UpdateAvailable;
        IsChecking = true;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var result = await AppUpdateChecker.CheckAsync(_build, http, ct).ConfigureAwait(true);
            _result = result;
            UpdateAvailable = result.Status == UpdateStatus.UpdateAvailable;
            StatusText = result.Status switch
            {
                UpdateStatus.UpdateAvailable => $"Update available — {result.LatestVersion ?? "newer build"}.",
                UpdateStatus.UpToDate => "You're on the latest build.",
                _ => "Couldn't check for updates (offline?).",
            };
            return UpdateAvailable;
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private async Task CheckAsync() => await CheckForUpdatesAsync().ConfigureAwait(true);

    /// <summary>
    /// Apply the available update. Until the in-place swap lands this opens
    /// the release page (the same fallback the swap uses when it can't write
    /// the app dir).
    /// </summary>
    [RelayCommand]
    private void Update() => OpenUrl(_result?.ReleasePageUrl ?? $"{_repoUrl}/releases");

    [RelayCommand]
    private void OpenRepo() => OpenUrl(_repoUrl);

    [RelayCommand]
    private void OpenReleases() => OpenUrl($"{_repoUrl}/releases");

    [RelayCommand]
    private void OpenIssues() => OpenUrl($"{_repoUrl}/issues");

    [RelayCommand]
    private void OpenLicense() => OpenUrl($"{_repoUrl}/blob/main/LICENSE");

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[hzn-about] open url failed: {ex.Message}");
        }
    }
}
