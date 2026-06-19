using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Sources.Test;

/// <summary>
/// Factory for <see cref="TestToneSource"/>. One numeric field for the
/// frequency so the smoke test can sweep across the spectrum without a
/// code change. Defaults to 440 Hz (A4).
/// </summary>
public sealed class TestToneSourceFactory : IAudioSourceFactory
{
    public const string KeyFrequency = "frequency";

    public string Id => "testtone";
    public string DisplayName => "Test Tone";
    public string? Description => "Diagnostic sine wave for verifying the audio pipe end-to-end.";

    public IReadOnlyList<ConfigField> Schema { get; } = new ConfigField[]
    {
        // TextField over a number-typed field on purpose: we don't yet
        // have a NumericField in the schema vocabulary, and a string
        // round-trip (parse on Create) is enough for this one-off.
        new TextField(
            Key:         KeyFrequency,
            Label:       "Frequency (Hz)",
            Default:     "440",
            Placeholder: "440",
            Description: "Tone frequency in Hertz. 440 = concert A."),
    };

    public IAudioSource Create(ConfigValues values)
    {
        var raw = values.GetString(KeyFrequency);
        double hz = 440.0;
        if (!string.IsNullOrWhiteSpace(raw) &&
            double.TryParse(raw, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var parsed))
        {
            hz = Math.Clamp(parsed, 20.0, 20000.0);
        }
        return new TestToneSource(hz);
    }
}
