using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SpotifyAPI.Web;

namespace HorizonRadio.Core.Sources.Spotify;

/// <summary>
/// The user's Spotify account connection: a PKCE OAuth login (no client secret — safe
/// to ship a default Client ID in the binary) that yields a ready user-scoped
/// <see cref="SpotifyClient"/> for playback control, queue, and search. The refresh
/// token is persisted (DPAPI-encrypted) via <see cref="SpotifyAuthStore"/>, so the
/// user logs in once and the client silently refreshes thereafter.
///
/// The browser redirect is captured by a one-shot loopback <see cref="TcpListener"/>
/// (no embedded-web-server dependency). The redirect URI must be registered on the
/// Spotify app exactly as <see cref="RedirectUri"/>.
/// </summary>
public sealed class SpotifyConnection : IAsyncDisposable
{
    // Bring-your-own Client ID only — Spotify's 2026 Development-Mode rules (≤5
    // users per Client ID, no Extended Quota for individuals) make a bundled app
    // unviable. Each user supplies their own app's Client ID; we never ship one.
    private static readonly string[] Scopes =
    [
        SpotifyAPI.Web.Scopes.UserReadPlaybackState,
        SpotifyAPI.Web.Scopes.UserModifyPlaybackState,
        SpotifyAPI.Web.Scopes.UserReadCurrentlyPlaying,
        SpotifyAPI.Web.Scopes.PlaylistReadPrivate,
        SpotifyAPI.Web.Scopes.PlaylistReadCollaborative,
        SpotifyAPI.Web.Scopes.UserLibraryRead,
    ];

    private readonly SpotifyAuthStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _redirectPort;
    private string _clientId;
    private SpotifyClient? _client;

    public SpotifyConnection(SpotifyAuthStore store, string clientId, int redirectPort = 5599)
    {
        _store = store;
        _clientId = clientId;
        _redirectPort = redirectPort;
    }

    private static void Log(string msg) => Debug.WriteLine($"[hzn-spotify-conn] {msg}");

    /// <summary>The loopback redirect URI; must be registered on the Spotify app.</summary>
    public string RedirectUri => $"http://127.0.0.1:{_redirectPort}/callback";

    /// <summary>True when a stored token or live client exists (i.e. the user has
    /// logged in at least once and hasn't disconnected).</summary>
    public bool IsConnected => _client != null || _store.Load() != null;

    /// <summary>Override the Client ID (the user's own Spotify app, for a private
    /// rate-limit bucket). Clears any cached client so the next call uses it.</summary>
    public void SetClientId(string clientId)
    {
        if (_clientId == clientId) return;
        _clientId = clientId;
        _client = null;
    }

    /// <summary>A ready user-scoped client, refreshed from the stored token, or null
    /// if not logged in / no Client ID. The <see cref="PKCEAuthenticator"/> auto-
    /// refreshes the access token and we persist rotated refresh tokens.</summary>
    public async Task<SpotifyClient?> GetClientAsync(CancellationToken ct = default)
    {
        if (_client != null) return _client;
        if (string.IsNullOrWhiteSpace(_clientId)) return null;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client != null) return _client;
            var refresh = _store.Load();
            if (string.IsNullOrEmpty(refresh)) return null;

            var token = await new OAuthClient()
                .RequestToken(new PKCETokenRefreshRequest(_clientId, refresh), ct)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(token.RefreshToken)) _store.Save(token.RefreshToken);
            _client = BuildClient(token);
            return _client;
        }
        catch (Exception ex)
        {
            Log($"refresh failed: {ex.Message}");
            return null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Interactive PKCE login: opens the browser, captures the loopback
    /// redirect, exchanges the code, and persists the refresh token.</summary>
    public async Task LoginAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_clientId))
            throw new InvalidOperationException("No Spotify Client ID configured.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (verifier, challenge) = PKCEUtil.GenerateCodes();
            var login = new LoginRequest(new Uri(RedirectUri), _clientId, LoginRequest.ResponseType.Code)
            {
                CodeChallengeMethod = "S256",
                CodeChallenge = challenge,
                Scope = Scopes,
            };

            var code = await CaptureCodeAsync(login.ToUri(), ct).ConfigureAwait(false);

            var token = await new OAuthClient()
                .RequestToken(new PKCETokenRequest(_clientId, code, new Uri(RedirectUri), verifier), ct)
                .ConfigureAwait(false);

            _store.Save(token.RefreshToken);
            _client = BuildClient(token);
        }
        finally { _gate.Release(); }
    }

    public void Logout()
    {
        _store.Clear();
        _client = null;
    }

    private SpotifyClient BuildClient(PKCETokenResponse token)
    {
        var authenticator = new PKCEAuthenticator(_clientId, token);
        // Spotify can rotate the refresh token on each refresh — persist the latest.
        authenticator.TokenRefreshed += (_, t) =>
        {
            if (!string.IsNullOrEmpty(t.RefreshToken)) _store.Save(t.RefreshToken);
        };
        var config = SpotifyClientConfig.CreateDefault().WithAuthenticator(authenticator);
        return new SpotifyClient(config);
    }

    // Capture the OAuth redirect on a one-shot loopback listener: open the auth URL
    // in the browser, accept the single callback request, pull `code` from its query.
    private async Task<string> CaptureCodeAsync(Uri authUrl, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, _redirectPort);
        listener.Start();
        try
        {
            OpenBrowser(authUrl.ToString());

            using var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            using var stream = client.GetStream();

            var buffer = new byte[8192];
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            var request = Encoding.ASCII.GetString(buffer, 0, read);

            var error = ExtractQueryParam(request, "error");
            if (error != null) throw new InvalidOperationException($"Spotify login failed: {error}");
            var code = ExtractQueryParam(request, "code")
                ?? throw new InvalidOperationException("Spotify login returned no authorization code.");

            const string body = "<html><body style=\"font-family:sans-serif;text-align:center;margin-top:3em\">" +
                                "<h2>Horizon Radio is connected to Spotify.</h2><p>You can close this tab.</p></body></html>";
            var response = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n" +
                           $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(response), ct).ConfigureAwait(false);

            return code;
        }
        finally { listener.Stop(); }
    }

    // Parse a query param from the first request line: "GET /callback?code=...&... HTTP/1.1".
    private static string? ExtractQueryParam(string request, string key)
    {
        var firstLine = request.Split('\n', 2)[0];
        var parts = firstLine.Split(' ');
        if (parts.Length < 2) return null;
        var q = parts[1].IndexOf('?');
        if (q < 0) return null;

        foreach (var pair in parts[1][(q + 1)..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && Uri.UnescapeDataString(pair[..eq]) == key)
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }

    private static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex) { Log($"open browser failed: {ex.Message}"); }
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
