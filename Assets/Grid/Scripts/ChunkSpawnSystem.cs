using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;

/// <summary>
/// Spawns the initial chunk grid and wires up neighbor slices.
///
/// -- NETWORK SEAM --
/// Replace the OnCreate loop with calls to SpawnChunk per coord received
/// from the server. SpawnChunk is an instance method so it has access to
/// ref SystemState — call it from OnUpdate when server data arrives.
/// </summary>
public partial struct ChunkSpawnSystem : ISystem
{
    const int WORLD_SIZE = 100;

    public void OnCreate(ref SystemState state)
    {
        if (state.World.Name != "ClientWorld") { state.Enabled = false; return; }

        for (int x = 0; x < WORLD_SIZE; x++)
            for (int z = 0; z < WORLD_SIZE; z++)
                SpawnChunk(ref state, new int3(x, 0, z));

        RefreshAllNeighborSlices(ref state);
    }

    // ── Chunk creation ────────────────────────────────────────────────────────

    public void SpawnChunk(ref SystemState state, int3 coord)
    {
        Entity e = state.EntityManager.CreateEntity();

        state.EntityManager.AddComponentData(e, new ChunkPosition { Coord = coord });

        var blocks = state.EntityManager.AddBuffer<BlockElement>(e);
        blocks.Resize(ChunkSettings.VOLUME, NativeArrayOptions.ClearMemory);

        // Solid floor at y=0
        for (int x = 0; x < ChunkSettings.SIZE; x++)
            for (int z = 0; z < ChunkSettings.SIZE; z++)
                blocks[ChunkSettings.Index(x, 0, z)] = new BlockElement { Value = 1 };

        state.EntityManager.AddComponentData(e, new ChunkLODState
        { Level = ChunkLODLevel.Unloaded });
        state.EntityManager.AddComponentData(e, new ChunkRenderEntity
        { Value = Entity.Null });

        // Default all-solid neighbor slices; filled by RefreshAllNeighborSlices
        state.EntityManager.AddComponentObject(e, new ChunkNeighborSlices());

        // Mark dirty so the mesh system picks it up once LOD is assigned
        state.EntityManager.AddComponent<ChunkDirty>(e);
    }

    // ── Neighbor slice wiring ─────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds neighbor slices for every chunk that exists.
    /// Call after bulk spawning, or after receiving a large batch from the server.
    /// For incremental single-chunk updates use RefreshNeighborSlicesFor in
    /// ChunkStreamingSystem.
    /// </summary>
    public void RefreshAllNeighborSlices(ref SystemState state)
    {
        var coordToEntity = new NativeHashMap<int3, Entity>(1024, Allocator.Temp);

        foreach (var (pos, entity) in
            SystemAPI.Query<RefRO<ChunkPosition>>().WithEntityAccess())
            coordToEntity.TryAdd(pos.ValueRO.Coord, entity);

        foreach (var (pos, entity) in
            SystemAPI.Query<RefRO<ChunkPosition>>().WithEntityAccess())
        {
            if (!state.EntityManager.HasComponent<ChunkNeighborSlices>(entity)) continue;
            var ns = state.EntityManager.GetComponentObject<ChunkNeighborSlices>(entity);
            int3 coord = pos.ValueRO.Coord;

            for (int dir = 0; dir < 6; dir++)
            {
                int3 neighborCoord = coord + DirOffset(dir);
                if (!coordToEntity.TryGetValue(neighborCoord, out Entity neighborEntity)) continue;
                if (!state.EntityManager.HasBuffer<BlockElement>(neighborEntity)) continue;

                var neighborBlocks = state.EntityManager
                    .GetBuffer<BlockElement>(neighborEntity)
                    .AsNativeArray()
                    .Reinterpret<byte>();

                byte[] slice = ExtractBorderSlice(neighborBlocks, dir ^ 1); // opposite face
                SetSlice(ns, dir, slice);
            }
        }

        coordToEntity.Dispose();
    }

    // ── Border slice extraction ───────────────────────────────────────────────

    /// <summary>
    /// Extracts the SIZE×SIZE border layer of a chunk for face direction `dir`.
    /// Static because it only touches a NativeArray — no ECS context needed.
    /// Called by ChunkStreamingSystem for incremental updates.
    /// </summary>
    public static byte[] ExtractBorderSlice(NativeArray<byte> blocks, int dir)
    {
        int S = ChunkSettings.SIZE;
        var slice = new byte[ChunkSettings.FACE];

        switch (dir)
        {
            case 0: // +Y → y = SIZE-1
                for (int x = 0; x < S; x++)
                    for (int z = 0; z < S; z++)
                        slice[ChunkSettings.SliceIndex(x, z)] =
                            blocks[ChunkSettings.Index(x, S - 1, z)];
                break;
            case 1: // -Y → y = 0
                for (int x = 0; x < S; x++)
                    for (int z = 0; z < S; z++)
                        slice[ChunkSettings.SliceIndex(x, z)] =
                            blocks[ChunkSettings.Index(x, 0, z)];
                break;
            case 2: // +X → x = SIZE-1
                for (int z = 0; z < S; z++)
                    for (int y = 0; y < S; y++)
                        slice[ChunkSettings.SliceIndex(z, y)] =
                            blocks[ChunkSettings.Index(S - 1, y, z)];
                break;
            case 3: // -X → x = 0
                for (int z = 0; z < S; z++)
                    for (int y = 0; y < S; y++)
                        slice[ChunkSettings.SliceIndex(z, y)] =
                            blocks[ChunkSettings.Index(0, y, z)];
                break;
            case 4: // +Z → z = SIZE-1
                for (int x = 0; x < S; x++)
                    for (int y = 0; y < S; y++)
                        slice[ChunkSettings.SliceIndex(x, y)] =
                            blocks[ChunkSettings.Index(x, y, S - 1)];
                break;
            case 5: // -Z → z = 0
                for (int x = 0; x < S; x++)
                    for (int y = 0; y < S; y++)
                        slice[ChunkSettings.SliceIndex(x, y)] =
                            blocks[ChunkSettings.Index(x, y, 0)];
                break;
        }

        return slice;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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

    public static int3 DirOffset(int dir) => dir switch
    {
        0 => new int3(0, 1, 0),
        1 => new int3(0, -1, 0),
        2 => new int3(1, 0, 0),
        3 => new int3(-1, 0, 0),
        4 => new int3(0, 0, 1),
        5 => new int3(0, 0, -1),
        _ => int3.zero
    };

    public void OnUpdate(ref SystemState state) { }
}