using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runtime registry of block tile textures — string-keyed, parallel to
/// ModelRegistry/SoundRegistry. Holds tile descriptors (id, source PNG) for every
/// tile the loaded mods provide. The atlas baker (3b) consumes these to build the
/// Texture2DArray and the tile-id → slice map; the asset pipeline ships the PNGs.
///
/// Orchestrator-driven: ContentBootstrap loads each mod's tiles in resolved order
/// and hands the full set to Initialize. Lives on both client and server (the
/// server is authoritative for the set; only the client bakes/renders).
/// </summary>
public static class TileRegistry
{
    public static IReadOnlyDictionary<string, TileContent> ById { get; private set; }
        = new Dictionary<string, TileContent>();

    public static IEnumerable<TileContent> All => ById.Values;
    public static int Count => ById.Count;

    public static TileContent Get(string tileId)
    {
        if (ById.TryGetValue(tileId, out var t)) return t;
        if (!tileId.Contains(':') && ById.TryGetValue($"base:{tileId}", out t)) return t;
        return null;
    }

    public static void Initialize(List<TileContent> content)
    {
        var byId = new Dictionary<string, TileContent>(content.Count, StringComparer.Ordinal);

        foreach (var c in content)
        {
            if (byId.ContainsKey(c.Id))
                Debug.LogWarning($"[TileRegistry] Duplicate tile id '{c.Id}' — later definition wins.");
            byId[c.Id] = c;
        }

        ById = byId;
        Debug.Log($"[TileRegistry] Ready — {byId.Count} tile(s).");
    }
}