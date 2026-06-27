using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

/// <summary>
/// Server-side chunk authority for a placement-driven, sparse world.
///
/// The server holds ONLY non-empty chunks. Chunks come into existence the first
/// time a block is placed in them; there is no radius streaming and no per-client
/// state.
///
/// Each frame:
///   1. World snapshot — for each RequestWorldSnapshotRpc (sent once per client
///      on join), stream every populated chunk to that client.
///   2. Player placements — resolve PlaceBlockRpc world-block coord to chunk +
///      local index, apply to the store (creating the chunk if new), then:
///        • brand-new chunk  → broadcast the full chunk (fragmented) to all
///        • existing chunk   → broadcast a BlockChangeRpc delta to all
///   3. Server events — same resolution/broadcast for ServerBlockChange.
///
/// Full chunks (4096 bytes) exceed NetCode's single-packet RPC limit, so they
/// are split into ChunkDataFragmentRpc pieces and reassembled client-side.
/// Deltas are tiny and sent as a single RPC.
///
/// -- PERSISTENCE SEAM --
/// ChunkDataStore is in-memory. Swap for disk/DB; the request/broadcast paths
/// are unchanged.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
public partial class ServerChunkSystem : SystemBase
{
    ChunkDataStore _store;
    EntityQuery _connectionQuery;

    protected override void OnCreate()
    {
        _store = new ChunkDataStore();
        _connectionQuery = GetEntityQuery(
            ComponentType.ReadOnly<NetworkId>(),
            ComponentType.ReadOnly<NetworkStreamInGame>());
        RequireForUpdate<NetworkStreamInGame>();
    }

    protected override void OnUpdate()
    {
        var ecb = new EntityCommandBuffer(Allocator.Temp);
        var connections = _connectionQuery.ToEntityArray(Allocator.Temp);

        // ── 1. World snapshot requests (one per client on join) ────────────────
        foreach (var (_, source, requestEntity) in
            SystemAPI.Query<RefRO<RequestWorldSnapshotRpc>, RefRO<ReceiveRpcCommandRequest>>()
                     .WithEntityAccess())
        {
            Entity connection = source.ValueRO.SourceConnection;

            foreach (var kvp in _store.AllChunks())
                SendChunkFragments(connection, kvp.Key, kvp.Value, ecb);

            ecb.DestroyEntity(requestEntity);
        }

        // ── 2. Player placements ───────────────────────────────────────────────
        foreach (var (place, _, requestEntity) in
            SystemAPI.Query<RefRO<PlaceBlockRpc>, RefRO<ReceiveRpcCommandRequest>>()
                     .WithEntityAccess())
        {
            ApplyPlacement(place.ValueRO.WorldBlock, place.ValueRO.NewValue, connections, ecb);
            ecb.DestroyEntity(requestEntity);
        }

        // ── 3. Server-side events ──────────────────────────────────────────────
        foreach (var (change, eventEntity) in
            SystemAPI.Query<RefRO<ServerBlockChange>>().WithEntityAccess())
        {
            ApplyPlacement(change.ValueRO.WorldBlock, change.ValueRO.NewValue, connections, ecb);
            ecb.DestroyEntity(eventEntity);
        }

        ecb.Playback(EntityManager);
        ecb.Dispose();
        connections.Dispose();
    }

    // ── Placement resolution + broadcast ──────────────────────────────────────

    /// <summary>
    /// Resolves a global block coordinate to (chunkCoord, localIndex), applies
    /// the change to the store, and broadcasts the appropriate RPC. If the chunk
    /// did not exist, it is created and the full chunk is sent (fragmented);
    /// otherwise only the delta is sent.
    /// </summary>
    void ApplyPlacement(int3 worldBlock, byte newValue,
                        NativeArray<Entity> connections, EntityCommandBuffer ecb)
    {
        int3 chunkCoord = WorldBlockToChunk(worldBlock, out int3 local);
        int blockIndex = ChunkSettings.Index(local.x, local.y, local.z);

        // Placing air into a chunk that doesn't exist is a no-op.
        bool chunkExisted = _store.Has(chunkCoord);
        if (!chunkExisted && newValue == 0)
            return;

        _store.SetBlock(chunkCoord, blockIndex, newValue, out bool created);

        if (created)
        {
            // Brand-new chunk: clients have never seen it. Send the whole thing.
            byte[] data = _store.Get(chunkCoord);
            BroadcastChunkFragments(connections, chunkCoord, data, ecb);
        }
        else
        {
            // Existing chunk: clients already have it. Send only the delta.
            var rpc = new BlockChangeRpc
            {
                ChunkCoord = chunkCoord,
                BlockIndex = blockIndex,
                NewValue = newValue,
            };
            BroadcastDelta(connections, rpc, ecb);
        }
    }

