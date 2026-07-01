using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

// ── Job: apply incoming block-change deltas ───────────────────────────────────

/// <summary>
/// Appends each BlockChangeRpc to the target chunk's ChunkBlockUpdate buffer.
/// Single-threaded: buffer appends via lookup must not race.
/// Deltas for chunks the client doesn't have are dropped (it will get correct
/// state from the next full chunk send / snapshot).
/// </summary>
[BurstCompile]
partial struct ApplyDeltasJob : IJobEntity
{
    public EntityCommandBuffer Ecb;
    [ReadOnly] public NativeHashMap<int3, Entity> Registry;
    [NativeDisableContainerSafetyRestriction]
    public BufferLookup<ChunkBlockUpdate> UpdateBuffers;

    public void Execute(Entity rpcEntity, in BlockChangeRpc data)
    {
        if (Registry.TryGetValue(data.ChunkCoord, out Entity chunkEntity)
            && UpdateBuffers.HasBuffer(chunkEntity))
        {
            UpdateBuffers[chunkEntity].Add(new ChunkBlockUpdate
            {
                BlockIndex = data.BlockIndex,
                NewValue = data.NewValue,
            });
        }
        Ecb.DestroyEntity(rpcEntity);
    }
}

// ── System ────────────────────────────────────────────────────────────────────

/// <summary>
/// Client-side chunk reception for the placement-driven model.
///
/// Owns the ChunkCoordRegistry. Responsibilities:
///   1. On going in-game, send RequestWorldSnapshotRpc exactly once.
///   2. Receive ChunkDataFragmentRpc, reassemble into a full chunk's wire bytes,
///      create the chunk entity if new (or refill an existing one), populate its
///      block buffer, mark dirty.
///   3. Receive BlockChangeRpc: queue the delta onto the chunk's update buffer
///      (drained by ChunkApplySystem).
///
/// Full-chunk reception (step 2) runs on the main thread because it performs
/// structural changes (entity creation, managed ChunkNeighborSlices). The delta
/// path (step 3) is a Burst job.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
public partial class ClientChunkReceiveSystem : SystemBase
{
    NativeHashMap<int3, Entity> _coordToEntity;
    EntityQuery _connectionQuery;
    EntityQuery _manifestReadyQuery;

    // Reassembly staging for fragmented chunks: coord → partial buffer + tracking.
    // A full chunk (ChunkSettings.BYTE_SIZE bytes — 8192 now that blocks are
    // ushort, §1.5) exceeds NetCode's single-packet RPC limit, so the server
    // sends it as ChunkDataFragmentRpc pieces; we collect them here until every
    // fragment for a coord has arrived, then hand the assembled bytes to
    // ReceiveChunk.
    readonly System.Collections.Generic.Dictionary<int3, ChunkAssembly> _assembling = new();

    sealed class ChunkAssembly
    {
        public byte[] Data = new byte[ChunkSettings.BYTE_SIZE];
        public bool[] Got;            // which fragment indices have arrived (dedupe)
        public int Received;          // count of distinct fragments received
        public int Expected = -1;     // total fragments, set from the first one seen
    }

    protected override void OnCreate()
    {
        _coordToEntity = new NativeHashMap<int3, Entity>(4096, Allocator.Persistent);

        var registryEntity = EntityManager.CreateEntity();
        EntityManager.AddComponentData(registryEntity, new ChunkCoordRegistry
        {
            Map = _coordToEntity
        });

        _connectionQuery = GetEntityQuery(
            ComponentType.ReadOnly<NetworkId>(),
            ComponentType.ReadOnly<NetworkStreamInGame>());

        // Reception is gated until asset sync completes (ClientAssetSyncSystem),
        // which itself waits for the content manifest to be adopted first. That
        // covers both halves of §1.5/§7.4: no server-numbered block bytes are
        // processed under the wrong numbering, AND no chunk is meshed against a
        // block whose texture/model hasn't actually arrived yet — which is what
        // let ContentManifestReady alone through before AssetSyncReady existed.
        _manifestReadyQuery = GetEntityQuery(
            ComponentType.ReadOnly<AssetSyncReady>(),
            ComponentType.ReadOnly<NetworkStreamInGame>());

        RequireForUpdate<NetworkStreamInGame>();
    }

