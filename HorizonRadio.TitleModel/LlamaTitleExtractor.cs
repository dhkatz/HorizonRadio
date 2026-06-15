using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Models;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace HorizonRadio.TitleModel;

/// <summary>
/// An <see cref="ITitleExtractor"/> backed by a local GGUF model via LLamaSharp (llama.cpp). It
/// reads one freeform stream title and returns a single (artist, title) hypothesis — useful for
/// the cases the deterministic parser can't handle (no separators, reversed order, mixed-language
/// EN-artist/JP-title). A GBNF grammar constrains the model's output to a tiny JSON object, so the
/// result is always parseable; the resolver still catalog-validates it, so a wrong extraction
/// can't reach the UI.
///
/// The model (~0.5–1.2 GB RAM for a 0.6–1.7B Q4 model) loads lazily on the first call — never at
/// startup and never if radio is unused — and stays loaded for the session. Inference is
/// serialized (one at a time) because a single context isn't thread-safe and concurrency would
/// only thrash CPU/RAM.
/// </summary>
public sealed class LlamaTitleExtractor : ITitleExtractor
{
    // Multilingual extraction instructions. Kept short — every token here is processed on each
    // call. The grammar (below) guarantees the shape, so the prompt only needs to teach intent.
    private const string SystemPrompt =
        "You extract the performing artist and the song title from a music radio \"now playing\" " +
        "string. The text may be in any language (English, Japanese, etc.) and may mix languages " +
        "(e.g. an English artist with a Japanese title). It may contain a station/channel name, " +
        "bracketed vocalist/circle tags, or the title and artist in either order. Return the song " +
        "title and the performing artist. If the artist is unknown, return an empty string for it. " +
        "Do not translate; copy the names exactly as written.";

    // Force the output to exactly {"artist": "...", "title": "..."} — no prose, always valid JSON.
    private const string Gbnf =
        "root ::= \"{\" ws \"\\\"artist\\\"\" ws \":\" ws string ws \",\" ws \"\\\"title\\\"\" ws \":\" ws string ws \"}\"\n" +
        "string ::= \"\\\"\" char* \"\\\"\"\n" +
        "char ::= [^\"\\\\] | \"\\\\\" ([\"\\\\/bfnrt] | \"u\" [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F])\n" +
        "ws ::= [ \\t\\n]*";

    private readonly Func<string?> _modelPath;
    private readonly int _gpuLayerCount;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private LLamaWeights? _weights;
    private StatelessExecutor? _executor;
    private bool _loadFailed;

    /// <param name="modelPath">Resolves the GGUF path each load attempt (re-read so installing the
    /// model mid-session works); null/absent → the extractor no-ops.</param>
    /// <param name="gpuLayerCount">Layers to offload to GPU. 0 = CPU-only (the shipped default);
    /// raising it with a GPU backend package enables GPU acceleration with no other change.</param>
    public LlamaTitleExtractor(Func<string?> modelPath, int gpuLayerCount = 0)
    {
        _modelPath = modelPath;
        _gpuLayerCount = gpuLayerCount;
    }

    private static void Log(string msg) => Debug.WriteLine($"[hzn-titlemodel] {msg}");

    public async Task<IReadOnlyList<TitleCandidate>> ExtractAsync(string rawTitle, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawTitle)) return [];

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!EnsureLoaded()) return [];

            var prompt = BuildPrompt(rawTitle.Trim());
            var inferenceParams = new InferenceParams
            {
                MaxTokens = 128,
                AntiPrompts = ["<|im_end|>", "\n\n"],
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = 0.1f,
                    Grammar = new Grammar(Gbnf, "root"),
                },
            };

            var sb = new StringBuilder(128);
            await foreach (var tok in _executor!.InferAsync(prompt, inferenceParams, ct).ConfigureAwait(false))
            {
                sb.Append(tok);
                if (sb.Length > 1024) break; // grammar bounds this, but never run away
            }

            return Parse(sb.ToString());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log($"inference failed: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    // Caller holds _gate. Returns false (no-op) when no model is installed yet — without marking a
    // permanent failure, so a mid-session install is picked up on the next call. A genuine load
    // error (corrupt/incompatible file) latches _loadFailed so we don't re-attempt every title.
    private bool EnsureLoaded()
    {
        if (_executor != null) return true;
        if (_loadFailed) return false;

        var path = _modelPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

        try
        {
            var parameters = new ModelParams(path)
            {
                ContextSize = 2048,
                GpuLayerCount = _gpuLayerCount,
            };
            _weights = LLamaWeights.LoadFromFile(parameters);
            _executor = new StatelessExecutor(_weights, parameters);
            Log($"loaded model: {Path.GetFileName(path)} (gpuLayers={_gpuLayerCount})");
            return true;
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            _weights?.Dispose();
            _weights = null;
            _executor = null;
            Log($"model load failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // ChatML, matching the pinned Qwen-family instruct model's template.
    private static string BuildPrompt(string raw) =>
        $"<|im_start|>system\n{SystemPrompt}<|im_end|>\n<|im_start|>user\n{raw}<|im_end|>\n<|im_start|>assistant\n";

    private static IReadOnlyList<TitleCandidate> Parse(string json)
    {
        json = json.Trim();
        if (json.Length == 0) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var title = root.TryGetProperty("title", out var t) ? t.GetString()?.Trim() : null;
            var artist = root.TryGetProperty("artist", out var a) ? a.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(title)) return [];
            return [new TitleCandidate(string.IsNullOrWhiteSpace(artist) ? null : artist, title)];
        }
        catch (JsonException ex)
        {
            Log($"unparseable output: {ex.Message}");
            return [];
        }
    }

    public ValueTask DisposeAsync()
    {
        _weights?.Dispose();
        _weights = null;
        _executor = null;
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
