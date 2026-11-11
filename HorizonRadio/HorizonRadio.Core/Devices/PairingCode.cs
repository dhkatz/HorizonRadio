using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace HorizonRadio.Core.Devices;

/// <summary>
/// The code one device shows and the other types to pair them: 12 characters, <c>ABCD-EFGH-JKLM</c>,
/// from an alphabet without look-alikes (no I, O, 0, 1). The first group only says where the two
/// meet (<see cref="Topic"/>: anyone may learn it); the other two are the secret each device proves
/// it knows (40 bits), through a key stretched so a guess costs a fraction of a second: someone who
/// saw a proof can't work the code out from it while it's still good, and a wrong guess ends the
/// pairing.
/// </summary>
public sealed class PairingCode
{
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int Length = 12;

    /// <summary>How long a code can be used.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private const int Iterations = 200_000;

    private readonly string _chars;
    private byte[]? _key;

    private PairingCode(string chars) => _chars = chars;

    public static PairingCode New() => new(RandomNumberGenerator.GetString(Alphabet, Length));

    /// <summary>Read a code as typed: any case, with or without dashes and spaces.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out PairingCode? code)
    {
        code = null;
        if (text == null) return false;
        var chars = new StringBuilder(Length);
        foreach (var c in text)
        {
            if (c is '-' or ' ' or '\t') continue;
            var upper = char.ToUpperInvariant(c);
            if (!Alphabet.Contains(upper, StringComparison.Ordinal)) return false;
            chars.Append(upper);
        }
        if (chars.Length != Length) return false;
        code = new PairingCode(chars.ToString());
        return true;
    }

    public override string ToString() => $"{_chars[..4]}-{_chars[4..8]}-{_chars[8..]}";

    public override bool Equals(object? obj) => obj is PairingCode other && other._chars == _chars;

    public override int GetHashCode() => _chars.GetHashCode(StringComparison.Ordinal);

    /// <summary>Where the two devices meet: the host advertises it, the joiner finds it.</summary>
    internal string Topic => TopicFor("pair");

    /// <summary>Where two peers meet for one use of a code (pairing devices, adding a friend).</summary>
    internal string TopicFor(string purpose) => purpose + "/" + _chars[..4];

    /// <summary>Proof of knowing the code, for one step of pairing between two peers.</summary>
    internal string Proof(string step, string from, string to) =>
        Convert.ToBase64String(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes($"horizon-radio/pair/{step}|{from}|{to}")));

    internal bool Verifies(string? proof, string step, string from, string to)
    {
        if (proof == null) return false;
        Span<byte> given = stackalloc byte[32];
        if (!Convert.TryFromBase64String(proof, given, out var written) || written != 32) return false;
        var expected = HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes($"horizon-radio/pair/{step}|{from}|{to}"));
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }

    private byte[] Key => _key ??= Rfc2898DeriveBytes.Pbkdf2(
        Encoding.UTF8.GetBytes(_chars[4..]),
        Encoding.UTF8.GetBytes("horizon-radio/pair/" + _chars[..4]),
        Iterations, HashAlgorithmName.SHA256, 32);
}
