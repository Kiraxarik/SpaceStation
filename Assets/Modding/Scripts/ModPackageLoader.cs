using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// A discovered mod: its parsed manifest plus where it lives on disk. Namespace
/// equals the manifest id and is the prefix for all of the mod's content.
/// </summary>
public sealed class ModPackage
{
    public ModManifestData Manifest;
    public string Directory;   // absolute path to the mod folder
    public ModVersion Version; // parsed from Manifest.version

    public string Id => Manifest.id;
}

/// <summary>
/// The result of resolving a set of mods: a deterministic load order plus
/// diagnostics. If Ok is false, Errors explains why and Order contains only the
/// mods that resolved cleanly (unloadable mods and anything depending on them are
/// excluded). The server should refuse to start with Errors; the client surfaces
/// them through the content handshake later.
/// </summary>
public sealed class ModResolution
{
    public List<ModPackage> Order = new();
    public List<string> Errors = new();
    public List<string> Warnings = new();
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// Discovers mod packages under StreamingAssets/Mods and resolves them into a
/// deterministic load order honouring dependencies, versions, and the core API
/// version.
///
/// This load order is what makes content override precedence deterministic — it
/// replaces the filesystem enumeration order the earlier loaders relied on (the
/// determinism gap flagged back when numeric ids were introduced). A mod that
/// overrides another's content is guaranteed to load after it, because it must
/// declare the dependency.
///
/// Discovery (filesystem + JSON) and resolution (pure graph logic) are split so
/// the resolver can be exercised without Unity.
/// </summary>
public static class ModPackageLoader
{
    const string ManifestFile = "mod.json";

    // ── Top-level entry ────────────────────────────────────────────────────────

    /// <summary>Discovers every mod and resolves them into load order with diagnostics.</summary>
    public static ModResolution DiscoverAndResolve()
    {
        var packages = Discover(out var discoveryErrors);
        var resolution = Resolve(packages);
        resolution.Errors.InsertRange(0, discoveryErrors);
        LogResolution(resolution);
        return resolution;
    }

    // ── Discovery (filesystem) ─────────────────────────────────────────────────

    public static List<ModPackage> Discover(out List<string> errors)
    {
        errors = new List<string>();
        var packages = new List<ModPackage>();

        string modsRoot = Path.Combine(Application.streamingAssetsPath, "Mods");
        if (!Directory.Exists(modsRoot))
        {
            errors.Add($"No Mods folder at '{modsRoot}'.");
            return packages;
        }

        foreach (string modDir in Directory.EnumerateDirectories(modsRoot))
        {
            string manifestPath = Path.Combine(modDir, ManifestFile);
            if (!File.Exists(manifestPath))
            {
                errors.Add($"Folder '{Path.GetFileName(modDir)}' has no {ManifestFile}; skipped.");
                continue;
            }

            ModManifestData manifest;
            try
            {
                manifest = JsonUtility.FromJson<ModManifestData>(File.ReadAllText(manifestPath));
            }
            catch (Exception e)
            {
                errors.Add($"Failed to parse {Path.GetFileName(modDir)}/{ManifestFile}: {e.Message}");
                continue;
            }

            if (manifest == null || string.IsNullOrWhiteSpace(manifest.id))
            {
                errors.Add($"{Path.GetFileName(modDir)}/{ManifestFile} has no 'id'; skipped.");
                continue;
            }

            if (!ModVersion.TryParse(manifest.version, out var version))
            {
                errors.Add($"Mod '{manifest.id}' has malformed version '{manifest.version}'; treating as 0.0.0.");
                version = ModVersion.Zero;
            }

            packages.Add(new ModPackage { Manifest = manifest, Directory = modDir, Version = version });
        }

        return packages;
    }

    // ── Resolution (pure) ──────────────────────────────────────────────────────

