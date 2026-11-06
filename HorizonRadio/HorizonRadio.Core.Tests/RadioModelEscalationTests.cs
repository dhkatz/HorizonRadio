using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources.Radio;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The title-extraction model's escalation seam: when the model is invoked (the run policy gate)
/// and how its extraction is merged with the deterministic parse (append vs promote). Exercises
/// the pure decision functions plus a fake <see cref="ITitleExtractor"/> so no real model is needed.
/// </summary>
public class RadioModelEscalationTests
{
    // -- ShouldRunModel: the run-policy gate --

    [Fact]
    public void Off_never_runs_even_with_a_model()
    {
        Assert.False(RadioPlayableItem.ShouldRunModel(TitleModelMode.Off, hasExtractor: true, ParseConfidence.Low));
    }

    [Fact]
    public void No_extractor_never_runs()
    {
        Assert.False(RadioPlayableItem.ShouldRunModel(TitleModelMode.Always, hasExtractor: false, ParseConfidence.Low));
        Assert.False(RadioPlayableItem.ShouldRunModel(TitleModelMode.Escalate, hasExtractor: false, ParseConfidence.Low));
    }

    [Theory]
    [InlineData(ParseConfidence.Low, true)]
    [InlineData(ParseConfidence.Medium, true)]
    [InlineData(ParseConfidence.High, false)] // a clean parse is left alone under Escalate
    public void Escalate_runs_only_on_a_shaky_parse(ParseConfidence confidence, bool expected)
    {
        Assert.Equal(expected,
            RadioPlayableItem.ShouldRunModel(TitleModelMode.Escalate, hasExtractor: true, confidence));
    }

    [Theory]
    [InlineData(ParseConfidence.Low)]
    [InlineData(ParseConfidence.Medium)]
    [InlineData(ParseConfidence.High)] // Always runs even on a clean parse
    public void Always_runs_regardless_of_confidence(ParseConfidence confidence)
    {
        Assert.True(RadioPlayableItem.ShouldRunModel(TitleModelMode.Always, hasExtractor: true, confidence));
    }

    // -- ComposeWithModel: merge (Escalate) vs promote (Always) --

    [Fact]
    public void Escalate_keeps_deterministic_display_and_appends_model_as_fallback()
    {
        var detPrimary = new TitleCandidate("ChannelGuess", "Heavenz - テロメアの産声");
        var detAlts = new List<TitleCandidate> { new("ExGrooveCh", "Heavenz - テロメアの産声") };
        var model = new List<TitleCandidate> { new("Heavenz", "テロメアの産声") };

        var (title, artist, candidates) =
            RadioPlayableItem.ComposeWithModel(TitleModelMode.Escalate, model, detPrimary, detAlts);

        // Display unchanged — the deterministic primary stays on screen.
        Assert.Equal("Heavenz - テロメアの産声", title);
        Assert.Equal("ChannelGuess", artist);
        // The model's hypothesis is now a catalog-validated fallback candidate.
        Assert.Contains(candidates, c => c.Artist == "Heavenz" && c.Title == "テロメアの産声");
        // The deterministic alternative is still present too.
        Assert.Contains(candidates, c => c.Artist == "ExGrooveCh");
    }

    [Fact]
    public void Always_promotes_the_model_and_keeps_deterministic_as_fallback()
    {
        var detPrimary = new TitleCandidate("ChannelGuess", "Heavenz - テロメアの産声");
        var detAlts = new List<TitleCandidate> { new("ExGrooveCh", "Heavenz - テロメアの産声") };
        var model = new List<TitleCandidate> { new("Heavenz", "テロメアの産声") };

        var (title, artist, candidates) =
            RadioPlayableItem.ComposeWithModel(TitleModelMode.Always, model, detPrimary, detAlts);

        // Display is now the model's clean extraction.
        Assert.Equal("テロメアの産声", title);
        Assert.Equal("Heavenz", artist);
        // The deterministic primary survives as a fallback in case the model's guess misses.
        Assert.Contains(candidates, c => c.Artist == "ChannelGuess" && c.Title == "Heavenz - テロメアの産声");
        // The promoted hypothesis isn't duplicated in its own candidate list.
        Assert.DoesNotContain(candidates, c => c.Artist == "Heavenz" && c.Title == "テロメアの産声");
    }

    [Fact]
    public void Compose_dedups_a_model_hypothesis_that_equals_the_deterministic_primary()
    {
        var detPrimary = new TitleCandidate("Artist", "Song");
        var model = new List<TitleCandidate> { new("artist", "song") }; // same, different case

        var (_, _, candidates) =
            RadioPlayableItem.ComposeWithModel(TitleModelMode.Escalate, model, detPrimary, []);

        Assert.Empty(candidates); // the case-insensitive duplicate of the primary is dropped
    }

    [Fact]
    public async Task Fake_extractor_output_feeds_the_compose_seam()
    {
        ITitleExtractor fake = new FakeTitleExtractor([new TitleCandidate("Heavenz", "テロメアの産声")]);
        var model = await fake.ExtractAsync("ExGrooveCh - Heavenz - テロメアの産声", CancellationToken.None);

        var (title, artist, _) = RadioPlayableItem.ComposeWithModel(
            TitleModelMode.Always, model, new TitleCandidate("ExGrooveCh", "Heavenz - テロメアの産声"), []);

        Assert.Equal("テロメアの産声", title);
        Assert.Equal("Heavenz", artist);
    }

    private sealed class FakeTitleExtractor(IReadOnlyList<TitleCandidate> result) : ITitleExtractor
    {
        public Task<IReadOnlyList<TitleCandidate>> ExtractAsync(string rawTitle, CancellationToken ct) =>
            Task.FromResult(result);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
