using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

/// <summary>
/// Builds an AssetManifest from a resolved mod set by hashing each mod's shippable
/// files. Runs on both server and client; the server's result is authoritative.
///
/// Shippable files per mod = the manifest (mod.json), the content JSONs that exist
/// (blocks.json / models.json / sounds.json), and every asset file referenced by
/// that mod's model and sound content (geometry, textures, animations, clips).
/// Unreferenced files in the folder are intentionally NOT shipped, and .meta files
/// are never included — so the hash is identical between editor and build.
///
/// Hashing reads file bytes (streamed, so large audio/textures are fine). For now
/// it runs eagerly at startup; for large modpacks this could be cached to disk
/// keyed by file size+mtime, but content is small at this stage.
/// </summary>
public static class AssetManifestBuilder
{
    public static AssetManifest Build(ModResolution resolution)
    {
        var manifest = new AssetManifest();

        foreach (var pkg in resolution.Order)
            manifest.Mods.Add(BuildMod(pkg));

        return manifest;
    }

    static ModAssetEntry BuildMod(ModPackage pkg)
    {
        // Collect this mod's shippable absolute paths, deduped by relative path.
        var byRel = new SortedDictionary<string, string>(StringComparer.Ordinal); // rel → absolute

        void Add(string abs)
        {
            if (string.IsNullOrEmpty(abs) || !File.Exists(abs)) return;
            string rel = Rel(pkg.Directory, abs);
            byRel[rel] = abs; // dedup; later identical rel just overwrites with same path
        }

        // Convention files.
        Add(Path.Combine(pkg.Directory, "mod.json"));
        Add(Path.Combine(pkg.Directory, "blocks.json"));
        Add(Path.Combine(pkg.Directory, "models.json"));
        Add(Path.Combine(pkg.Directory, "sounds.json"));

        // Referenced assets for this mod's model + sound content.
        foreach (var m in ModelRegistry.All)
            if (string.Equals(m.SourceMod, pkg.Id, StringComparison.Ordinal))
                foreach (var f in m.AssetFiles) Add(f);

        foreach (var s in SoundRegistry.All)
            if (string.Equals(s.SourceMod, pkg.Id, StringComparison.Ordinal))
                foreach (var f in s.AssetFiles) Add(f);

        // Hash each (already sorted by relative path via SortedDictionary).
        var entry = new ModAssetEntry { ModId = pkg.Id, Version = pkg.Version.ToString() };

        foreach (var kvp in byRel)
        {
            try
            {
                var info = new FileInfo(kvp.Value);
                entry.Files.Add(new FileAssetEntry
                {
                    RelativePath = kvp.Key,
                    Hash = Sha256File(kvp.Value),
                    Size = info.Length,
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetManifestBuilder] '{pkg.Id}': failed to hash '{kvp.Key}': {e.Message}");
            }
        }

        entry.ModHash = RollUp(entry.Files);
        return entry;
    }

    // ── Hashing ────────────────────────────────────────────────────────────────

    static string Sha256File(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return ToHex(sha.ComputeHash(fs));
    }

    /// <summary>Rollup hash over sorted (relativePath:fileHash) lines — whole-mod identity.</summary>
    static string RollUp(List<FileAssetEntry> files)
    {
        var sb = new StringBuilder();
        foreach (var f in files)        // already in ordinal RelativePath order
            sb.Append(f.RelativePath).Append(':').Append(f.Hash).Append('\n');

        using var sha = SHA256.Create();
        return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    static string Rel(string modDir, string file)
        => Path.GetRelativePath(modDir, file).Replace('\\', '/');
}