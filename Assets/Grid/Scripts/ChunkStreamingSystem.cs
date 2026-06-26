using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Transforms;
using UnityEngine;

/// <summary>
/// Drives all chunk visibility, LOD transitions, and block-delta application.
///
/// Each frame:
///   1. Locate the LocalPlayer entity and compute its chunk coordinate.
///   2. For every chunk, compute Chebyshev distance and derive the target LOD tier.
///   3. When the tier changes:
///        - Destroy the existing render entity (if any).
///        - If the new tier is not Unloaded, mark the chunk dirty so the
///          appropriate mesh system rebuilds it at the new detail level.
///        - If the new tier IS Unloaded, block data stays in the
///          DynamicBuffer<BlockElement> cache on the entity — nothing is destroyed.
///   4. Apply any pending ChunkBlockUpdate components (server deltas), then
///      mark those chunks dirty so their mesh (full or LOD) rebuilds.
///
/// -- NETWORK SEAM --
/// NetworkChunkSystem should:
///   a) Create chunk entities with block data when they first enter VeryFarRadius.
///   b) Add ChunkBlockUpdate components as block-change packets arrive.
///   c) When a chunk leaves VeryFarRadius entirely (not implemented here yet),
///      it can request the server to stop sending deltas for that coord.
/// </summary>
public partial class ChunkStreamingSystem : SystemBase
{
    // Cached query for the render entity lookup (structural-change safe)
    EntityQuery _playerQuery;

    protected override void OnCreate()
    {
        if (World.Name != "ClientWorld") { Enabled = false; return; }

        _playerQuery = GetEntityQuery(
            ComponentType.ReadOnly<LocalPlayer>(),
            ComponentType.ReadOnly<LocalTransform>());
    }

    protected override void OnUpdate()
    {
        // ── 1. Find player chunk coord ─────────────────────────────────────────
        if (_playerQuery.IsEmpty) return;
        if (!SystemAPI.HasSingleton<ChunkViewDistanceSettings>()) return;

        var viewDistance = SystemAPI.GetSingleton<ChunkViewDistanceSettings>();

        var playerTransform = EntityManager.GetComponentData<LocalTransform>(
            _playerQuery.GetSingletonEntity());

        int3 playerChunk = WorldPosToChunkCoord(playerTransform.Position);

        // ── 2. Collect LOD transitions ─────────────────────────────────────────
        // We gather work into lists first to avoid structural changes mid-iteration.

        var toMarkDirty = new NativeList<Entity>(Allocator.Temp);
        var toDestroyRender = new NativeList<Entity>(Allocator.Temp); // render entities

        foreach (var (pos, lodState, renderRef, entity) in
            SystemAPI
                .Query<RefRO<ChunkPosition>,
                       RefRW<ChunkLODState>,
                       RefRW<ChunkRenderEntity>>()
                .WithEntityAccess())
        {
            ChunkLODLevel target = TargetLOD(pos.ValueRO.Coord, playerChunk, viewDistance);
            ChunkLODLevel current = lodState.ValueRO.Level;

            if (target == current) continue;

            // Destroy existing render entity if one exists
            Entity existingRender = renderRef.ValueRO.Value;
            if (existingRender != Entity.Null)
                toDestroyRender.Add(existingRender);

            // Update state
            lodState.ValueRW.Level = target;
            renderRef.ValueRW.Value = Entity.Null;

            // Schedule mesh rebuild unless unloading
            if (target != ChunkLODLevel.Unloaded)
                toMarkDirty.Add(entity);
        }

        // ── 3. Apply structural changes ────────────────────────────────────────
        foreach (var re in toDestroyRender)
            if (EntityManager.Exists(re))
                EntityManager.DestroyEntity(re);

        foreach (var e in toMarkDirty)
            if (!EntityManager.HasComponent<ChunkDirty>(e))
                EntityManager.AddComponent<ChunkDirty>(e);

        toDestroyRender.Dispose();
        toMarkDirty.Dispose();

        // ── 4. Apply block-change deltas ───────────────────────────────────────
        // Collect first to avoid modifying buffers mid-query.
        var deltaWork = new System.Collections.Generic.List<(Entity entity, int idx, byte val)>();

        foreach (var (update, entity) in
            SystemAPI.Query<RefRO<ChunkBlockUpdate>>().WithEntityAccess())
        {
            deltaWork.Add((entity, update.ValueRO.BlockIndex, update.ValueRO.NewValue));
        }

        foreach (var (entity, blockIndex, newValue) in deltaWork)
        {
            if (!EntityManager.Exists(entity)) continue;

            // Apply to cached block buffer
            var blocks = EntityManager.GetBuffer<BlockElement>(entity);
            if (blockIndex >= 0 && blockIndex < blocks.Length)
                blocks[blockIndex] = new BlockElement { Value = newValue };

            // Update the border slice on all 6 neighbors so their meshes
            // also rebuild with correct cross-chunk culling.
            // -- NETWORK SEAM --
            // If this becomes a hot path, batch slice refreshes rather than
            // doing them per-delta. For now correctness > perf.
            RefreshNeighborSlicesFor(entity);

            // Mark this chunk and its neighbors dirty
            EntityManager.RemoveComponent<ChunkBlockUpdate>(entity);
            if (!EntityManager.HasComponent<ChunkDirty>(entity))
                EntityManager.AddComponent<ChunkDirty>(entity);

            MarkNeighborsDirty(entity);
        }
    }

