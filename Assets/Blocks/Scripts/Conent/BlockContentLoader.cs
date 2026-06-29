using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Task A of the content pipeline: discovery + parse + namespacing.
///
/// Reads block definitions from the base game (a TextAsset on BlockRegistryConfig)
/// and from each mod (StreamingAssets/Mods/&lt;mod&gt;/blocks.json), and returns them
/// keyed by stable, namespaced string id. It assigns NO numeric ids — that is
/// Task B (ContentManifest). Keeping the two apart is what lets the same loaded
/// set be numbered by a local deterministic build (today) or by the server's
/// authoritative ordering (Module 4) without re-reading any files.
///
/// Namespacing: a bare authored id ("wall_panel") is prefixed with its source
/// namespace → "base:wall_panel" for the base file, "&lt;modfolder&gt;:wall_panel" for a
/// mod. An id that already contains ':' is left untouched, so a mod can author
/// "base:wall_panel" to deliberately target/override base content. (Override
/// precedence when two sources share an id is "later load wins"; an explicit
/// mod load-order is a later concern — for now base loads first, then mods.)
/// </summary>
public static class BlockContentLoader
{
    public const string BaseNamespace = "base";
    const string ModBlocksFile = "blocks.json";

    /// <summary>
    /// Loads every block definition from base + mods, with ids namespaced.
    /// Duplicates are NOT resolved here (the registry decides override precedence);
    /// this just returns everything it found, in load order (base first).
    /// </summary>
    public static List<BlockDefinitionData> LoadAll(BlockRegistryConfig config)
    {
        var all = new List<BlockDefinitionData>();

        // ── 1. Base game ──────────────────────────────────────────────────────
        if (config != null && config.BaseBlocksFile != null)
        {
            var parsed = ParseAndNamespace(
                config.BaseBlocksFile.text, config.BaseBlocksFile.name, BaseNamespace);
            all.AddRange(parsed);
            Debug.Log($"[BlockContentLoader] Base: {parsed.Count} block(s) from '{config.BaseBlocksFile.name}'.");
        }
        else
        {
            Debug.LogError("[BlockContentLoader] No Base Blocks File assigned in BlockRegistryConfig. " +
                           "Drag your blocks.json into the field in the Inspector.");
        }

        // ── 2. Mods (StreamingAssets/Mods/<mod>/blocks.json) ──────────────────
        string modsRoot = Path.Combine(Application.streamingAssetsPath, "Mods");
        if (Directory.Exists(modsRoot))
        {
            foreach (string modDir in Directory.EnumerateDirectories(modsRoot))
            {
                string modName = Path.GetFileName(modDir);
                string ns = Sanitize(modName);
                string modFile = Path.Combine(modDir, ModBlocksFile);
                if (!File.Exists(modFile)) continue;

                try
                {
                    var parsed = ParseAndNamespace(
                        File.ReadAllText(modFile), $"{modName}/{ModBlocksFile}", ns);
                    all.AddRange(parsed);
                    Debug.Log($"[BlockContentLoader] Mod '{modName}' (ns '{ns}'): {parsed.Count} block(s).");
                }
                catch (Exception e)
                {
                    Debug.LogError($"[BlockContentLoader] Failed to load mod '{modName}': {e.Message}");
                }
            }
        }

        return all;
    }

    // ── Parse + namespace one file ─────────────────────────────────────────────

    static List<BlockDefinitionData> ParseAndNamespace(string json, string sourceName, string ns)
    {
        var result = new List<BlockDefinitionData>();

        BlocksFile file;
        try
        {
            file = JsonUtility.FromJson<BlocksFile>(json);
        }
        catch (Exception e)
        {
            Debug.LogError($"[BlockContentLoader] Failed to parse '{sourceName}': {e.Message}");
            return result;
        }

        if (file?.blocks == null)
        {
            Debug.LogWarning($"[BlockContentLoader] '{sourceName}' is empty or malformed.");
            return result;
        }

        foreach (var def in file.blocks)
        {
            if (def == null || string.IsNullOrWhiteSpace(def.id))
            {
                Debug.LogWarning($"[BlockContentLoader] Entry in '{sourceName}' has no id. Skipping.");
                continue;
            }

            def.id = Namespace(def.id, ns);
            result.Add(def);
        }

        return result;
    }

    /// <summary>Prefixes a bare id with its source namespace; respects an explicit one.</summary>
    static string Namespace(string id, string ns)
        => id.Contains(':') ? id : $"{ns}:{id}";

    /// <summary>Lowercases and replaces whitespace so a folder name is a clean namespace.</summary>
    static string Sanitize(string raw)
    {
        var chars = raw.Trim().ToLowerInvariant().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (char.IsWhiteSpace(chars[i]) || chars[i] == ':') chars[i] = '_';
        return new string(chars);
    }

    // ── JSON shape ─────────────────────────────────────────────────────────────

    [Serializable]
    class BlocksFile
    {
        public BlockDefinitionData[] blocks = Array.Empty<BlockDefinitionData>();
    }
}