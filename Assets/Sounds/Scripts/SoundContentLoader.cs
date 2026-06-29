using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Task A (sparse asset layer): parse one mod's sounds.json into resolved sound
/// content descriptors. Mirrors ModelContentLoader — the mod package loader owns
/// discovery and order and calls this per mod.
///
/// Namespaces each id by the mod's canonical id, resolves relative clip paths to
/// absolute, and verifies each clip exists (a missing clip would break distribution,
/// so it's logged loudly). Does not decode audio.
/// </summary>
public static class SoundContentLoader
{
    const string SoundsFileName = "sounds.json";

    /// <summary>
    /// Loads sound content from one mod folder. Returns empty if the mod has no
    /// sounds.json (mods needn't define sounds).
    /// </summary>
    public static List<SoundContent> LoadFromMod(string modDir, string ns)
    {
        var result = new List<SoundContent>();

        string path = Path.Combine(modDir, SoundsFileName);
        if (!File.Exists(path)) return result;

        SoundsFileData file;
        try
        {
            file = JsonUtility.FromJson<SoundsFileData>(File.ReadAllText(path));
        }
        catch (Exception e)
        {
            Debug.LogError($"[SoundContentLoader] Failed to parse '{ns}/{SoundsFileName}': {e.Message}");
            return result;
        }

        if (file?.sounds == null)
        {
            Debug.LogWarning($"[SoundContentLoader] '{ns}/{SoundsFileName}' is empty or malformed.");
            return result;
        }

        foreach (var def in file.sounds)
        {
            if (def == null || string.IsNullOrWhiteSpace(def.id))
            {
                Debug.LogWarning($"[SoundContentLoader] Entry in '{ns}/{SoundsFileName}' has no id. Skipping.");
                continue;
            }

            string id = def.id.Contains(':') ? def.id : $"{ns}:{def.id}";

            if (def.clips == null || def.clips.Length == 0)
            {
                Debug.LogError($"[SoundContentLoader] '{id}' has no clips. Skipping.");
                continue;
            }

            result.Add(new SoundContent
            {
                Id = id,
                SourceMod = ns,
                ClipPaths = ResolveMany(modDir, def.clips, id),
                Volume = def.volume,
                PitchMin = def.pitch_min,
                PitchMax = def.pitch_max,
                Loop = def.loop,
                Spatial = def.spatial,
                MinDistance = def.min_distance,
                MaxDistance = def.max_distance,
            });
        }

        Debug.Log($"[SoundContentLoader] '{ns}': {result.Count} sound(s).");
        return result;
    }

    // ── Path resolution + existence check ──────────────────────────────────────

    static string[] ResolveMany(string modDir, string[] relatives, string id)
    {
        var abs = new string[relatives.Length];
        for (int i = 0; i < relatives.Length; i++)
        {
            string rel = relatives[i];
            if (string.IsNullOrWhiteSpace(rel)) { abs[i] = ""; continue; }

            string full = Path.GetFullPath(Path.Combine(modDir, rel));
            if (!File.Exists(full))
                Debug.LogError($"[SoundContentLoader] '{id}': clip file not found: '{rel}' (looked at '{full}').");
            abs[i] = full;
        }
        return abs;
    }

    // ── JSON shape ─────────────────────────────────────────────────────────────

    [Serializable]
    class SoundsFileData
    {
        public SoundDefinitionData[] sounds = Array.Empty<SoundDefinitionData>();
    }
}