    // ── LOD tier selection ────────────────────────────────────────────────────

    static ChunkLODLevel TargetLOD(int3 chunkCoord, int3 playerChunk, ChunkViewDistanceSettings settings)
    {
        // Chebyshev distance: max of per-axis distances
        int3 diff = chunkCoord - playerChunk;
        // Ignore Y for a flat-station layout: only X/Z distance matters
        int dist = math.max(math.abs(diff.x), math.abs(diff.z));

        if (dist <= settings.FullDetailRadius) return ChunkLODLevel.Full;
        if (dist <= settings.MediumLODRadius) return ChunkLODLevel.Medium;
        if (dist <= settings.FarLODRadius) return ChunkLODLevel.Far;
        if (dist <= settings.VeryFarRadius) return ChunkLODLevel.VeryFar;
        return ChunkLODLevel.Unloaded;
    }

    // ── Neighbor slice + dirty helpers ────────────────────────────────────────

    // Six neighbor offsets matching face direction indices (0=+Y … 5=-Z)
    static readonly int3[] NeighborOffsets = new int3[]
    {
        new int3( 0,  1,  0), new int3( 0, -1,  0),
        new int3( 1,  0,  0), new int3(-1,  0,  0),
        new int3( 0,  0,  1), new int3( 0,  0, -1),
    };

    void MarkNeighborsDirty(Entity chunkEntity)
    {
        if (!EntityManager.HasComponent<ChunkPosition>(chunkEntity)) return;
        int3 coord = EntityManager.GetComponentData<ChunkPosition>(chunkEntity).Coord;

        foreach (var (pos, entity) in
            SystemAPI.Query<RefRO<ChunkPosition>>().WithEntityAccess())
        {
            int3 diff = pos.ValueRO.Coord - coord;
            // Is this entity one of the 6 face-adjacent neighbors?
            bool isNeighbor =
                (math.abs(diff.x) + math.abs(diff.y) + math.abs(diff.z)) == 1;

            if (isNeighbor && !EntityManager.HasComponent<ChunkDirty>(entity))
                EntityManager.AddComponent<ChunkDirty>(entity);
        }
    }

    void RefreshNeighborSlicesFor(Entity chunkEntity)
    {
        if (!EntityManager.HasComponent<ChunkPosition>(chunkEntity)) return;
        int3 coord = EntityManager.GetComponentData<ChunkPosition>(chunkEntity).Coord;

        if (!EntityManager.HasComponent<ChunkNeighborSlices>(chunkEntity)) return;
        var ns = EntityManager.GetComponentObject<ChunkNeighborSlices>(chunkEntity);

        // For each of the 6 neighbor directions, if the neighbor exists,
        // re-extract its border slice facing this chunk.
        foreach (var (pos, entity) in
            SystemAPI.Query<RefRO<ChunkPosition>>().WithEntityAccess())
        {
            int3 diff = pos.ValueRO.Coord - coord;
            int faceDir = DiffToFaceDir(diff);
            if (faceDir < 0) continue; // not an axis-aligned neighbor

            if (!EntityManager.HasBuffer<BlockElement>(entity)) continue;

            var neighborBlocks = EntityManager
                .GetBuffer<BlockElement>(entity)
                .AsNativeArray()
                .Reinterpret<byte>();

            byte[] slice = ChunkSpawnSystem.ExtractBorderSlice(neighborBlocks, OppositeDir(faceDir));
            SetSlice(ns, faceDir, slice);
        }
    }

    static int DiffToFaceDir(int3 diff)
    {
        if (diff.Equals(new int3(0, 1, 0))) return 0;
        if (diff.Equals(new int3(0, -1, 0))) return 1;
        if (diff.Equals(new int3(1, 0, 0))) return 2;
        if (diff.Equals(new int3(-1, 0, 0))) return 3;
        if (diff.Equals(new int3(0, 0, 1))) return 4;
        if (diff.Equals(new int3(0, 0, -1))) return 5;
        return -1;
    }

    static int OppositeDir(int dir) => dir ^ 1;

    static void SetSlice(ChunkNeighborSlices ns, int dir, byte[] slice)
    {
        switch (dir)
        {
            case 0: ns.PosY = slice; break;
            case 1: ns.NegY = slice; break;
            case 2: ns.PosX = slice; break;
            case 3: ns.NegX = slice; break;
            case 4: ns.PosZ = slice; break;
            case 5: ns.NegZ = slice; break;
        }
    }

    // ── Utility ───────────────────────────────────────────────────────────────

    static int3 WorldPosToChunkCoord(float3 worldPos)
        => new int3(
            (int)math.floor(worldPos.x / ChunkSettings.SIZE),
            (int)math.floor(worldPos.y / ChunkSettings.SIZE),
            (int)math.floor(worldPos.z / ChunkSettings.SIZE));
}