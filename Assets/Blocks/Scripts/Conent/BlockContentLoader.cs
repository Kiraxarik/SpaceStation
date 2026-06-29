using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Task A (dense tile layer): parse one mod's blocks.json into namespaced block
/// definitions. No numeric ids (that's ContentManifest), no discovery — the mod
/// package loader owns discovery and load order and calls this per mod, in order.
///
/// The namespace is the mod's canonical id (from mod.json), so a block authored
/// "wall_panel" in mod "base" becomes "base:wall_panel". An id already containing
/// ':' is left as authored, letting a mod target/override another's content (the
/// override is well-defined because the resolver guarantees the overriding mod
/// loads later).
/// </summary>
public static class BlockContentLoader
{
    const string BlocksFileName = "blocks.json";

    /// <summary>
    /// Loads block definitions from one mod folder. Returns empty if the mod has
    /// no blocks.json (mods needn't define blocks).
    /// </summary>
    public static List<BlockDefinitionData> LoadFromMod(string modDir, string ns)
    {
        var result = new List<BlockDefinitionData>();

        string path = Path.Combine(modDir, BlocksFileName);
        if (!File.Exists(path)) return result;

        BlocksFile file;
        try
        {
            file = JsonUtility.FromJson<BlocksFile>(File.ReadAllText(path));
        }
        catch (Exception e)
        {
            Debug.LogError($"[BlockContentLoader] Failed to parse '{ns}/{BlocksFileName}': {e.Message}");
            return result;
        }

        if (file?.blocks == null)
        {
            Debug.LogWarning($"[BlockContentLoader] '{ns}/{BlocksFileName}' is empty or malformed.");
            return result;
        }

        foreach (var def in file.blocks)
        {
            if (def == null || string.IsNullOrWhiteSpace(def.id))
            {
                Debug.LogWarning($"[BlockContentLoader] Entry in '{ns}/{BlocksFileName}' has no id. Skipping.");
                continue;
            }

            def.id = def.id.Contains(':') ? def.id : $"{ns}:{def.id}";
            result.Add(def);
        }

        Debug.Log($"[BlockContentLoader] '{ns}': {result.Count} block(s).");
        return result;
    }

    // ── JSON shape ─────────────────────────────────────────────────────────────

    [Serializable]
    class BlocksFile
    {
        public BlockDefinitionData[] blocks = Array.Empty<BlockDefinitionData>();
    }
}