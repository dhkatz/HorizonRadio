using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Sources;

/// <summary>
/// Optional capability for an <see cref="IAudioSourceFactory"/> whose source needs
/// the user to connect an account before it can play (OAuth, an API key, …). The
/// Sources tab checks for this — the same way the player bar checks for
/// <see cref="ITransportControls"/>/<see cref="IPlaybackProgress"/> — and renders a
/// generic Connect / Disconnect / status panel, so authenticating sources don't get
/// hardcoded into the view. Implemented by the factory because connecting is a
/// config-time concern (it reads the form's <see cref="ConfigValues"/>, e.g. a
/// Client ID) that precedes building any source instance.
/// </summary>
public interface IAuthenticatingSource
{
    /// <summary>Whether an account is currently connected (a token is stored / live).</summary>
    bool IsConnected { get; }

    /// <summary>One-line status for the panel — "Connected", or what the user still
    /// needs to do (e.g. the redirect URI to register, or "enter your Client ID").</summary>
    string StatusText { get; }

    /// <summary>Run the interactive connect flow (e.g. PKCE browser login), using the
    /// current form values for any required config. Throws with a user-facing message
    /// on failure (missing key, denied consent, …).</summary>
    Task ConnectAsync(ConfigValues values, CancellationToken ct = default);

    /// <summary>Forget the stored account credentials.</summary>
    void Disconnect();
}
