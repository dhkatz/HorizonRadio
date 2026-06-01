using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace HorizonRadio.Core.Events;

/// <summary>
/// Persists the event→action bindings to
/// <c>%LOCALAPPDATA%\HorizonRadio\events-config.json</c>. Same hand-editable
/// shape as the other config stores. Reads are lock-free (the executor calls
/// <see cref="GetAction"/> from event-source threads); writes come from the
/// UI thread.
///
/// File shape:
/// <code>
/// { "rules": { "race_start": { "type": "NextTrack" },
///              "paused":     { "type": "SetVolume", "param": "0.3" } } }
/// </code>
/// </summary>
public sealed class EventRuleStore
{
    private readonly ConcurrentDictionary<string, EventAction> _rules = new();

    private static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HorizonRadio",
            "events-config.json");

    private static void Log(string msg) => Debug.WriteLine($"[hzn-events-cfg] {msg}");

    /// <summary>The action bound to <paramref name="kind"/>, or
    /// <see cref="EventAction.None"/> if unbound.</summary>
    public EventAction GetAction(string kind) =>
        _rules.TryGetValue(kind, out var a) ? a : EventAction.None;

    public void SetAction(string kind, EventAction action)
    {
        if (action.Type == EventActionType.None) _rules.TryRemove(kind, out _);
        else _rules[kind] = action;
    }

    public static EventRuleStore LoadFromDisk(string? path = null)
    {
        path ??= DefaultPath;
        var store = new EventRuleStore();
        try
        {
            if (!File.Exists(path)) return store;
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("rules", out var rules) ||
                rules.ValueKind != JsonValueKind.Object)
                return store;

            foreach (var rule in rules.EnumerateObject())
            {
                if (rule.Value.ValueKind != JsonValueKind.Object) continue;
                var typeStr = rule.Value.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (typeStr == null || !Enum.TryParse<EventActionType>(typeStr, out var type)) continue;
                var param = rule.Value.TryGetProperty("param", out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString()
                    : null;
                store._rules[rule.Name] = new EventAction(type, param);
            }
        }
        catch (Exception ex) { Log($"load failed: {ex.Message}"); }
        return store;
    }

    public void SaveToDisk(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = File.Create(path);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            writer.WriteStartObject("rules");
            foreach (var (kind, action) in _rules)
            {
                writer.WriteStartObject(kind);
                writer.WriteString("type", action.Type.ToString());
                if (!string.IsNullOrEmpty(action.Param))
                    writer.WriteString("param", action.Param);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        catch (Exception ex) { Log($"save failed: {ex.Message}"); }
    }

    // -- helpers for the SetVolume param (invariant round-trip) --

    public static string FormatVolume(double level) =>
        level.ToString("0.###", CultureInfo.InvariantCulture);

    public static double ParseVolume(string? param, double fallback = 1.0) =>
        double.TryParse(param, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;
}
