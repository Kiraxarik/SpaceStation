using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using UnityEngine;

// ── ChunkSettings ─────────────────────────────────────────────────────────────

public static class ChunkSettings
{
    public const int SIZE = 16;
    public const int VOLUME = SIZE * SIZE * SIZE;
    public const int FACE = SIZE * SIZE;

    public static int Index(int x, int y, int z)
        => x + (y * SIZE) + (z * SIZE * SIZE);

    // 2D index into a SIZE×SIZE border slice.
    //   +Y/-Y : u=x, v=z
    //   +X/-X : u=z, v=y
    //   +Z/-Z : u=x, v=y
    public static int SliceIndex(int u, int v) => u + v * SIZE;
}

// ── Block face atlas ──────────────────────────────────────────────────────────

/// <summary>
/// Six atlas tile indices, one per face.
/// Direction order: 0=+Y(top), 1=-Y(bottom), 2=+X, 3=-X, 4=+Z, 5=-Z
/// </summary>
public struct BlockFaces
{
    public int Top, Bottom, PosX, NegX, PosZ, NegZ;

    public static BlockFaces Uniform(int tile) => new BlockFaces
    { Top = tile, Bottom = tile, PosX = tile, NegX = tile, PosZ = tile, NegZ = tile };

    public static BlockFaces TopSideBottom(int top, int side, int bottom) => new BlockFaces
    { Top = top, Bottom = bottom, PosX = side, NegX = side, PosZ = side, NegZ = side };

    public static BlockFaces Custom(int top, int bottom, int posX, int negX, int posZ, int negZ) => new BlockFaces
    { Top = top, Bottom = bottom, PosX = posX, NegX = negX, PosZ = posZ, NegZ = negZ };

    public int ForDirection(int dir) => dir switch
    {
        0 => Top,
        1 => Bottom,
        2 => PosX,
        3 => NegX,
        4 => PosZ,
        5 => NegZ,
        _ => 0
    };
}

public static class BlockRegistry
{
    public static readonly BlockFaces[] Faces = new BlockFaces[]
    {
        BlockFaces.Uniform(0),                          // 0 — air
        BlockFaces.TopSideBottom(top: 0, side: 1, bottom: 2),  // 1 — floor tile
        BlockFaces.Uniform(3),                          // 2 — wall panel
    };
}

// ── ECS components ────────────────────────────────────────────────────────────

public struct ChunkPosition : IComponentData
{
    public int3 Coord;
}

[InternalBufferCapacity(0)]
public struct BlockElement : IBufferElementData
{
    public byte Value;
}

/// <summary>Tag: this chunk's mesh needs rebuilding.</summary>
public struct ChunkDirty : IComponentData { }

/// <summary>
/// Stores the current LOD tier of this chunk.
/// Set by the LOD system; read by both mesh systems.
/// </summary>
public struct ChunkLODState : IComponentData
{
    public ChunkLODLevel Level;
}

/// <summary>
/// Holds a reference to the render entity spawned for this chunk so it can be
/// destroyed cleanly when the LOD tier changes or the chunk is unloaded.
/// Entity.Null means no render entity exists right now.
/// </summary>
public struct ChunkRenderEntity : IComponentData
{
    public Entity Value;
}

/// <summary>
/// Tag: marks the local player entity so the streaming/LOD system can find it.
/// Add this to whatever entity carries the player's LocalTransform.
/// </summary>
public struct LocalPlayer : IComponentData { }

// NOTE: ChunkBlockUpdate now lives in ChunkNetworkComponents.cs as an
// IBufferElementData (so multiple same-frame changes to one chunk all survive).
// The old IComponentData version that used to be here has been removed to
// avoid a duplicate-type collision.

/// <summary>
/// Managed component: the six SIZE×SIZE border slices of neighboring chunks
/// used by BuildChunkMeshJob for cross-chunk face culling.
///
/// Defaults to all-AIR. In the placement-driven sparse model a chunk at the
/// edge of the station has genuinely empty (non-existent) neighbors, so the
/// outward-facing border faces MUST render against the void. An all-solid
/// default would hide them and the station would look sealed off. Real
/// neighbor data overwrites these slices when an adjacent chunk exists.
/// </summary>
public class ChunkNeighborSlices : IComponentData
{
    public byte[] PosY, NegY, PosX, NegX, PosZ, NegZ;

    public ChunkNeighborSlices()
    {
        PosY = Air(); NegY = Air();
        PosX = Air(); NegX = Air();
        PosZ = Air(); NegZ = Air();
    }

    // Zero = air (face is shown). A freshly allocated byte[] is already zeroed.
    static byte[] Air() => new byte[ChunkSettings.FACE];

    public byte[] ForDirection(int dir) => dir switch
    {
        0 => PosY,
        1 => NegY,
        2 => PosX,
        3 => NegX,
        4 => PosZ,
        5 => NegZ,
        _ => NegZ
    };
}

/// <summary>
/// Managed component holding the live Unity Mesh for this chunk.
/// Exists only while a render entity is active.
/// </summary>
public class ChunkMeshRef : IComponentData
{
    public Mesh Value;
}