    // ── Fragmented full-chunk send ────────────────────────────────────────────

    /// <summary>
    /// Broadcasts a full chunk to every in-game connection, split into
    /// ChunkDataFragmentRpc pieces that each fit inside a single packet.
    /// </summary>
    static void BroadcastChunkFragments(NativeArray<Entity> connections, int3 coord,
                                        byte[] data, EntityCommandBuffer ecb)
    {
        int count = ChunkFragmentCodec.FragmentCount;
        for (int f = 0; f < count; f++)
        {
            var rpc = ChunkFragmentCodec.Build(coord, data, f);
            for (int i = 0; i < connections.Length; i++)
            {
                var e = ecb.CreateEntity();
                ecb.AddComponent(e, rpc);
                ecb.AddComponent(e, new SendRpcCommandRequest { TargetConnection = connections[i] });
            }
        }
    }

    /// <summary>
    /// Sends a full chunk to a single connection, fragmented. Used by the
    /// per-client world snapshot on join.
    /// </summary>
    static void SendChunkFragments(Entity connection, int3 coord,
                                   byte[] data, EntityCommandBuffer ecb)
    {
        int count = ChunkFragmentCodec.FragmentCount;
        for (int f = 0; f < count; f++)
        {
            var rpc = ChunkFragmentCodec.Build(coord, data, f);
            var e = ecb.CreateEntity();
            ecb.AddComponent(e, rpc);
            ecb.AddComponent(e, new SendRpcCommandRequest { TargetConnection = connection });
        }
    }

    // ── Delta send ────────────────────────────────────────────────────────────

    static void BroadcastDelta(NativeArray<Entity> connections, BlockChangeRpc rpc, EntityCommandBuffer ecb)
    {
        for (int i = 0; i < connections.Length; i++)
        {
            var e = ecb.CreateEntity();
            ecb.AddComponent(e, rpc);
            ecb.AddComponent(e, new SendRpcCommandRequest { TargetConnection = connections[i] });
        }
    }

    // ── Coordinate resolution ─────────────────────────────────────────────────

    /// <summary>
    /// Converts a global block coordinate to its chunk coordinate and the
    /// block-local coordinate within that chunk. Uses floored division so
    /// negative coordinates resolve correctly (e.g. world block -1 → chunk -1,
    /// local SIZE-1), which matters for an infinite space volume.
    /// </summary>
    static int3 WorldBlockToChunk(int3 worldBlock, out int3 local)
    {
        int S = ChunkSettings.SIZE;
        int3 chunk = new int3(
            (int)math.floor(worldBlock.x / (float)S),
            (int)math.floor(worldBlock.y / (float)S),
            (int)math.floor(worldBlock.z / (float)S));

        local = worldBlock - chunk * S;   // always in [0, SIZE-1] after floored div
        return chunk;
    }

    // ── In-memory sparse store ────────────────────────────────────────────────

    sealed class ChunkDataStore
    {
        readonly System.Collections.Generic.Dictionary<int3, byte[]> _chunks = new();

        public bool Has(int3 coord) => _chunks.ContainsKey(coord);

        public byte[] Get(int3 coord) => _chunks[coord];

        public System.Collections.Generic.IEnumerable<
            System.Collections.Generic.KeyValuePair<int3, byte[]>> AllChunks() => _chunks;

        /// <summary>
        /// Applies a block change, allocating the chunk's buffer if it's new.
        /// <paramref name="created"/> is true when this call brought the chunk
        /// into existence.
        /// </summary>
        public void SetBlock(int3 coord, int blockIndex, byte value, out bool created)
        {
            if (!_chunks.TryGetValue(coord, out byte[] blocks))
            {
                blocks = new byte[ChunkSettings.VOLUME]; // all air
                _chunks[coord] = blocks;
                created = true;
            }
            else
            {
                created = false;
            }

            blocks[blockIndex] = value;

            // -- OPTIONAL EVICTION --
            // If a chunk becomes all-air again you may want to remove it so it
            // stops being sent on snapshot. Left in for now (cheap, and keeps
            // dirtied-then-cleared chunks from flickering on rejoin).
        }
    }
}