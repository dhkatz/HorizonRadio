namespace HorizonRadio.Core.Sources;

/// <summary>Helpers for reading artist-credit strings, which sources hand us as a
/// comma-separated list ("A, B, C"). Centralized so the "primary artist" rule is
/// defined once rather than copied per source.</summary>
public static class ArtistCredits
{
    /// <summary>The primary (first non-empty) credit, trimmed; null when there is none.</summary>
    public static string? FirstOrNull(string? credits)
    {
        if (string.IsNullOrWhiteSpace(credits)) return null;
        foreach (var part in credits.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0) return trimmed;
        }
        return null;
    }
}
