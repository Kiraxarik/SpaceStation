using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runtime registry of sound content — string-keyed, parallel to ModelRegistry.
/// Holds descriptors (id, clip paths, playback params) for every sound the loaded
/// mods provide. No decoded audio: this is the "what sounds exist and what files
/// they ship" layer the asset pipeline and the future audio runtime read from.
///
/// Orchestrator-driven: ContentBootstrap loads each mod's sounds in resolved order
/// and hands the full set to Initialize. Lives on both client and server — the
/// server is authoritative for the set; the client loads its own to know what it
/// has and what it's missing.
/// </summary>
public static class SoundRegistry
{
    public static IReadOnlyDictionary<string, SoundContent> ById { get; private set; }
        = new Dictionary<string, SoundContent>();

    /// <summary>Enumerates all loaded sound content (for the asset manifest, distribution).</summary>
    public static IEnumerable<SoundContent> All => ById.Values;

    public static int Count => ById.Count;

    /// <summary>
    /// Sound content by id. Accepts a namespaced id ("base:footstep_metal") or a
    /// bare name ("footstep_metal") resolved against base. Null if not found.
    /// </summary>
    public static SoundContent Get(string soundId)
    {
        if (ById.TryGetValue(soundId, out var s)) return s;
        if (!soundId.Contains(':') && ById.TryGetValue($"base:{soundId}", out s)) return s;
        return null;
    }

    /// <summary>
    /// Builds the registry from the full, ordered set of sound content. Duplicate
    /// ids resolve last-wins (mod load order), matching the block and model sides.
    /// </summary>
    public static void Initialize(List<SoundContent> content)
    {
        var byId = new Dictionary<string, SoundContent>(content.Count, StringComparer.Ordinal);

        foreach (var c in content)
        {
            if (byId.ContainsKey(c.Id))
                Debug.LogWarning($"[SoundRegistry] Duplicate sound id '{c.Id}' — later definition wins.");
            byId[c.Id] = c;
        }

        ById = byId;
        Debug.Log($"[SoundRegistry] Ready — {byId.Count} sound content item(s).");
    }
}