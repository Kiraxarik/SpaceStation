using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using System.Collections.Generic;

/// <summary>
/// Drains ChunkBlockUpdate buffers each frame: applies queued block deltas to
/// each chunk's BlockElement buffer, refreshes neighbor border slices, and marks
/// affected chunks (and their neighbors) dirty for remeshing.
///
/// Runs on the main thread because it touches the managed ChunkNeighborSlices
/// component. Cheap early-out when no chunk has queued updates.
///
/// Separated from ClientChunkReceiveSystem so the receive path (entity creation)
/// and the apply path (block mutation) don't interleave structural changes.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateAfter(typeof(ClientChunkReceiveSystem))]
public partial class ChunkApplySystem : SystemBase
{
    EntityQuery _updateQuery;
    NativeHashMap<int3, Entity> _registry;       // borrowed from the singleton

    protected override void OnCreate()
    {
        _updateQuery = GetEntityQuery(ComponentType.ReadOnly<ChunkBlockUpdate>());
        RequireForUpdate<ChunkCoordRegistry>();
        RequireForUpdate(_updateQuery);
    }

    protected override void OnUpdate()
    {
        _registry = SystemAPI.GetSingleton<ChunkCoordRegistry>().Map;

        // Collect (entity, index, value) first to avoid mutating mid-iteration.
        // ushort, not byte (§1.5) — mirrors BlockElement.Value's width.
        var work = new List<(Entity entity, int index, ushort value)>();
        var dirtyChunks = new HashSet<Entity>();

        foreach (var (updates, entity) in
            SystemAPI.Query<DynamicBuffer<ChunkBlockUpdate>>().WithEntityAccess())
        {
            for (int i = 0; i < updates.Length; i++)
                work.Add((entity, updates[i].BlockIndex, updates[i].NewValue));
        }

        if (work.Count == 0) return;

        foreach (var (entity, blockIndex, newValue) in work)
        {
            if (!EntityManager.Exists(entity)) continue;
            var blocks = EntityManager.GetBuffer<BlockElement>(entity);
            if ((uint)blockIndex < (uint)blocks.Length)
                blocks[blockIndex] = new BlockElement { Value = newValue };
            dirtyChunks.Add(entity);
        }

        foreach (var entity in dirtyChunks)
        {
            if (!EntityManager.Exists(entity)) continue;
            int3 coord = EntityManager.GetComponentData<ChunkPosition>(entity).Coord;

            RefreshNeighborSlices(entity, coord);
            MarkNeighborsDirty(coord);

            if (!EntityManager.HasComponent<ChunkDirty>(entity))
                EntityManager.AddComponent<ChunkDirty>(entity);
        }

        // Clear all drained buffers.
        foreach (var updates in SystemAPI.Query<DynamicBuffer<ChunkBlockUpdate>>())
            updates.Clear();
    }

    // ── Neighbor slice management ─────────────────────────────────────────────

    void RefreshNeighborSlices(Entity entity, int3 coord)
    {
        if (!EntityManager.HasComponent<ChunkNeighborSlices>(entity)) return;
        var ns = EntityManager.GetComponentObject<ChunkNeighborSlices>(entity);
        var ourBlocks = EntityManager.GetBuffer<BlockElement>(entity)
            .AsNativeArray().Reinterpret<ushort>();

        for (int dir = 0; dir < 6; dir++)
        {
            int3 neighborCoord = coord + DirOffset(dir);
            if (!_registry.TryGetValue(neighborCoord, out Entity neighbor)) continue;
            if (!EntityManager.HasBuffer<BlockElement>(neighbor)) continue;

            var nb = EntityManager.GetBuffer<BlockElement>(neighbor)
                .AsNativeArray().Reinterpret<ushort>();

            SetSlice(ns, dir, ExtractBorderSlice(nb, dir ^ 1));

            if (EntityManager.HasComponent<ChunkNeighborSlices>(neighbor))
            {
                var nns = EntityManager.GetComponentObject<ChunkNeighborSlices>(neighbor);
                SetSlice(nns, dir ^ 1, ExtractBorderSlice(ourBlocks, dir));
            }
        }
    }

    void MarkNeighborsDirty(int3 coord)
    {
        for (int dir = 0; dir < 6; dir++)
        {
            if (!_registry.TryGetValue(coord + DirOffset(dir), out Entity neighbor)) continue;
            if (!EntityManager.HasComponent<ChunkDirty>(neighbor))
                EntityManager.AddComponent<ChunkDirty>(neighbor);
        }
    }

    static ushort[] ExtractBorderSlice(NativeArray<ushort> blocks, int dir)
    {
        int S = ChunkSettings.SIZE;
        var slice = new ushort[ChunkSettings.FACE];
        switch (dir)
        {
            case 0:
                for (int x = 0; x < S; x++) for (int z = 0; z < S; z++)
                    slice[ChunkSettings.SliceIndex(x, z)] = blocks[ChunkSettings.Index(x, S - 1, z)];
                break;
            case 1:
                for (int x = 0; x < S; x++) for (int z = 0; z < S; z++)
                    slice[ChunkSettings.SliceIndex(x, z)] = blocks[ChunkSettings.Index(x, 0, z)];
                break;
            case 2:
                for (int z = 0; z < S; z++) for (int y = 0; y < S; y++)
                    slice[ChunkSettings.SliceIndex(z, y)] = blocks[ChunkSettings.Index(S - 1, y, z)];
                break;
            case 3:
                for (int z = 0; z < S; z++) for (int y = 0; y < S; y++)
                    slice[ChunkSettings.SliceIndex(z, y)] = blocks[ChunkSettings.Index(0, y, z)];
                break;
            case 4:
                for (int x = 0; x < S; x++) for (int y = 0; y < S; y++)
                    slice[ChunkSettings.SliceIndex(x, y)] = blocks[ChunkSettings.Index(x, y, S - 1)];
                break;
            case 5:
                for (int x = 0; x < S; x++) for (int y = 0; y < S; y++)
                    slice[ChunkSettings.SliceIndex(x, y)] = blocks[ChunkSettings.Index(x, y, 0)];
                break;
        }
        return slice;
    }

    static int3 DirOffset(int dir) => dir switch
    {
        0 => new int3(0, 1, 0),
        1 => new int3(0, -1, 0),
        2 => new int3(1, 0, 0),
        3 => new int3(-1, 0, 0),
        4 => new int3(0, 0, 1),
        5 => new int3(0, 0, -1),
        _ => int3.zero
    };

    static void SetSlice(ChunkNeighborSlices ns, int dir, ushort[] s)
    {
        switch (dir)
        {
            case 0: ns.PosY = s; break;
            case 1: ns.NegY = s; break;
            case 2: ns.PosX = s; break;
            case 3: ns.NegX = s; break;
            case 4: ns.PosZ = s; break;
            case 5: ns.NegZ = s; break;
        }
    }
}