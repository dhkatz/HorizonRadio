namespace HorizonRadio.Sdk;

/// <summary>
/// Keeps a secret (a login, a key) so only this user on this machine can read it back: on
/// Windows, DPAPI for the current user. Each kind of secret names its purpose, so one kind's
/// protected bytes can't be read back as another's. The host's comes from its platform; on an OS
/// with nothing to protect with, <see cref="Scheme"/> is null, and a secret either isn't kept or
/// is kept as written (the caller's choice).
/// </summary>
public interface ISecretProtector
{
    /// <summary>How it protects, to keep beside what it protected ("dpapi"); null when it can't.</summary>
    string? Scheme { get; }

    /// <summary>The protected form of <paramref name="plain"/>.</summary>
    /// <param name="plain">The secret.</param>
    /// <param name="purpose">What the secret is ("HorizonRadio.Spotify.RefreshToken.v1"); the same
    /// purpose reads it back.</param>
    /// <exception cref="NotSupportedException"><see cref="Scheme"/> is null.</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">The OS couldn't protect it.</exception>
    byte[] Protect(byte[] plain, string purpose);

    /// <summary>The plain bytes, or null when they can't be read here: another user's or another
    /// machine's, protected for another purpose or in another way, or damaged.</summary>
    byte[]? Unprotect(byte[] protectedData, string purpose);

    /// <summary>Protects nothing (<see cref="Scheme"/> null): a host with no platform for it, or a test.</summary>
    static ISecretProtector None { get; } = new NoProtection();

    private sealed class NoProtection : ISecretProtector
    {
        public string? Scheme => null;

        public byte[] Protect(byte[] plain, string purpose) =>
            throw new NotSupportedException("This host has nothing to protect secrets with.");

        public byte[]? Unprotect(byte[] protectedData, string purpose) => null;
    }
}
