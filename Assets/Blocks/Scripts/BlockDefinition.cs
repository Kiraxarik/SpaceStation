using System;
using UnityEngine;

/// <summary>
/// JSON-serializable definition for one block type.
/// One file per block in Assets/Resources/Blocks/ (base game) or
/// StreamingAssets/Mods/YourMod/Blocks/ (mods).
///
/// Example — wall_panel.json:
/// {
///     "id":          "wall_panel",
///     "numeric_id":  2,
///     "tiles":       { "all": 3 },
///     "solid":       true,
///     "atmos_passable": false
/// }
///
/// Mods should use numeric_ids >= 128 to avoid conflicts with base game blocks.
/// </summary>
[Serializable]
public class BlockDefinitionData
{
    /// <summary>
    /// Unique string identifier. Referenced by save files and mod code.
    /// Never changes between versions — the numeric_id can be remapped but
    /// this string is the stable canonical name.
    /// </summary>
    public string id = "";

    /// <summary>
    /// Runtime byte value stored in DynamicBuffer&lt;BlockElement&gt;.
    /// 0 is always air. Max 255 (byte range).
    /// The mesh systems index BlockRegistry.Faces[] with this value.
    /// </summary>
    public int numeric_id = 0;

    /// <summary>Which atlas tiles to use on each face.</summary>
    public BlockTileData tiles = new BlockTileData();

    // ── Future simulation properties ───────────────────────────────────────
    // These don't affect rendering now but are here so mods can define them
    // and Phase 3 systems (atmospherics, power) can read them from the registry.

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
    public int all = -1;   // shorthand: every face uses this tile
    public int top = -1;   // +Y face override
    public int bottom = -1;   // -Y face override
    public int side = -1;   // +X/-X/+Z/-Z override (four side faces)
    public int posX = -1;   // individual face overrides
    public int negX = -1;
    public int posZ = -1;
    public int negZ = -1;

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

    /// <summary>Returns the first non-negative value, or 0 if all are -1.</summary>
    static int Resolve(params int[] candidates)
    {
        foreach (int v in candidates)
            if (v >= 0) return v;
        return 0;
    }
}