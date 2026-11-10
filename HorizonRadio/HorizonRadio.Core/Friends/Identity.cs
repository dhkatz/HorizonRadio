using System.Security.Cryptography;
using System.Text;
using HorizonRadio.Core.Settings;

namespace HorizonRadio.Core.Friends;

/// <summary>Who you are to your friends.</summary>
public static class IdentitySettings
{
    /// <summary>Your identity's private key (ECDSA P-256, PKCS#8, base64). Made the first time it's
    /// needed, and handed to your other devices when they pair, so all of them are you.</summary>
    public static readonly Setting<string?> Key = new("identity.key", SettingScope.Secret, null);

    /// <summary>What your friends see you as. Null: your Windows user name.</summary>
    public static readonly Setting<string?> Name = new("identity.name", SettingScope.Synced, null);
}

/// <summary>
/// You, as your friends know you: a key pair shared by your devices. Your <see cref="UserId"/> is
/// derived from the public key, which friends keep when they add you; they then trust a device that
/// signs with the private key (<see cref="Sign"/>, <see cref="Verify"/>).
/// </summary>
public sealed class Identity
{
    private readonly byte[] _privateKey;

    private Identity(byte[] privateKey, string publicKey)
    {
        _privateKey = privateKey;
        PublicKey = publicKey;
        UserId = UserIdOf(publicKey);
    }

    /// <summary>The public key (SubjectPublicKeyInfo, base64), which friends keep.</summary>
    public string PublicKey { get; }

    /// <summary>A short, stable id for you: from the public key.</summary>
    public string UserId { get; }

    /// <summary>This device's identity, made and kept in the settings if there isn't one yet.</summary>
    public static Identity GetOrCreate(SettingsStore settings)
    {
        if (settings.Get(IdentitySettings.Key) is { Length: > 0 } stored)
        {
            try
            {
                return FromPrivateKey(stored);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                System.Diagnostics.Trace.WriteLine($"[hzn-friends] the identity key can't be read; making a new one: {ex.Message}");
            }
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = Convert.ToBase64String(key.ExportPkcs8PrivateKey());
        settings.Set(IdentitySettings.Key, privateKey);
        return FromPrivateKey(privateKey);
    }

    private static Identity FromPrivateKey(string privateKey)
    {
        var bytes = Convert.FromBase64String(privateKey);
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(bytes, out _);
        return new Identity(bytes, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>The id of whoever holds the private key to <paramref name="publicKey"/>.</summary>
    public static string UserIdOf(string publicKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(publicKey)).AsSpan(0, 16));

    public string Sign(string message)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(_privateKey, out _);
        return Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(message), HashAlgorithmName.SHA256));
    }

    /// <summary>Whether <paramref name="signature"/> is <paramref name="message"/> signed by the
    /// holder of <paramref name="publicKey"/>.</summary>
    public static bool Verify(string publicKey, string message, string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyData(Encoding.UTF8.GetBytes(message), Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }
}
