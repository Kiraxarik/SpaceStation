using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Runtime block registry — the lookup surface the rest of the game reads from
/// (Faces[], Definitions, GetId, …). Its public shape is unchanged, so the mesh
/// systems and interaction code are untouched.
///
/// What changed: the registry no longer parses files or reads authored numeric
/// ids itself. It composes the two separated tasks instead:
///   Task A  BlockContentLoader.LoadAll  → definitions keyed by namespaced id
///   Task B  ContentManifest.Build       → deterministic string id → byte id map
/// then indexes its tables by the manifest's numeric ids.
///
/// Numeric ids are now ASSIGNED, never authored (architecture §1.5). Initialize()
/// derives the assignment locally and deterministically — correct for the server,
/// and correct for the client as long as content matches. When content can differ
/// (real mods), the client will instead call InitializeFromManifest() with the
/// server's authoritative ordering (Module 4); everything below already supports
/// that — only the source of the ordering changes.
/// </summary>
public static class BlockRegistry
{
    // ── Public API (unchanged contract) ───────────────────────────────────────

    /// <summary>Indexed by byte block value. Faces[id].ForDirection(dir) → atlas tile.</summary>
    public static BlockFaces[] Faces { get; private set; } = Array.Empty<BlockFaces>();

    /// <summary>Maps stable string id ("base:wall_panel") → session byte id.</summary>
    public static IReadOnlyDictionary<string, byte> IdByName { get; private set; }
        = new Dictionary<string, byte>();

    /// <summary>Maps session byte id → stable string id. Use this when writing saves.</summary>
    public static IReadOnlyDictionary<byte, string> NameById { get; private set; }
        = new Dictionary<byte, string>();

    /// <summary>Full definitions (sim properties) for Phase 3 systems.</summary>
    public static IReadOnlyDictionary<byte, BlockDefinitionData> Definitions { get; private set; }
        = new Dictionary<byte, BlockDefinitionData>();

    /// <summary>
    /// The session manifest (string id ↔ byte id ordering) this registry was
    /// built from. Module 4 serializes Manifest.Order across the handshake.
    /// </summary>
    public static ContentManifest Manifest { get; private set; }

    /// <summary>
    /// Returns the byte id for a block, or 0 (air) if not found. Accepts a fully
    /// namespaced id ("base:floor_tile") or a bare name ("floor_tile"), which is
    /// resolved against the base namespace for convenience so game code needn't
    /// hardcode the "base:" prefix everywhere.
    /// </summary>
    public static byte GetId(string blockName)
    {
        if (IdByName.TryGetValue(blockName, out byte id)) return id;

        if (!blockName.Contains(':') &&
            IdByName.TryGetValue($"{BlockContentLoader.BaseNamespace}:{blockName}", out id))
            return id;

        return 0;
    }

    /// <summary>Returns the definition for a byte id, or null if unregistered.</summary>
    public static BlockDefinitionData GetDefinition(byte id)
        => Definitions.TryGetValue(id, out var def) ? def : null;

    // ── Initialisation ────────────────────────────────────────────────────────

    /// <summary>
    /// Loads local content and numbers it with a locally-derived deterministic
    /// manifest. Called by BlockRegistryConfig.AutoLoad() before any scene loads.
    /// </summary>
    public static void Initialize(BlockRegistryConfig config)
    {
        var defs = BlockContentLoader.LoadAll(config);                 // Task A
        var manifest = ContentManifest.Build(defs.Select(d => d.id));  // Task B (local authority)
        PopulateFrom(manifest, defs);
    }

    /// <summary>
    /// Loads local content but numbers it with an externally-provided ordering —
    /// the server's authoritative manifest. Used by the client post-handshake
    /// (Module 4). Local definitions supply the DATA (faces, sim props); the
    /// server supplies the IDENTITY ordering. A manifest entry with no matching
    /// local definition keeps default faces until its asset arrives (Module 5).
    /// </summary>
    public static void InitializeFromManifest(IReadOnlyList<string> serverOrder, BlockRegistryConfig config)
    {
        var defs = BlockContentLoader.LoadAll(config);                 // Task A
        var manifest = ContentManifest.BuildFromOrder(serverOrder);    // Task B (server authority)
        PopulateFrom(manifest, defs);
    }

    // ── Table population ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds the runtime lookup tables from a manifest (the numbering) plus the
    /// loaded definitions (the data). Indexed strictly by the manifest's numeric
    /// ids so Faces[]/Definitions agree with whatever ordering was chosen.
    /// </summary>
    static void PopulateFrom(ContentManifest manifest, List<BlockDefinitionData> defs)
    {
        // Resolve duplicates by string id here: later definition wins (load order).
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
                // Air legitimately may carry no faces; anything else missing here
                // means the manifest references content we have no local data for.
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