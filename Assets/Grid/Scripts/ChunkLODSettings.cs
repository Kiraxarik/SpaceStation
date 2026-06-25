// ── ChunkLODSettings.cs ───────────────────────────────────────────────────────
// Central config for all streaming and LOD constants.
// Edit these values to tune view distances and mesh quality.

public static class ChunkLODSettings
{
    // ── Radii (in chunks, Chebyshev distance) ─────────────────────────────────
    // A chunk at distance <= FullDetailRadius gets a full greedy mesh.
    // Beyond that it falls into whichever LOD tier it lands in.
    // Beyond VeryFarRadius it is unloaded (render destroyed, block data cached).

    public const int FullDetailRadius = 8;
    public const int MediumLODRadius = 16;
    public const int FarLODRadius = 32;
    public const int VeryFarRadius = 64;

    // ── LOD downsample factors ────────────────────────────────────────────────
    // MediumFactor=2 means every 2x2 column of blocks merges into one sample.
    // Must be a power of two and <= ChunkSettings.SIZE.

    public const int MediumFactor = 2;
    public const int FarFactor = 4;
    public const int VeryFarFactor = 8;
}

/// <summary>
/// Which rendering tier a chunk is currently in.
/// Ordered from most to least detail so integer comparisons work naturally.
/// </summary>
public enum ChunkLODLevel : byte
{
    Full = 0,   // full greedy mesh
    Medium = 1,   // downsampled 2x
    Far = 2,   // downsampled 4x
    VeryFar = 3,   // downsampled 8x
    Unloaded = 4,  // render destroyed, block data cached
}