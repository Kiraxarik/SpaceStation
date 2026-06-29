using System;

/// <summary>
/// mod.json — the identity manifest every mod folder carries, including the base
/// game (which is mod "base", architecture §0.2). A mod is no longer "a folder
/// that happens to contain blocks.json"; it's a folder with a mod.json that
/// declares who it is, what core API it targets, and what it depends on.
///
/// Content files (blocks.json, models.json, textures/, models/, scripts/) are
/// still discovered by convention inside the folder. The manifest owns identity,
/// versioning, and dependencies — the things the asset pipeline hashes mods by
/// and the things that determine a deterministic load order.
///
/// Example — StreamingAssets/Mods/mymod/mod.json:
/// {
///   "id":          "mymod",
///   "name":        "My Mod",
///   "version":     "1.2.0",
///   "author":      "someone",
///   "description": "adds reactors",
///   "api_version": 1,
///   "dependencies": [ { "id": "base", "min_version": "1.0.0" } ]
/// }
/// </summary>
[Serializable]
public class ModManifestData
{
    /// <summary>
    /// Canonical mod id — this IS the content namespace prefix ("mymod:reactor").
    /// Lowercase, no spaces or ':'. Must be unique across loaded mods.
    /// </summary>
    public string id = "";

    public string name = "";
    public string version = "0.0.0";
    public string author = "";
    public string description = "";

    /// <summary>
    /// Which core modding API this mod was built against. The core advertises
    /// ModApi.Current; a mod targeting a NEWER api than the core provides is
    /// rejected (it needs features this build doesn't have).
    /// </summary>
    public int api_version = 1;

    public ModDependencyData[] dependencies = Array.Empty<ModDependencyData>();
}

/// <summary>One declared dependency: another mod that must load first.</summary>
[Serializable]
public class ModDependencyData
{
    public string id = "";

    /// <summary>
    /// Minimum acceptable version of the dependency ("1.0.0"). Empty means "any
    /// version". Satisfaction uses semver compatibility — see ModVersion.Satisfies.
    /// </summary>
    public string min_version = "";
}

/// <summary>The core modding API version this build advertises.</summary>
public static class ModApi
{
    /// <summary>Bumped when a breaking change is made to the modding interface.</summary>
    public const int Current = 1;
}

/// <summary>
/// A parsed semantic version (major.minor.patch) with comparison. Kept tiny and
/// engine-free so the resolver can be unit-tested. Missing components default to
/// 0 ("1.2" → 1.2.0, "1" → 1.0.0).
/// </summary>
public readonly struct ModVersion : IComparable<ModVersion>, IEquatable<ModVersion>
{
    public readonly int Major, Minor, Patch;

    public ModVersion(int major, int minor, int patch)
    {
        Major = major; Minor = minor; Patch = patch;
    }

    public static readonly ModVersion Zero = new ModVersion(0, 0, 0);

    /// <summary>Parses "x", "x.y", or "x.y.z". Returns false (and Zero) on malformed input.</summary>
    public static bool TryParse(string s, out ModVersion version)
    {
        version = Zero;
        if (string.IsNullOrWhiteSpace(s)) return false;

        string[] parts = s.Trim().Split('.');
        if (parts.Length == 0 || parts.Length > 3) return false;

        int major = 0, minor = 0, patch = 0;
        if (!int.TryParse(parts[0], out major)) return false;
        if (parts.Length >= 2 && !int.TryParse(parts[1], out minor)) return false;
        if (parts.Length >= 3 && !int.TryParse(parts[2], out patch)) return false;
        if (major < 0 || minor < 0 || patch < 0) return false;

        version = new ModVersion(major, minor, patch);
        return true;
    }

    /// <summary>
    /// Whether this installed version satisfies a required minimum under semver
    /// rules: same major (a major bump is a breaking change) AND not older than
    /// the requirement. A Zero requirement ("any version") is always satisfied.
    /// </summary>
    public bool Satisfies(ModVersion required)
    {
        if (required.Equals(Zero)) return true;
        return Major == required.Major && CompareTo(required) >= 0;
    }

    public int CompareTo(ModVersion other)
    {
        if (Major != other.Major) return Major.CompareTo(other.Major);
        if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
        return Patch.CompareTo(other.Patch);
    }

    public bool Equals(ModVersion other) => Major == other.Major && Minor == other.Minor && Patch == other.Patch;
    public override bool Equals(object obj) => obj is ModVersion v && Equals(v);
    public override int GetHashCode() => (Major, Minor, Patch).GetHashCode();
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}