using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using HorizonRadio.Sdk;

namespace HorizonRadio.Platform.Windows;

/// <summary>Secrets protected with DPAPI for the current Windows user: unreadable to another user
/// or on another machine. The purpose is DPAPI's extra entropy.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    public string? Scheme => "dpapi";

    public byte[] Protect(byte[] plain, string purpose) =>
        ProtectedData.Protect(plain, Encoding.UTF8.GetBytes(purpose), DataProtectionScope.CurrentUser);

    public byte[]? Unprotect(byte[] protectedData, string purpose)
    {
        try
        {
            return ProtectedData.Unprotect(protectedData, Encoding.UTF8.GetBytes(purpose), DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            Trace.WriteLine($"[hzn-secrets] couldn't read a secret ({purpose}): {ex.Message}");
            return null;
        }
    }
}
