using System;
using System.Collections.Generic;

/// <summary>
/// JSON-serializable definition for one block tile texture — a single face image
/// that the atlas baker packs into a Texture2DArray slice. Authored in a mod's
/// tiles.json. A tile is string-ID content (architecture §1.5), shipped as a PNG
/// and referenced by blocks via its namespaced id ("base:floor_side"), never by a
/// magic atlas index — which is what frees mods from coordinating tile numbers and
/// makes block visuals shippable like every other asset.
///
/// Example — tiles.json entry:
/// {
///   "id":      "floor_side",
///   "texture": "tiles/floor_side.png"
/// }
///
/// Emissive map and frame-strip animation are deliberately absent for now; they're
/// parked (emissive → power/presentation layer; frames → atlas/shader) and slot in
/// here as optional fields when their consumers land, without disturbing this shape.
/// </summary>
[Serializable]
public class TileDefinitionData
{
    /// <summary>Bare id ("floor_side"); namespaced by source → "base:floor_side".</summary>
    public string id = "";

    /// <summary>Relative path to the tile PNG. All tiles bake to a fixed slice size.</summary>
    public string texture = "";
}

/// <summary>
/// A loaded, resolved tile: namespaced id, source mod, and the absolute path of its
/// texture. The atlas baker (3b) assigns it a Texture2DArray slice; the asset
/// pipeline ships its PNG. Holds no decoded pixels.
/// </summary>
public sealed class TileContent
{
    public string Id;            // namespaced, e.g. "base:floor_side"
    public string SourceMod;
    public string TexturePath;   // absolute

    /// <summary>Every file this tile ships — the asset footprint.</summary>
    public IEnumerable<string> AssetFiles
    {
        get { if (!string.IsNullOrEmpty(TexturePath)) yield return TexturePath; }
    }
}