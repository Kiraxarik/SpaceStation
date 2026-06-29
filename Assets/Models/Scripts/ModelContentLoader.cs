using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Discovery + parse + path-resolution for model content (the Task-A analog of
/// BlockContentLoader, for the sparse asset layer instead of the dense tile layer).
///
/// Scans every StreamingAssets/Mods/&lt;mod&gt;/models.json — including the base game,
/// which ships as StreamingAssets/Mods/base/. Keeping base content as real files
/// under the same root as mods means the asset pipeline (hashing, distribution)
/// has ONE enumeration path, not a separate case for built-in content.
///
/// For each entry it namespaces the id by source folder, resolves the relative
/// file references to absolute paths, and verifies the files exist (a missing
/// referenced asset is a content error that would break distribution, so it's
/// logged loudly). It does NOT open or parse the geometry/animation files — only
/// the descriptors are produced here.
/// </summary>
public static class ModelContentLoader
{
    const string ModelsFile = "models.json";

    /// <summary>Loads every model content item from every mod folder.</summary>
    public static List<ModelContent> LoadAll()
    {
        var result = new List<ModelContent>();

        string modsRoot = Path.Combine(Application.streamingAssetsPath, "Mods");
        if (!Directory.Exists(modsRoot))
        {
            Debug.LogWarning($"[ModelContentLoader] No Mods folder at '{modsRoot}'. No model content loaded.");
            return result;
        }

        foreach (string modDir in Directory.EnumerateDirectories(modsRoot))
        {
            string modName = Path.GetFileName(modDir);
            string ns = Sanitize(modName);
            string modelsPath = Path.Combine(modDir, ModelsFile);
            if (!File.Exists(modelsPath)) continue;

            try
            {
                var parsed = ParseFile(File.ReadAllText(modelsPath), $"{modName}/{ModelsFile}", ns, modDir);
                result.AddRange(parsed);
                Debug.Log($"[ModelContentLoader] Mod '{modName}' (ns '{ns}'): {parsed.Count} model(s).");
            }
            catch (Exception e)
            {
                Debug.LogError($"[ModelContentLoader] Failed to load models for '{modName}': {e.Message}");
            }
        }

        return result;
    }

    // ── Parse one models.json ──────────────────────────────────────────────────

    static List<ModelContent> ParseFile(string json, string sourceName, string ns, string modDir)
    {
        var result = new List<ModelContent>();

        ModelsFileData file;
        try
        {
            file = JsonUtility.FromJson<ModelsFileData>(json);
        }
        catch (Exception e)
        {
            Debug.LogError($"[ModelContentLoader] Failed to parse '{sourceName}': {e.Message}");
            return result;
        }

        if (file?.models == null)
        {
            Debug.LogWarning($"[ModelContentLoader] '{sourceName}' is empty or malformed.");
            return result;
        }

        foreach (var def in file.models)
        {
            if (def == null || string.IsNullOrWhiteSpace(def.id))
            {
                Debug.LogWarning($"[ModelContentLoader] Entry in '{sourceName}' has no id. Skipping.");
                continue;
            }

            string id = def.id.Contains(':') ? def.id : $"{ns}:{def.id}";

            if (string.IsNullOrWhiteSpace(def.geometry))
            {
                Debug.LogError($"[ModelContentLoader] '{id}' has no geometry file. Skipping.");
                continue;
            }

            var content = new ModelContent
            {
                Id = id,
                Type = string.IsNullOrWhiteSpace(def.type) ? "prop" : def.type,
                SourceMod = ns,
                DisplayName = def.display_name ?? "",
                GeometryPath = Resolve(modDir, def.geometry, id, "geometry"),
                TexturePaths = ResolveMany(modDir, def.textures, id, "texture"),
                AnimationPaths = ResolveMany(modDir, def.animations, id, "animation"),
            };

            result.Add(content);
        }

        return result;
    }

    // ── Path resolution + existence check ──────────────────────────────────────

    static string Resolve(string modDir, string relative, string id, string kind)
    {
        if (string.IsNullOrWhiteSpace(relative)) return "";
        string abs = Path.GetFullPath(Path.Combine(modDir, relative));
        if (!File.Exists(abs))
            Debug.LogError($"[ModelContentLoader] '{id}': {kind} file not found: '{relative}' (looked at '{abs}').");
        return abs;
    }

    static string[] ResolveMany(string modDir, string[] relatives, string id, string kind)
    {
        if (relatives == null || relatives.Length == 0) return Array.Empty<string>();
        var abs = new string[relatives.Length];
        for (int i = 0; i < relatives.Length; i++)
            abs[i] = Resolve(modDir, relatives[i], id, kind);
        return abs;
    }

    static string Sanitize(string raw)
    {
        var chars = raw.Trim().ToLowerInvariant().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (char.IsWhiteSpace(chars[i]) || chars[i] == ':') chars[i] = '_';
        return new string(chars);
    }

    // ── JSON shape ─────────────────────────────────────────────────────────────

    [Serializable]
    class ModelsFileData
    {
        public ModelDefinitionData[] models = Array.Empty<ModelDefinitionData>();
    }
}