    protected override void OnDestroy()
    {
        if (_coordToEntity.IsCreated) _coordToEntity.Dispose();
    }

    protected override void OnUpdate()
    {
        // ── 0. Gate on asset sync ───────────────────────────────────────────────
        // Do nothing — neither request the world nor process any incoming chunk
        // data — until asset sync is complete (AssetSyncReady). Unprocessed RPCs
        // persist as entities and are handled once ready.
        if (_manifestReadyQuery.IsEmpty)
            return;

        var ecb = new EntityCommandBuffer(Allocator.Temp);

        // ── 1. Send the one-time world snapshot request ────────────────────────
        // Tag the connection once it's in-game so we don't re-send every frame.
        foreach (var (id, connectionEntity) in
            SystemAPI.Query<RefRO<NetworkId>>()
                     .WithAll<NetworkStreamInGame>()
                     .WithNone<WorldSnapshotRequested>()
                     .WithEntityAccess())
        {
            var req = ecb.CreateEntity();
            ecb.AddComponent(req, new RequestWorldSnapshotRpc());
            ecb.AddComponent(req, new SendRpcCommandRequest
            {
                TargetConnection = connectionEntity
            });

            ecb.AddComponent<WorldSnapshotRequested>(connectionEntity);
        }

        // ── 2. Receive fragmented full chunk payloads ──────────────────────────
        var completedChunks = new System.Collections.Generic.List<(int3 Coord, byte[] Data)>();

        foreach (var (frag, rpcEntity) in
            SystemAPI.Query<RefRO<ChunkDataFragmentRpc>>()
                     .WithAll<ReceiveRpcCommandRequest>()
                     .WithEntityAccess())
        {
            AccumulateFragment(frag.ValueRO, completedChunks);
            ecb.DestroyEntity(rpcEntity);
        }

        ecb.Playback(EntityManager);
        ecb.Dispose();

        // Now safe: iteration over the RPC query has fully ended.
        foreach (var (coord, data) in completedChunks)
        {
            ReceiveChunk(coord, data);
        }

        // ── 3. Receive block-change deltas (Burst job) ─────────────────────────
        var deltaEcb = new EntityCommandBuffer(Allocator.TempJob);
        Dependency = new ApplyDeltasJob
        {
            Ecb = deltaEcb,
            Registry = _coordToEntity,
            UpdateBuffers = GetBufferLookup<ChunkBlockUpdate>(),
        }.Schedule(Dependency);
        Dependency.Complete();
        deltaEcb.Playback(EntityManager);
        deltaEcb.Dispose();
    }

    // ── Fragment reassembly ───────────────────────────────────────────────────

    /// <summary>
    /// Scatters one chunk fragment into its staging buffer. Once every fragment
    /// for a coord has arrived, hands the assembled wire bytes off to be applied
    /// via ReceiveChunk. Fragments may span multiple frames; staging persists on
    /// the system between frames. Reliable RPC delivery guarantees all arrive.
    /// </summary>
    void AccumulateFragment(in ChunkDataFragmentRpc frag,
     System.Collections.Generic.List<(int3, byte[])> completed)
    {
        if (!_assembling.TryGetValue(frag.Coord, out var asm))
        {
            asm = new ChunkAssembly
            {
                Expected = frag.FragmentCount,
                Got = new bool[frag.FragmentCount],
            };
            _assembling[frag.Coord] = asm;
        }

        if (frag.FragmentIndex < asm.Got.Length && !asm.Got[frag.FragmentIndex])
        {
            ChunkFragmentCodec.Scatter(frag, asm.Data);
            asm.Got[frag.FragmentIndex] = true;
            asm.Received++;
        }

        if (asm.Received >= asm.Expected)
        {
            completed.Add((frag.Coord, asm.Data));
            _assembling.Remove(frag.Coord);
        }
    }

