using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Parses one mod's parts.json into resolved part content. Mirrors
/// TileContentLoader/ModelContentLoader exactly — the mod package loader owns
/// discovery and order and calls this per mod. Namespaces each id; unlike the
/// tile/model loaders there are no asset files to resolve or verify yet (parts
/// have no visual representation until the detail-mesh layer, §2.5, lands).
/// </summary>
public static class PartContentLoader
{
    const string PartsFileName = "parts.json";

    /// <summary>
    /// Loads part content from one mod folder. Returns empty if the mod has no
    /// parts.json (mods needn't define parts).
    /// </summary>
    public static List<PartContent> LoadFromMod(string modDir, string ns)
    {
        var result = new List<PartContent>();

        string path = Path.Combine(modDir, PartsFileName);
        if (!File.Exists(path)) return result;

        PartsFileData file;
        try
        {
            file = JsonUtility.FromJson<PartsFileData>(File.ReadAllText(path));
        }
        catch (Exception e)
        {
            Debug.LogError($"[PartContentLoader] Failed to parse '{ns}/{PartsFileName}': {e.Message}");
            return result;
        }

        if (file?.parts == null)
        {
            Debug.LogWarning($"[PartContentLoader] '{ns}/{PartsFileName}' is empty or malformed.");
            return result;
        }

        foreach (var def in file.parts)
        {
            if (def == null || string.IsNullOrWhiteSpace(def.id))
            {
                Debug.LogWarning($"[PartContentLoader] Entry in '{ns}/{PartsFileName}' has no id. Skipping.");
                continue;
            }

            string id = def.id.Contains(':') ? def.id : $"{ns}:{def.id}";

            result.Add(new PartContent
            {
                Id = id,
                SourceMod = ns,
                Slot = string.IsNullOrWhiteSpace(def.slot) ? "generic" : def.slot,
                Primitives = def.primitives ?? Array.Empty<string>(),
            });
        }

        Debug.Log($"[PartContentLoader] '{ns}': {result.Count} part(s).");
        return result;
    }

    // ── JSON shape ─────────────────────────────────────────────────────────────

    [Serializable]
    class PartsFileData
    {
        public PartDefinitionData[] parts = Array.Empty<PartDefinitionData>();
    }
}