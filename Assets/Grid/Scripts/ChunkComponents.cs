using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using UnityEngine;

// ── ChunkSettings ─────────────────────────────────────────────────────────────

public static class ChunkSettings
{
    public const int SIZE = 16;

    /// <summary>Block count per chunk. NOT the same as wire byte size any more —
    /// BlockElement.Value is ushort (§1.5), so raw wire transfer is BYTE_SIZE.</summary>
    public const int VOLUME = SIZE * SIZE * SIZE;
    public const int FACE = SIZE * SIZE;

    /// <summary>Raw byte size of one chunk's block buffer on the wire —
    /// VOLUME ushorts, little-endian pairs. Use this (not VOLUME) anywhere sizing
    /// a byte[] for chunk transfer/fragmentation; VOLUME alone under-sizes it by
    /// half now that blocks are ushort, not byte.</summary>
    public const int BYTE_SIZE = VOLUME * sizeof(ushort);

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
///
/// Populated at runtime from BlockDefinitionData.tiles by BlockRegistry.
/// The mesh systems read BlockRegistry.Faces[] which is an array of these.
/// </summary>
public struct BlockFaces
{
    // Six per-face tile string ids (e.g. "base:floor_top"). Resolved to
    // Texture2DArray slice indices by TileAtlasBaker on the client.
    public string Top, Bottom, PosX, NegX, PosZ, NegZ;

    public static BlockFaces Uniform(string tile) => new BlockFaces
    { Top = tile, Bottom = tile, PosX = tile, NegX = tile, PosZ = tile, NegZ = tile };

    public static BlockFaces TopSideBottom(string top, string side, string bottom) => new BlockFaces
    { Top = top, Bottom = bottom, PosX = side, NegX = side, PosZ = side, NegZ = side };

    public static BlockFaces Custom(string top, string bottom, string posX, string negX, string posZ, string negZ) => new BlockFaces
    { Top = top, Bottom = bottom, PosX = posX, NegX = negX, PosZ = posZ, NegZ = negZ };

    public string ForDirection(int dir) => dir switch
    {
        0 => Top,
        1 => Bottom,
        2 => PosX,
        3 => NegX,
        4 => PosZ,
        5 => NegZ,
        _ => ""
    };
}

// Note: BlockRegistry has moved to BlockRegistry.cs and is now loaded at
// runtime from JSON files in Assets/Resources/Blocks/ and mod folders.
// BlockRegistry.Faces[] has the same shape and is available before any
// ECS system runs (initialized via [RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]).

// ── ECS components ────────────────────────────────────────────────────────────

public struct ChunkPosition : IComponentData
{
    public int3 Coord;
}

/// <summary>
/// Dense per-block identity. ushort, not byte (§1.5) — a byte capped block
/// types at 256, which a single block already burns through fast since it can
/// reference up to 6 distinct tile ids and mods stack on top of the base game.
/// ushort gives 65,536 identities, effectively uncapped. Doubles the dense
/// buffer's memory footprint (4KB → 8KB per chunk) — trivial.
/// </summary>
[InternalBufferCapacity(0)]
public struct BlockElement : IBufferElementData
{
    public ushort Value;
}

/// <summary>Tag: this chunk's mesh needs rebuilding.</summary>
public struct ChunkDirty : IComponentData { }

/// <summary>Current LOD tier. Set by ChunkLODSystem; read by mesh systems.</summary>
public struct ChunkLODState : IComponentData
{
    public ChunkLODLevel Level;
}

/// <summary>
/// Reference to the render entity spawned for this chunk.
/// Entity.Null means no render entity currently exists.
/// Written by mesh systems; read and cleared by ChunkLODSystem.
/// </summary>
public struct ChunkRenderEntity : IComponentData
{
    public Entity Value;
}

/// <summary>
/// Tag: marks the locally-owned player entity so LOD and other systems
/// can find it without querying all player ghosts.
/// Added by LocalPlayerTagSystem when GhostOwnerIsLocal is present.
/// </summary>
public struct LocalPlayer : IComponentData { }

/// <summary>
/// Managed component: the six SIZE×SIZE border slices of neighbouring chunks
/// used by BuildChunkMeshJob for cross-chunk face culling.
///
/// Defaults to all-AIR so that station edges facing empty space correctly
/// show their outward faces. Previously all-solid, which was right when every
/// chunk was guaranteed a neighbour — now the world is sparse.
///
/// ushort, not byte (§1.5) — mirrors BlockElement.Value's width so a border
/// slice can hold any block id in the full ushort range.
/// </summary>
public class ChunkNeighborSlices : IComponentData
{
    public ushort[] PosY, NegY, PosX, NegX, PosZ, NegZ;

    public ChunkNeighborSlices()
    {
        PosY = Air(); NegY = Air();
        PosX = Air(); NegX = Air();
        PosZ = Air(); NegZ = Air();
    }

    // All zeroes = air = face is visible against this neighbour.
    static ushort[] Air() => new ushort[ChunkSettings.FACE];

    public ushort[] ForDirection(int dir) => dir switch
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