    // ── Full chunk reception ──────────────────────────────────────────────────

    void ReceiveChunk(int3 coord, byte[] data)
    {
        if (!_coordToEntity.TryGetValue(coord, out Entity entity))
        {
            entity = CreateChunkEntity(coord);
            _coordToEntity.TryAdd(coord, entity);
        }

        // Fill block buffer from the reassembled wire bytes.
        var blocks = EntityManager.GetBuffer<BlockElement>(entity);
        ChunkBlockCodec.ToBuffer(data, blocks);

        // Refresh slices on this chunk and its existing neighbors so cross-chunk
        // culling is correct, then mark dirty.
        RefreshNeighborSlices(entity, coord);

        if (!EntityManager.HasComponent<ChunkDirty>(entity))
            EntityManager.AddComponent<ChunkDirty>(entity);
    }

    Entity CreateChunkEntity(int3 coord)
    {
        Entity e = EntityManager.CreateEntity();
        EntityManager.AddComponentData(e, new ChunkPosition { Coord = coord });

        var blocks = EntityManager.AddBuffer<BlockElement>(e);
        blocks.Resize(ChunkSettings.VOLUME, NativeArrayOptions.ClearMemory);

        EntityManager.AddBuffer<ChunkBlockUpdate>(e);

        // Chunks the server sends are always meant to be visible; start them at
        // Full LOD. ChunkStreamingSystem (if present) can still retier them by
        // distance afterwards.
        EntityManager.AddComponentData(e, new ChunkLODState { Level = ChunkLODLevel.Full });
        EntityManager.AddComponentData(e, new ChunkRenderEntity { Value = Entity.Null });
        EntityManager.AddComponentObject(e, new ChunkNeighborSlices());

        return e;
    }

    // ── Neighbor slice management ─────────────────────────────────────────────

    void RefreshNeighborSlices(Entity entity, int3 coord)
    {
        if (!EntityManager.HasComponent<ChunkNeighborSlices>(entity)) return;
        var ns = EntityManager.GetComponentObject<ChunkNeighborSlices>(entity);
        var ourBlocks = EntityManager.GetBuffer<BlockElement>(entity)
            .AsNativeArray().Reinterpret<ushort>();

        // Pass 1: pure reads — no structural changes allowed in here.
        var pendingSlices = new System.Collections.Generic.List<(ChunkNeighborSlices nns, int dir, ushort[] slice)>();
        var pendingDirty = new System.Collections.Generic.List<Entity>();

        for (int dir = 0; dir < 6; dir++)
        {
            int3 neighborCoord = coord + DirOffset(dir);
            if (!_coordToEntity.TryGetValue(neighborCoord, out Entity neighbor)) continue;
            if (!EntityManager.HasBuffer<BlockElement>(neighbor)) continue;

            var nb = EntityManager.GetBuffer<BlockElement>(neighbor)
                .AsNativeArray().Reinterpret<ushort>();

            SetSlice(ns, dir, ExtractBorderSlice(nb, dir ^ 1));

            if (EntityManager.HasComponent<ChunkNeighborSlices>(neighbor))
            {
                var nns = EntityManager.GetComponentObject<ChunkNeighborSlices>(neighbor);
                pendingSlices.Add((nns, dir ^ 1, ExtractBorderSlice(ourBlocks, dir)));

                if (!EntityManager.HasComponent<ChunkDirty>(neighbor))
                    pendingDirty.Add(neighbor);
            }
        }

        // Pass 2: apply managed-object writes and structural changes now that all
        // NativeArray reads for this call are finished.
        foreach (var (nns, dir, slice) in pendingSlices)
            SetSlice(nns, dir, slice);

        foreach (var neighbor in pendingDirty)
            EntityManager.AddComponent<ChunkDirty>(neighbor);
    }

    // ── Border slice extraction ───────────────────────────────────────────────

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

    // ── Helpers ───────────────────────────────────────────────────────────────

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