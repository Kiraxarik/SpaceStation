using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Task A (sparse asset layer): parse one mod's models.json into resolved model
/// content descriptors. Like BlockContentLoader, it parses a single mod folder on
/// command — the mod package loader owns discovery and order.
///
/// For each entry it namespaces the id by the mod's canonical id, resolves the
/// relative file references to absolute paths, and verifies the files exist (a
/// missing referenced asset is a content error that would break distribution, so
/// it's logged loudly). It does not open or parse geometry/animation files.
/// </summary>
public static class ModelContentLoader
{
    const string ModelsFileName = "models.json";

    /// <summary>
    /// Loads model content from one mod folder. Returns empty if the mod has no
    /// models.json (mods needn't define models).
    /// </summary>
    public static List<ModelContent> LoadFromMod(string modDir, string ns)
    {
        var result = new List<ModelContent>();

        string path = Path.Combine(modDir, ModelsFileName);
        if (!File.Exists(path)) return result;

        ModelsFileData file;
        try
        {
            file = JsonUtility.FromJson<ModelsFileData>(File.ReadAllText(path));
        }
        catch (Exception e)
        {
            Debug.LogError($"[ModelContentLoader] Failed to parse '{ns}/{ModelsFileName}': {e.Message}");
            return result;
        }

        if (file?.models == null)
        {
            Debug.LogWarning($"[ModelContentLoader] '{ns}/{ModelsFileName}' is empty or malformed.");
            return result;
        }

        foreach (var def in file.models)
        {
            if (def == null || string.IsNullOrWhiteSpace(def.id))
            {
                Debug.LogWarning($"[ModelContentLoader] Entry in '{ns}/{ModelsFileName}' has no id. Skipping.");
                continue;
            }

            string id = def.id.Contains(':') ? def.id : $"{ns}:{def.id}";

            if (string.IsNullOrWhiteSpace(def.geometry))
            {
                Debug.LogError($"[ModelContentLoader] '{id}' has no geometry file. Skipping.");
                continue;
            }

            result.Add(new ModelContent
            {
                Id = id,
                Type = string.IsNullOrWhiteSpace(def.type) ? "prop" : def.type,
                SourceMod = ns,
                DisplayName = def.display_name ?? "",
                GeometryPath = Resolve(modDir, def.geometry, id, "geometry"),
                TexturePaths = ResolveMany(modDir, def.textures, id, "texture"),
                AnimationPaths = ResolveMany(modDir, def.animations, id, "animation"),
            });
        }

        Debug.Log($"[ModelContentLoader] '{ns}': {result.Count} model(s).");
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

    // ── JSON shape ─────────────────────────────────────────────────────────────

    [Serializable]
    class ModelsFileData
    {
        public ModelDefinitionData[] models = Array.Empty<ModelDefinitionData>();
    }
}