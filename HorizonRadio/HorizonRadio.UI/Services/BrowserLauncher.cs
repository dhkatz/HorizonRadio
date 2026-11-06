using System;
using System.Diagnostics;

namespace HorizonRadio.UI.Services;

/// <summary>
/// One place to hand a URL (or folder/file path) to the OS default handler. Centralizes the
/// shell-execute launch so the platform behavior lives in a single spot rather than being
/// re-implemented at each call site (About links, the history "report" draft, …).
/// </summary>
public static class BrowserLauncher
{
    /// <summary>Open <paramref name="url"/> with the OS default handler. Returns false if the URL
    /// was empty or the launch failed (best-effort; never throws).</summary>
    public static bool Open(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[hzn-browser] open failed: {ex.Message}");
            return false;
        }
    }
}
