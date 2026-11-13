using System.IO;
using System.Linq;
using System.Net.Http;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Tools;

namespace HorizonRadio.Core.Tools;

/// <summary>
/// Installing, updating and removing the tools the plugins bring (the Tools page, the CLI): each
/// installs where the catalog says (<see cref="ToolCatalog.TargetFor"/>), the registry is rescanned
/// after, and the Console (tagged with the tool's id) says how it went. Also checks whether each
/// installed tool is the build this app expects (<see cref="ToolFreshnessChecker"/>).
/// </summary>
public sealed class ToolInstalls(ToolRegistry registry, IPluginLog log)
{
    /// <summary>Every tool's installer, in display order.</summary>
    public IReadOnlyList<IToolInstaller> Installers { get; } = [.. registry.Catalog.Plugins.Select(p => p.Installer)];

    /// <summary>Install (or update) <paramref name="kind"/>, reporting each step to
    /// <paramref name="progress"/> and, when its status changes, to the Console.</summary>
    /// <exception cref="InvalidOperationException">No plugin brings that tool.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; the old install, if any, is kept.</exception>
    public async Task InstallAsync(string kind, IProgress<ToolInstallProgress>? progress = null, CancellationToken ct = default)
    {
        var installer = Find(kind);
        var logged = new LoggedProgress(kind, log, progress);

        log.Write(kind, "install: starting…");
        try
        {
            await installer.InstallAsync(registry.Catalog.TargetFor(kind), logged, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            log.Write(kind, "install: cancelled");
            throw;
        }
        catch (Exception ex)
        {
            log.Trace("tools", $"install {kind}: {ex}");
            log.Write(kind, $"install failed: {ex.Message}");
            throw;
        }
        registry.Rescan();
        log.Write(kind, "install: done");
    }

    /// <summary>Remove <paramref name="kind"/>'s install. False when it wasn't installed here.</summary>
    /// <exception cref="IOException">It couldn't be removed (a file in use).</exception>
    public bool Uninstall(string kind)
    {
        var dir = registry.Catalog.DirectoryFor(kind);
        if (!Directory.Exists(dir)) return false;
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write(kind, $"uninstall failed: {ex.Message}");
            throw;
        }
        finally
        {
            registry.Rescan();
        }
        log.Write(kind, "uninstalled");
        return true;
    }

    /// <summary>Whether each tool is installed and the build this app expects, keyed by its id.
    /// Tools are checked at once (some ask their download site); a check that can't tell (offline)
    /// says <see cref="ToolFreshness.Unknown"/>, never "update available".</summary>
    public async Task<IReadOnlyDictionary<string, ToolFreshness>> CheckAsync(CancellationToken ct = default)
    {
        using var http = ToolInstallerBase.CreateHttpClient(TimeSpan.FromSeconds(30));
        var results = await Task.WhenAll(Installers.Select(async installer =>
            (installer.Kind, Freshness: await ToolFreshnessChecker
                .CheckAsync(installer, registry.PrimaryFor(installer.Kind), http, ct).ConfigureAwait(false))))
            .ConfigureAwait(false);
        var updates = results.Where(r => r.Freshness == ToolFreshness.UpdateAvailable).Select(r => r.Kind).ToList();
        if (updates.Count > 0) log.Write("tools", $"updates available: {string.Join(", ", updates)}");
        return results.ToDictionary(r => r.Kind, r => r.Freshness);
    }

    // Logs each new status as it's reported (in order, unlike Progress<T>, which posts), and passes
    // every report on to the caller's progress.
    private sealed class LoggedProgress(string kind, IPluginLog log, IProgress<ToolInstallProgress>? inner)
        : IProgress<ToolInstallProgress>
    {
        private readonly Lock _lock = new();
        private string _lastStatus = "";

        public void Report(ToolInstallProgress value)
        {
            inner?.Report(value);
            lock (_lock)
            {
                // Percentage ticks repeat the same status text.
                if (value.Status == _lastStatus) return;
                _lastStatus = value.Status;
            }
            log.Write(kind, $"install: {value.Status}");
        }
    }

    private IToolInstaller Find(string kind) =>
        Installers.FirstOrDefault(i => i.Kind == kind)
            ?? throw new InvalidOperationException($"No plugin brings a tool called {kind}.");
}
