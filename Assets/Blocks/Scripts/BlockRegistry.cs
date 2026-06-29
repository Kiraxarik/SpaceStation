using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Runtime block registry — the lookup surface the rest of the game reads from
/// (Faces[], Definitions, GetId, …). Public shape unchanged, so mesh systems and
/// interaction code are untouched.
///
/// Content is no longer loaded here. ContentBootstrap resolves the mod load order,
/// loads each mod's block definitions in order, and hands the full set to
/// BuildLocal. The loaded defs are cached so the client can re-number them against
/// the server's authoritative ordering at handshake (InitializeFromManifest)
/// without re-reading any files.
///
/// Numeric ids are assigned, never authored (§1.5):
///   BuildLocal               → deterministic local ordering (server, and client at startup)
///   InitializeFromManifest   → adopt the server's ordering (client, post-handshake)
/// </summary>
public static class BlockRegistry
{
    // ── Public API (unchanged contract) ───────────────────────────────────────

    public static BlockFaces[] Faces { get; private set; } = Array.Empty<BlockFaces>();

    public static IReadOnlyDictionary<string, byte> IdByName { get; private set; }
        = new Dictionary<string, byte>();

    public static IReadOnlyDictionary<byte, string> NameById { get; private set; }
        = new Dictionary<byte, string>();

    public static IReadOnlyDictionary<byte, BlockDefinitionData> Definitions { get; private set; }
        = new Dictionary<byte, BlockDefinitionData>();

    /// <summary>The session manifest this registry was built from. Module 4 serializes Manifest.Order.</summary>
    public static ContentManifest Manifest { get; private set; }

    /// <summary>
    /// Byte id for a block, or 0 (air) if not found. Accepts a namespaced id
    /// ("base:floor_tile") or a bare name ("floor_tile") resolved against base.
    /// </summary>
    public static byte GetId(string blockName)
    {
        if (IdByName.TryGetValue(blockName, out byte id)) return id;
        if (!blockName.Contains(':') && IdByName.TryGetValue($"base:{blockName}", out id)) return id;
        return 0;
    }

    public static BlockDefinitionData GetDefinition(byte id)
        => Definitions.TryGetValue(id, out var def) ? def : null;

    // ── Build ──────────────────────────────────────────────────────────────────

    // Cached so InitializeFromManifest can re-number the same loaded data against
    // the server ordering without re-reading mod files.
    static List<BlockDefinitionData> _loadedDefs;

    /// <summary>
    /// Builds the registry from the full, ordered set of block definitions using a
    /// locally-derived deterministic manifest. Called by ContentBootstrap at startup
    /// (server, and client before it connects).
    /// </summary>
    public static void BuildLocal(List<BlockDefinitionData> defs)
    {
        _loadedDefs = defs;
        var manifest = ContentManifest.Build(defs.Select(d => d.id));
        PopulateFrom(manifest, defs);
    }

    /// <summary>
    /// Re-numbers the already-loaded definitions against an externally-provided
    /// ordering — the server's authoritative manifest (Module 4). Local defs supply
    /// the DATA; the server supplies the IDENTITY ordering. A manifest entry with no
    /// matching local definition keeps default faces until its asset arrives.
    /// </summary>
    public static void InitializeFromManifest(IReadOnlyList<string> serverOrder)
    {
        if (_loadedDefs == null)
        {
            Debug.LogError("[BlockRegistry] InitializeFromManifest called before content was loaded. " +
                           "ContentBootstrap must run first.");
            return;
        }

        var manifest = ContentManifest.BuildFromOrder(serverOrder);
        PopulateFrom(manifest, _loadedDefs);
    }

    // ── Table population ───────────────────────────────────────────────────────

    static void PopulateFrom(ContentManifest manifest, List<BlockDefinitionData> defs)
    {
        // Resolve duplicate ids: later definition wins (mod load order).
        var defByName = new Dictionary<string, BlockDefinitionData>(defs.Count, StringComparer.Ordinal);
        foreach (var d in defs)
        {
            if (defByName.ContainsKey(d.id))
                Debug.LogWarning($"[BlockRegistry] Duplicate content id '{d.id}' — later definition wins.");
            defByName[d.id] = d;
        }

        int count = manifest.Count;
        var faces = new BlockFaces[count];
        var idByName = new Dictionary<string, byte>(count, StringComparer.Ordinal);
        var nameById = new Dictionary<byte, string>(count);
        var defsByByte = new Dictionary<byte, BlockDefinitionData>(count);

        int missingData = 0;

        for (int i = 0; i < count; i++)
        {
            byte id = (byte)i;
            string name = manifest.NameOf(id);

            idByName[name] = id;
            nameById[id] = name;

            if (defByName.TryGetValue(name, out var def))
            {
                faces[id] = def.tiles.ToBlockFaces();
                defsByByte[id] = def;
            }
            else if (id != ContentManifest.AirId)
            {
                missingData++;
            }
        }

        Faces = faces;
        IdByName = idByName;
        NameById = nameById;
        Definitions = defsByByte;
        Manifest = manifest;

        string missingNote = missingData > 0
            ? $" ({missingData} id(s) have no local data yet — awaiting asset sync)"
            : "";
        Debug.Log($"[BlockRegistry] Ready — {count} tile id(s) incl. air. Highest id {count - 1}{missingNote}.");
    }
}