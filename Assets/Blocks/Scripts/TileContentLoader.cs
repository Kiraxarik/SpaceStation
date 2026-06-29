using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Task A (block tile textures): parse one mod's tiles.json into resolved tile
/// content. Mirrors the model/sound loaders — the mod package loader owns discovery
/// and order and calls this per mod. Namespaces each id, resolves the relative PNG
/// path to absolute, and verifies it exists. Does not decode the image.
/// </summary>
public static class TileContentLoader
{
    const string TilesFileName = "tiles.json";

    public static List<TileContent> LoadFromMod(string modDir, string ns)
    {
        var result = new List<TileContent>();

        string path = Path.Combine(modDir, TilesFileName);
        if (!File.Exists(path)) return result;

        TilesFileData file;
        try
        {
            file = JsonUtility.FromJson<TilesFileData>(File.ReadAllText(path));
        }
        catch (Exception e)
        {
            Debug.LogError($"[TileContentLoader] Failed to parse '{ns}/{TilesFileName}': {e.Message}");
            return result;
        }

        if (file?.tiles == null)
        {
            Debug.LogWarning($"[TileContentLoader] '{ns}/{TilesFileName}' is empty or malformed.");
            return result;
        }

        foreach (var def in file.tiles)
        {
            if (def == null || string.IsNullOrWhiteSpace(def.id))
            {
                Debug.LogWarning($"[TileContentLoader] Entry in '{ns}/{TilesFileName}' has no id. Skipping.");
                continue;
            }

            string id = def.id.Contains(':') ? def.id : $"{ns}:{def.id}";

            if (string.IsNullOrWhiteSpace(def.texture))
            {
                Debug.LogError($"[TileContentLoader] '{id}' has no texture. Skipping.");
                continue;
            }

            string abs = Path.GetFullPath(Path.Combine(modDir, def.texture));
            if (!File.Exists(abs))
                Debug.LogError($"[TileContentLoader] '{id}': texture not found: '{def.texture}' (looked at '{abs}').");

            result.Add(new TileContent { Id = id, SourceMod = ns, TexturePath = abs });
        }

        Debug.Log($"[TileContentLoader] '{ns}': {result.Count} tile(s).");
        return result;
    }

    [Serializable]
    class TilesFileData
    {
        public TileDefinitionData[] tiles = Array.Empty<TileDefinitionData>();
    }
}