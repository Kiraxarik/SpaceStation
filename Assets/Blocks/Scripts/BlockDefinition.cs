using System;
using UnityEngine;

/// <summary>
/// JSON-serializable definition for one block type.
///
/// Authored in the base game's blocks.json or a mod's
/// StreamingAssets/Mods/&lt;mod&gt;/blocks.json.
///
/// Example — wall_panel entry:
/// {
///     "id":            "wall_panel",
///     "tiles":         { "all": "base:wall" },
///     "solid":         true,
///     "atmos_passable": false
/// }
///
/// Note there is NO numeric id here. Numeric (byte) ids are assigned at load by
/// the content manifest (architecture §1.5), never authored — that is what frees
/// mods from hand-coordinating id ranges. The authored "id" is namespaced by its
/// source at load ("wall_panel" → "base:wall_panel"), and that namespaced string
/// is the stable canonical identity used by saves and the manifest.
/// </summary>
[Serializable]
public class BlockDefinitionData
{
    /// <summary>
    /// Bare, unique-within-source string identifier ("wall_panel"). The loader
    /// prefixes it with the source namespace to form the canonical id
    /// ("base:wall_panel"). An id that already contains ':' is left as authored,
    /// which lets a mod deliberately target base content.
    /// </summary>
    public string id = "";

    /// <summary>Which atlas tiles to use on each face.</summary>
    public BlockTileData tiles = new BlockTileData();

    // ── Future simulation properties ───────────────────────────────────────
    // Don't affect rendering now; here so mods can define them and Phase 3
    // systems (atmospherics, power) can read them from the registry.

    /// <summary>True if this block stops movement and pathfinding.</summary>
    public bool solid = true;

    /// <summary>True if gas can flow through this block (atmos Phase 3).</summary>
    public bool atmos_passable = false;

    /// <summary>Electrical conductivity (power grid Phase 3). 0 = insulator.</summary>
    public float conductivity = 0f;
}

/// <summary>
/// Atlas tile indices for each face of a block.
///
/// Resolution order (highest priority first per face):
///   individual face field (posX, negX, posZ, negZ, top, bottom)
///     → side   (applies to all four horizontal faces)
///       → all  (applies to every face)
///         → 0  (fallback)
///
/// You only need to fill in the fields that differ from the default.
/// </summary>
[Serializable]
public class BlockTileData
{
    // Tile string ids ("base:floor_top"). Empty = unspecified, falls through the
    // resolution order below. Faces with no tile resolve to the missing slice.
    public string all = "";    // shorthand: every face uses this tile
    public string top = "";    // +Y face override
    public string bottom = ""; // -Y face override
    public string side = "";   // +X/-X/+Z/-Z override (four side faces)
    public string posX = "";   // individual face overrides
    public string negX = "";
    public string posZ = "";
    public string negZ = "";

    /// <summary>
    /// Converts this data into the BlockFaces struct used by the mesh systems.
    /// </summary>
    public BlockFaces ToBlockFaces() => new BlockFaces
    {
        Top = Resolve(top, all),
        Bottom = Resolve(bottom, all),
        PosX = Resolve(posX, side, all),
        NegX = Resolve(negX, side, all),
        PosZ = Resolve(posZ, side, all),
        NegZ = Resolve(negZ, side, all),
    };

    /// <summary>Returns the first non-empty tile id, or "" if none are set.</summary>
    static string Resolve(params string[] candidates)
    {
        foreach (string v in candidates)
            if (!string.IsNullOrEmpty(v)) return v;
        return "";
    }
}