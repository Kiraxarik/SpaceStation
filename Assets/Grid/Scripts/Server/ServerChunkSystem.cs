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
/// After every committed write ApplyPlacement emits a BlockChangedEvent entity.
/// Consequence systems (power, atmospherics, mass, …) query those events this
/// frame; BlockChangedEventCleanupSystem destroys them at frame end.
///
/// A full chunk (ChunkSettings.BYTE_SIZE bytes — 8192 now that blocks are ushort,
/// §1.5) exceeds NetCode's single-packet RPC limit, so it's always split into
/// ChunkDataFragmentRpc pieces and reassembled client-side. Deltas are tiny and
/// sent as a single RPC.
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

        // Used only for section 2/3's broadcast-to-everyone-in-game path below —
        // correct to require NetworkStreamInGame there, since a live delta should
        // only go to players who actually have a spawned body, not to someone
        // still sitting in the lobby with their initial snapshot mid-flight.
        _connectionQuery = GetEntityQuery(
            ComponentType.ReadOnly<NetworkId>(),
            ComponentType.ReadOnly<NetworkStreamInGame>());

        // NOT the system-level gate, though: RequireForUpdate<NetworkStreamInGame>
        // here would stop the WHOLE system ticking — including section 1, which
        // answers RequestWorldSnapshotRpc via source.ValueRO.SourceConnection
        // directly, nothing to do with _connectionQuery. Server-side,
        // NetworkStreamInGame is only added at RoundPhase.Running ∧ Committed
        // (PlayerSpawnServerSystem), so gating the system on it meant a client's
        // snapshot request — sent before they can possibly be Committed — would
        // never get answered. NetworkId is present as soon as the connection
        // exists, which is all the system needs to be worth ticking at all.
        RequireForUpdate<NetworkId>();
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

    // ── External read access ──────────────────────────────────────────────────

    /// <summary>
    /// Read-only block lookup for other server systems (currently ServerPartSystem,
    /// to validate a part install target is solid) that need the store's current
    /// value without duplicating chunk-coordinate resolution. 0 = air or an
    /// unloaded/nonexistent chunk — same "no block here" meaning either way, which
    /// is all a caller like "is this solid?" needs.
    /// </summary>
    public ushort GetBlockValue(int3 worldBlock)
    {
        int3 chunkCoord = WorldBlockToChunk(worldBlock, out int3 local);
        int blockIndex = ChunkSettings.Index(local.x, local.y, local.z);
        return _store.GetBlock(chunkCoord, blockIndex);
    }

    // ── Placement resolution + broadcast ──────────────────────────────────────

    /// <summary>
    /// Resolves a global block coordinate to (chunkCoord, localIndex), reads the
    /// old value, applies the change to the store, broadcasts the appropriate RPC,
    /// and emits a BlockChangedEvent for consequence systems to react to.
    ///
    /// If the chunk did not exist, it is created and the full chunk is sent
    /// (fragmented); otherwise only the delta is sent.
    /// </summary>
    void ApplyPlacement(int3 worldBlock, ushort newValue,
                        NativeArray<Entity> connections, EntityCommandBuffer ecb)
    {
        int3 chunkCoord = WorldBlockToChunk(worldBlock, out int3 local);
        int blockIndex = ChunkSettings.Index(local.x, local.y, local.z);

        // Placing air into a chunk that doesn't exist is a no-op.
        bool chunkExisted = _store.Has(chunkCoord);
        if (!chunkExisted && newValue == 0)
            return;

        // Read old value before the write so the event carries both.
        ushort oldValue = chunkExisted ? _store.GetBlock(chunkCoord, blockIndex) : (ushort)0;

        _store.SetBlock(chunkCoord, blockIndex, newValue, out bool created);

        if (created)
        {
            // Brand-new chunk: clients have never seen it. Send the whole thing.
            ushort[] data = _store.Get(chunkCoord);
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

        // ── Emit construction event ────────────────────────────────────────────
        // Consequence systems (power, atmospherics, mass recompute, …) react to
        // this rather than reading ServerChunkSystem internals directly.
        var eventEntity = ecb.CreateEntity();
        ecb.AddComponent(eventEntity, new BlockChangedEvent
        {
            WorldBlock = worldBlock,
            ChunkCoord = chunkCoord,
            LocalIndex = blockIndex,
            OldValue = oldValue,
            NewValue = newValue,
            IsNewChunk = created,
        });
    }

    // ── Fragmented full-chunk send ────────────────────────────────────────────

    /// <summary>
    /// Broadcasts a full chunk to every in-game connection, split into
    /// ChunkDataFragmentRpc pieces that each fit inside a single packet.
    /// </summary>
    static void BroadcastChunkFragments(NativeArray<Entity> connections, int3 coord,
                                        ushort[] data, EntityCommandBuffer ecb)
    {
        var wireBytes = new byte[ChunkSettings.BYTE_SIZE];
        ChunkBlockCodec.FromUshortArray(data, wireBytes);

        int count = ChunkFragmentCodec.FragmentCount;
        for (int f = 0; f < count; f++)
        {
            var rpc = ChunkFragmentCodec.Build(coord, wireBytes, f);
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
                                   ushort[] data, EntityCommandBuffer ecb)
    {
        var wireBytes = new byte[ChunkSettings.BYTE_SIZE];
        ChunkBlockCodec.FromUshortArray(data, wireBytes);

        int count = ChunkFragmentCodec.FragmentCount;
        for (int f = 0; f < count; f++)
        {
            var rpc = ChunkFragmentCodec.Build(coord, wireBytes, f);
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
        readonly System.Collections.Generic.Dictionary<int3, ushort[]> _chunks = new();

        public bool Has(int3 coord) => _chunks.ContainsKey(coord);

        public ushort[] Get(int3 coord) => _chunks[coord];

        /// <summary>Returns the block value at blockIndex, or 0 if the chunk doesn't exist.</summary>
        public ushort GetBlock(int3 coord, int blockIndex)
            => _chunks.TryGetValue(coord, out ushort[] blocks) ? blocks[blockIndex] : (ushort)0;

        public System.Collections.Generic.IEnumerable<
            System.Collections.Generic.KeyValuePair<int3, ushort[]>> AllChunks() => _chunks;

        /// <summary>
        /// Applies a block change, allocating the chunk's buffer if it's new.
        /// <paramref name="created"/> is true when this call brought the chunk
        /// into existence.
        /// </summary>
        public void SetBlock(int3 coord, int blockIndex, ushort value, out bool created)
        {
            if (!_chunks.TryGetValue(coord, out ushort[] blocks))
            {
                blocks = new ushort[ChunkSettings.VOLUME]; // all air
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