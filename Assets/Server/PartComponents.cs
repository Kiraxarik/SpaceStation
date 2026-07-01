using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

/// <summary>
/// Sparse tile-entity layer — architecture §2.3/§2.4. A tile entity exists only
/// for a world-block coordinate that has at least one part installed; the plain
/// 99% case (a bare block with no parts) never gets one. Server-only: this is
/// simulation state (§0.3), never replicated as per-tile ghost data. The dense
/// block array (BlockElement, both client and server) stays pure identity and
/// is completely unaware this layer exists — ServerPartSystem is the only
/// system that reads it (to validate a target is solid) or writes here.
/// </summary>

/// <summary>World block coordinate a tile entity represents. Written once at
/// creation, never changes — a tile entity's identity IS its coordinate.</summary>
public struct TileCoord : IComponentData
{
    public int3 WorldBlock;
}

/// <summary>The composition set (§2.2) — every part installed on this tile, in
/// install order. ServerPartSystem is the only writer (§0.6, one writer).</summary>
[InternalBufferCapacity(4)]
public struct InstalledPart : IBufferElementData
{
    public ushort PartId;
}

/// <summary>
/// Server-side coord → tile-entity lookup, the sparse-layer counterpart to the
/// client's ChunkCoordRegistry. Singleton, created once by ServerPartSystem.
/// Exposed as a singleton (not a private field) because future consequence
/// systems (atmos, power) will need "does a tile exist at this neighbor coord,
/// and what primitives does it have" lookups from outside ServerPartSystem.
/// </summary>
public struct TileEntityRegistry : IComponentData
{
    public NativeHashMap<int3, Entity> Map;
}

// ── Primitive flag components (§0.2, the queryable-flag rule) ─────────────
//
// Core owns this fixed vocabulary; a future hot phase queries WithAll<GasBarrier>()
// etc. without caring which part(s) contributed it. ServerPartSystem adds/removes
// these by recomputing the union of every installed part's declared primitives
// (PartRegistry) every time a tile's InstalledPart buffer changes — see
// ServerPartSystem.RecomputePrimitives and PartPrimitives.cs (the string-name →
// ComponentType vocabulary switch). Adding a new primitive means adding both a
// struct here AND a case in PartPrimitives — a deliberate engine change, per §0.2.
//
// No behavior consumes these yet (Phase 3 hasn't started) — they exist now so the
// composition mechanism is provably correct before anything reacts to it.

/// <summary>Blocks gas flow. Will be read by the atmos diffusion phase.</summary>
public struct GasBarrier : IComponentData { }

/// <summary>Conducts electricity. Will be read by power/shock systems.</summary>
public struct Conductive : IComponentData { }