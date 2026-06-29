using System;
using System.Collections.Generic;

/// <summary>
/// The hash manifest of all shippable content, per mod. Distinct from
/// ContentManifest (the byte-tile identity ordering) — this is the asset
/// integrity / distribution side (architecture §1.5, §7.4: content-hash verify).
///
/// The server builds the authoritative manifest; the client builds its own; the
/// handshake diffs them to find what the client must download. Per-file hashes
/// make the fetch a precise delta (content-addressable); the per-mod rollup lets a
/// fully-matching mod be skipped without comparing every file.
/// </summary>
[Serializable]
public class AssetManifest
{
    public List<ModAssetEntry> Mods = new();

    public ModAssetEntry FindMod(string modId)
    {
        foreach (var m in Mods)
            if (string.Equals(m.ModId, modId, StringComparison.Ordinal)) return m;
        return null;
    }

    // ── Diff (pure) ────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes what <paramref name="client"/> must fetch to match
    /// <paramref name="server"/>. For each server mod, a matching rollup short-
    /// circuits the whole mod; otherwise files are compared individually and any
    /// the client lacks or has with a different hash are added to Needed.
    /// </summary>
    public static AssetDiff Diff(AssetManifest server, AssetManifest client)
    {
        var diff = new AssetDiff();

        foreach (var sMod in server.Mods)
        {
            var cMod = client?.FindMod(sMod.ModId);

            // Whole-mod match: skip.
            if (cMod != null && string.Equals(cMod.ModHash, sMod.ModHash, StringComparison.Ordinal))
                continue;

            // Index client's files for this mod by relative path.
            var clientByPath = new Dictionary<string, FileAssetEntry>(StringComparer.Ordinal);
            if (cMod != null)
                foreach (var f in cMod.Files)
                    clientByPath[f.RelativePath] = f;

            foreach (var sFile in sMod.Files)
            {
                bool have = clientByPath.TryGetValue(sFile.RelativePath, out var cFile)
                            && string.Equals(cFile.Hash, sFile.Hash, StringComparison.Ordinal);
                if (!have)
                    diff.Needed.Add(new NeededFile
                    {
                        ModId = sMod.ModId,
                        RelativePath = sFile.RelativePath,
                        Hash = sFile.Hash,
                        Size = sFile.Size,
                    });
            }
        }

        // Mods the client has that the server doesn't define this session.
        if (client != null)
            foreach (var cMod in client.Mods)
                if (server.FindMod(cMod.ModId) == null)
                    diff.ClientExtraMods.Add(cMod.ModId);

        diff.Identical = diff.Needed.Count == 0 && diff.ClientExtraMods.Count == 0;
        return diff;
    }
}

[Serializable]
public class ModAssetEntry
{
    public string ModId;
    public string Version;

    /// <summary>SHA-256 over the mod's (relativePath:fileHash) lines, sorted. Whole-mod identity.</summary>
    public string ModHash;

    public List<FileAssetEntry> Files = new();
}

[Serializable]
public class FileAssetEntry
{
    /// <summary>Path relative to the mod folder, forward-slashed ("models/debug_cube.geo.json").</summary>
    public string RelativePath;

    /// <summary>SHA-256 hex of the file bytes.</summary>
    public string Hash;

    /// <summary>Byte length — used for download progress/budgeting.</summary>
    public long Size;
}

// ── Diff result ────────────────────────────────────────────────────────────────

public sealed class AssetDiff
{
    /// <summary>Files the client must fetch from the server (missing or hash-mismatched).</summary>
    public List<NeededFile> Needed = new();

    /// <summary>Mods the client has that the server doesn't define this session (informational).</summary>
    public List<string> ClientExtraMods = new();

    public bool Identical;

    public long TotalBytes
    {
        get { long t = 0; foreach (var n in Needed) t += n.Size; return t; }
    }
}

public struct NeededFile
{
    public string ModId;
    public string RelativePath;
    public string Hash;
    public long Size;
}