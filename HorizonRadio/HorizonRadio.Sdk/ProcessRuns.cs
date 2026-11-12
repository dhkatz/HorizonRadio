using System.Diagnostics;
using System.Text;

namespace HorizonRadio.Sdk;

/// <summary>What a process run to its end printed, and how it exited.</summary>
public sealed record ProcessResult(int ExitCode, string Output, string Errors);

/// <summary>Helpers for running a tool through <see cref="IProcesses"/>.</summary>
public static class ProcessRuns
{
    /// <summary>
    /// Runs <paramref name="info"/> to its end and returns what it printed. The command line and its
    /// stderr go to the Console tab under <paramref name="tag"/>, whether or not it succeeded.
    /// Cancelling kills the process (and its children) and throws. stdout and stderr are redirected
    /// (as UTF-8) and read at once, so a chatty tool can't stall on a full pipe; the caller decides
    /// what an exit code means.
    /// </summary>
    public static async Task<ProcessResult> RunToEndAsync(
        this IProcesses processes, ProcessStartInfo info, IPluginLog log, string tag, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(log);
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding ??= Encoding.UTF8;
        info.StandardErrorEncoding ??= Encoding.UTF8;

        log.Write(tag, $"$ {tag} {string.Join(' ', info.ArgumentList)}");
        using var process = processes.Start(info);
        using var killOnCancel = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already gone */ }
            catch (System.ComponentModel.Win32Exception) { /* exiting; can't be killed */ }
        });

        var errors = Task.Run(async () =>
        {
            try { return await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return ""; }
            catch (IOException) { return ""; }
        }, CancellationToken.None);
        var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var stderr = await errors.ConfigureAwait(false);
        log.Write(tag, stderr);
        return new ProcessResult(process.ExitCode, output, stderr);
    }
}
