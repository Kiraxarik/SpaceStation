using System;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// JSON-serializable definition for one Blockbench-backed content item — a
/// character, prop (table, crate), or item (gun, tool). Authored in a mod's
/// StreamingAssets/Mods/&lt;mod&gt;/models.json (the base game ships as the "base" mod
/// folder, architecture §0.2).
///
/// This is the modder-facing path for all NON-voxel visual content. Voxel bulk
/// geometry stays in the dense byte array + greedy mesher; anything that isn't a
/// merged box wall — anything with a real authored shape — comes through here.
///
/// Example — models.json entry:
/// {
///   "id":         "oak_table",
///   "type":       "prop",
///   "geometry":   "models/oak_table.geo.json",
///   "textures":   [ "textures/oak_table.png" ],
///   "animations": []
/// }
///
/// File paths are relative to the mod folder. There is no numeric id: model
/// content is sparse, string-keyed content (architecture §0.4), referenced by its
/// stable namespaced id ("mymod:oak_table"), never by a tile byte. (A per-session
/// numeric id for compact networking, like blocks get, is a later handshake
/// optimization — not needed to author or ship the asset.)
/// </summary>
[Serializable]
public class ModelDefinitionData
{
    /// <summary>Bare id ("oak_table"); namespaced by source at load → "base:oak_table".</summary>
    public string id = "";

    /// <summary>
    /// Coarse category, drives how later consumers instantiate it:
    /// "character" (rig + animations, attaches to an entity, drives hit capsules),
    /// "prop" (placed object, static or simple-state), "item" (held in hand).
    /// A string rather than an enum so mods can introduce new kinds; consumers
    /// switch on the values they understand.
    /// </summary>
    public string type = "prop";

    /// <summary>Relative path to the Bedrock geometry file (.geo.json).</summary>
    public string geometry = "";

    /// <summary>Relative path(s) to texture file(s). Model textures are standalone — NOT the block tile atlas.</summary>
    public string[] textures = Array.Empty<string>();

    /// <summary>Relative path(s) to Bedrock animation file(s) (.animation.json). Optional.</summary>
    public string[] animations = Array.Empty<string>();

    /// <summary>Optional human-readable label for UI/tooling.</summary>
    public string display_name = "";
}

/// <summary>
/// A loaded, resolved model content item: namespaced id, source mod, and the
/// absolute on-disk paths of every file it ships. This is the unit the asset
/// pipeline consumes — Module 2 hashes AssetFiles, Module 5 ships them, the
/// content handshake agrees on Id. Holds no parsed geometry or animation data;
/// that's a later, consumer-driven step.
/// </summary>
public sealed class ModelContent
{
    public string Id;             // namespaced, e.g. "base:oak_table"
    public string Type;
    public string SourceMod;      // owning namespace / mod folder
    public string DisplayName;

    public string GeometryPath;           // absolute
    public string[] TexturePaths;         // absolute
    public string[] AnimationPaths;       // absolute

    /// <summary>
    /// Every file this content ships, absolute paths. Geometry first, then
    /// textures, then animations. This is the asset footprint Module 2 hashes
    /// and Module 5 distributes.
    /// </summary>
    public IEnumerable<string> AssetFiles
    {
        get
        {
            if (!string.IsNullOrEmpty(GeometryPath)) yield return GeometryPath;
            foreach (var t in TexturePaths) yield return t;
            foreach (var a in AnimationPaths) yield return a;
        }
    }
}