    /// <summary>
    /// Resolves discovered packages into load order. Pure: no filesystem, no
    /// logging — returns all diagnostics in the result. Steps:
    ///   1. Reject duplicate ids and mods targeting a newer api than this core.
    ///   2. Mark mods unloadable if a required dependency is missing or its
    ///      version is unsatisfied, then cascade (dependents of unloadable mods
    ///      are themselves unloadable).
    ///   3. Topologically sort the surviving set (dependencies before dependents),
    ///      detecting cycles. Independent mods are ordered by id for determinism.
    /// </summary>
    public static ModResolution Resolve(List<ModPackage> packages)
    {
        var result = new ModResolution();

        // Index by id; reject duplicates.
        var byId = new Dictionary<string, ModPackage>(StringComparer.Ordinal);
        foreach (var p in packages)
        {
            if (byId.ContainsKey(p.Id))
            {
                result.Errors.Add($"Duplicate mod id '{p.Id}'. Ids must be unique.");
                continue;
            }
            byId[p.Id] = p;
        }

        // 1. API version gate.
        var loadable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in byId.Values)
        {
            if (p.Manifest.api_version > ModApi.Current)
            {
                result.Errors.Add(
                    $"Mod '{p.Id}' targets api_version {p.Manifest.api_version}, but this core provides {ModApi.Current}. " +
                    "Update the core or the mod.");
                continue;
            }
            loadable.Add(p.Id);
        }

        // 2. Missing / unsatisfied dependencies, then cascade.
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var p in byId.Values)
            {
                if (!loadable.Contains(p.Id)) continue;

                foreach (var dep in p.Manifest.dependencies ?? Array.Empty<ModDependencyData>())
                {
                    if (string.IsNullOrWhiteSpace(dep.id)) continue;

                    if (!byId.TryGetValue(dep.id, out var depPkg) || !loadable.Contains(dep.id))
                    {
                        result.Errors.Add($"Mod '{p.Id}' requires '{dep.id}', which is missing or failed to load.");
                        loadable.Remove(p.Id);
                        changed = true;
                        break;
                    }

                    ModVersion.TryParse(dep.min_version, out var required);
                    if (!depPkg.Version.Satisfies(required))
                    {
                        result.Errors.Add(
                            $"Mod '{p.Id}' requires '{dep.id}' >= {required} (same major), " +
                            $"but '{dep.id}' is {depPkg.Version}.");
                        loadable.Remove(p.Id);
                        changed = true;
                        break;
                    }
                }
            }
        }

        // 3. Topological sort over the loadable set (Kahn's), id-ordered for determinism.
        var ids = new List<string>(loadable);
        ids.Sort(StringComparer.Ordinal);

        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var id in ids) { inDegree[id] = 0; dependents[id] = new List<string>(); }

        foreach (var id in ids)
            foreach (var dep in byId[id].Manifest.dependencies ?? Array.Empty<ModDependencyData>())
            {
                if (string.IsNullOrWhiteSpace(dep.id) || !loadable.Contains(dep.id)) continue;
                inDegree[id]++;            // id depends on dep.id
                dependents[dep.id].Add(id); // dep.id is required by id
            }

        // Ready = no unmet deps, processed in id order for stable output.
        var ready = new List<string>();
        foreach (var id in ids) if (inDegree[id] == 0) ready.Add(id);

        while (ready.Count > 0)
        {
            ready.Sort(StringComparer.Ordinal);
            string id = ready[0];
            ready.RemoveAt(0);

            result.Order.Add(byId[id]);

            foreach (var dependent in dependents[id])
                if (--inDegree[dependent] == 0)
                    ready.Add(dependent);
        }

        // Anything left has a cycle.
        if (result.Order.Count < loadable.Count)
        {
            var inCycle = new List<string>();
            foreach (var id in ids)
                if (inDegree[id] > 0) inCycle.Add(id);
            result.Errors.Add($"Dependency cycle among mods: {string.Join(", ", inCycle)}. These will not load.");
        }

        return result;
    }

    // ── Logging ────────────────────────────────────────────────────────────────

    static void LogResolution(ModResolution r)
    {
        foreach (var w in r.Warnings) Debug.LogWarning($"[ModPackageLoader] {w}");
        foreach (var e in r.Errors) Debug.LogError($"[ModPackageLoader] {e}");

        var order = new List<string>(r.Order.Count);
        foreach (var p in r.Order) order.Add($"{p.Id} {p.Version}");
        Debug.Log($"[ModPackageLoader] Load order ({r.Order.Count}): {string.Join(" → ", order)}");
    }
}