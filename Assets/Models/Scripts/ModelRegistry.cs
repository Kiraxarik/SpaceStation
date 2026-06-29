using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runtime registry of model content — the string-keyed, sparse-layer counterpart
/// to BlockRegistry. Holds descriptors (id, type, asset file paths) for every
/// character/prop/item the loaded mods provide. No tile bytes, no faces, no
/// parsed geometry: this is the "what model content exists and what files it
/// ships" layer that the asset pipeline (hash/distribute) and future runtime
/// consumers (detail mesh, character animation) both read from.
///
/// Initialized at startup like BlockRegistry, so it's populated before any system
/// runs. Lives on both client and server: the server is authoritative for the set
/// (it drives the asset manifest); the client loads its own to know what it has
/// and what it's missing.
/// </summary>
public static class ModelRegistry
{
    /// <summary>All model content by namespaced id ("base:oak_table").</summary>
    public static IReadOnlyDictionary<string, ModelContent> ById { get; private set; }
        = new Dictionary<string, ModelContent>();

    /// <summary>Enumerates all loaded model content (for the asset manifest, distribution).</summary>
    public static IEnumerable<ModelContent> All => ById.Values;

    /// <summary>Number of registered model content items.</summary>
    public static int Count => ById.Count;

    /// <summary>
    /// Looks up model content by id. Accepts a fully namespaced id
    /// ("base:oak_table") or a bare name ("oak_table"), resolved against the base
    /// namespace for convenience. Null if not found.
    /// </summary>
    public static ModelContent Get(string modelId)
    {
        if (ById.TryGetValue(modelId, out var m)) return m;
        if (!modelId.Contains(':') && ById.TryGetValue($"base:{modelId}", out m)) return m;
        return null;
    }

    // ── Initialisation ─────────────────────────────────────────────────────────

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void AutoLoad()
    {
        Initialize(ModelContentLoader.LoadAll());
    }

    /// <summary>
    /// Builds the registry from loaded content. Duplicate ids resolve last-wins
    /// (load order), matching the block side.
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