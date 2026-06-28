using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Runtime block registry. Initialized via BlockRegistryConfig before any
/// scene or ECS world is created.
///
/// Base game blocks come from the TextAsset assigned in BlockRegistryConfig.
/// Mod blocks come from StreamingAssets/Mods/*/blocks.json at runtime.
/// </summary>
public static class BlockRegistry
{
    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Indexed by byte block value. Faces[id].ForDirection(dir) → atlas tile.
    /// Same contract as the old hardcoded array — mesh systems are unchanged.
    /// </summary>
    public static BlockFaces[] Faces { get; private set; } = Array.Empty<BlockFaces>();

    /// <summary>Maps stable string id ("wall_panel") → runtime byte id.</summary>
    public static IReadOnlyDictionary<string, byte> IdByName { get; private set; }
        = new Dictionary<string, byte>();

    /// <summary>
    /// Maps runtime byte id → stable string id.
    /// Use when writing world saves so saves aren't tied to numeric ids.
    /// </summary>
    public static IReadOnlyDictionary<byte, string> NameById { get; private set; }
        = new Dictionary<byte, string>();

    /// <summary>
    /// Full definitions including simulation properties (solid, atmos_passable,
    /// conductivity) for Phase 3 systems.
    /// </summary>
    public static IReadOnlyDictionary<byte, BlockDefinitionData> Definitions { get; private set; }
        = new Dictionary<byte, BlockDefinitionData>();

    /// <summary>Returns the byte id for a named block, or 0 (air) if not found.</summary>
    public static byte GetId(string blockName)
        => IdByName.TryGetValue(blockName, out byte id) ? id : (byte)0;

    /// <summary>Returns the definition for a byte id, or null if unregistered.</summary>
    public static BlockDefinitionData GetDefinition(byte id)
        => Definitions.TryGetValue(id, out var def) ? def : null;

    // ── Initialisation ────────────────────────────────────────────────────────

    /// <summary>
    /// Called by BlockRegistryConfig.AutoLoad() before any scene loads.
    /// Not intended to be called manually.
    /// </summary>
    public static void Initialize(BlockRegistryConfig config)
    {
        var definitions = new List<BlockDefinitionData>();
        int modCount = 0;

        // ── 1. Base game blocks ────────────────────────────────────────────────
        if (config.BaseBlocksFile != null)
        {
            List<BlockDefinitionData> parsed = ParseJson(
                config.BaseBlocksFile.text,
                config.BaseBlocksFile.name);
            definitions.AddRange(parsed);
            Debug.Log($"[BlockRegistry] Base: {parsed.Count} block(s) from '{config.BaseBlocksFile.name}'.");
        }
        else
        {
            Debug.LogError("[BlockRegistry] No Base Blocks File assigned in BlockRegistryConfig. " +
                           "Drag your blocks.json into the field in the Inspector.");
        }

        // ── 2. Mod blocks (StreamingAssets/Mods/*/blocks.json) ────────────────
        string modsRoot = Path.Combine(Application.streamingAssetsPath, "Mods");
        if (Directory.Exists(modsRoot))
        {
            foreach (string modDir in Directory.EnumerateDirectories(modsRoot))
            {
                string modName = Path.GetFileName(modDir);
                string modFile = Path.Combine(modDir, "blocks.json");
                if (!File.Exists(modFile)) continue;

                try
                {
                    List<BlockDefinitionData> parsed = ParseJson(
                        File.ReadAllText(modFile),
                        $"{modName}/blocks.json");
                    definitions.AddRange(parsed);
                    modCount += parsed.Count;
                    Debug.Log($"[BlockRegistry] Mod '{modName}': {parsed.Count} block(s).");
                }
                catch (Exception e)
                {
                    Debug.LogError($"[BlockRegistry] Failed to load mod '{modName}': {e.Message}");
                }
            }
        }

        // ── 3. Build lookup tables ─────────────────────────────────────────────
        definitions.Sort((a, b) => a.numeric_id.CompareTo(b.numeric_id));

        int maxId = 0;
        foreach (var def in definitions)
            maxId = Math.Max(maxId, def.numeric_id);

        var faces = new BlockFaces[maxId + 1];
        var idByName = new Dictionary<string, byte>(definitions.Count);
        var nameById = new Dictionary<byte, string>(definitions.Count);
        var defsByByte = new Dictionary<byte, BlockDefinitionData>(definitions.Count);

        foreach (BlockDefinitionData def in definitions)
        {
            if (def.numeric_id < 0 || def.numeric_id > 255)
            {
                Debug.LogError($"[BlockRegistry] '{def.id}' numeric_id {def.numeric_id} " +
                               "is outside byte range 0-255. Skipping.");
                continue;
            }

            byte byteId = (byte)def.numeric_id;

            if (idByName.ContainsKey(def.id))
                Debug.LogWarning($"[BlockRegistry] Duplicate id '{def.id}' — " +
                                 $"overriding with numeric_id {byteId}.");

            faces[byteId] = def.tiles.ToBlockFaces();
            idByName[def.id] = byteId;
            nameById[byteId] = def.id;
            defsByByte[byteId] = def;
        }

        Faces = faces;
        IdByName = idByName;
        NameById = nameById;
        Definitions = defsByByte;

        int baseCount = definitions.Count - modCount;
        Debug.Log($"[BlockRegistry] Ready — {baseCount} base + {modCount} mod " +
                  $"= {definitions.Count} block(s). Highest id: {maxId}.");
    }

    // ── JSON parsing ──────────────────────────────────────────────────────────

    [Serializable]
    class BlocksFile
    {
        public BlockDefinitionData[] blocks = Array.Empty<BlockDefinitionData>();
    }

    static List<BlockDefinitionData> ParseJson(string json, string sourceName)
    {
        var result = new List<BlockDefinitionData>();
        try
        {
            var file = JsonUtility.FromJson<BlocksFile>(json);
            if (file?.blocks == null)
            {
                Debug.LogWarning($"[BlockRegistry] '{sourceName}' is empty or malformed.");
                return result;
            }
            foreach (var def in file.blocks)
            {
                if (string.IsNullOrWhiteSpace(def?.id))
                {
                    Debug.LogWarning($"[BlockRegistry] Entry in '{sourceName}' has no id. Skipping.");
                    continue;
                }
                result.Add(def);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[BlockRegistry] Failed to parse '{sourceName}': {e.Message}");
        }
        return result;
    }
}