using System.IO;
using Avalonia.Media.Imaging;

namespace HorizonRadio.UI.Imaging;

/// <summary>
/// Image-bytes → <see cref="Bitmap"/> decoding, in one place. Returns null for empty
/// or malformed payloads (callers fall back to a placeholder tile) rather than
/// throwing — album art from ID3 frames, provider responses, or partial downloads can
/// be non-image or truncated.
/// </summary>
public static class ImageBytes
{
    public static Bitmap? ToBitmap(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }
}
