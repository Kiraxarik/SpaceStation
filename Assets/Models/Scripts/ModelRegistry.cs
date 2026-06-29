using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runtime registry of model content — the string-keyed, sparse-layer counterpart
/// to BlockRegistry. Holds descriptors (id, type, asset file paths) for every
/// character/prop/item the loaded mods provide. No tile bytes, no faces, no parsed
/// geometry.
///
/// Like BlockRegistry, it no longer self-loads: ContentBootstrap loads each mod's
/// models in resolved order and hands the full set to Initialize. Lives on both
/// client and server — the server is authoritative for the set (drives the asset
/// manifest); the client loads its own to know what it has and what it's missing.
/// </summary>
public static class ModelRegistry
{
    public static IReadOnlyDictionary<string, ModelContent> ById { get; private set; }
        = new Dictionary<string, ModelContent>();

    /// <summary>Enumerates all loaded model content (for the asset manifest, distribution).</summary>
    public static IEnumerable<ModelContent> All => ById.Values;

    public static int Count => ById.Count;

    /// <summary>
    /// Model content by id. Accepts a namespaced id ("base:oak_table") or a bare
    /// name ("oak_table") resolved against base. Null if not found.
    /// </summary>
    public static ModelContent Get(string modelId)
    {
        if (ById.TryGetValue(modelId, out var m)) return m;
        if (!modelId.Contains(':') && ById.TryGetValue($"base:{modelId}", out m)) return m;
        return null;
    }

    /// <summary>
    /// Builds the registry from the full, ordered set of model content. Duplicate
    /// ids resolve last-wins (mod load order), matching the block side.
    /// </summary>
    public static void Initialize(List<ModelContent> content)
    {
        var byId = new Dictionary<string, ModelContent>(content.Count, StringComparer.Ordinal);

        foreach (var c in content)
        {
            if (byId.ContainsKey(c.Id))
                Debug.LogWarning($"[ModelRegistry] Duplicate model id '{c.Id}' — later definition wins.");
            byId[c.Id] = c;
        }

        ById = byId;
        Debug.Log($"[ModelRegistry] Ready — {byId.Count} model content item(s).");
    }
}