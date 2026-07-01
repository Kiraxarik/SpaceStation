using Unity.Entities;
using Unity.Mathematics;

/// <summary>
/// Emitted by ServerChunkSystem every time a block write is committed to the
/// authoritative data store — whether the write came from a player placement
/// (PlaceBlockRpc), a server-side trigger (ServerBlockChange), or any future
/// source.
///
/// This is the OUTPUT fact, not an intent. By the time this entity exists the
/// store has already been updated and the network broadcast has been queued.
///
/// Consequence systems (power networks, atmospherics, mass recompute, …) query
/// these events in the same frame they appear and react independently.
/// BlockChangedEventCleanupSystem destroys them at the end of the frame so each
/// event is processed exactly once.
///
/// Usage:
///   foreach (var ev in SystemAPI.Query&lt;RefRO&lt;BlockChangedEvent&gt;&gt;())
///       HandleBlockChanged(ev.ValueRO);
/// </summary>
public struct BlockChangedEvent : IComponentData
{
    /// <summary>Global block coordinate (chunk * SIZE + local).</summary>
    public int3 WorldBlock;

    /// <summary>Chunk this block belongs to.</summary>
    public int3 ChunkCoord;

    /// <summary>Flat index within the chunk buffer (ChunkSettings.Index result).</summary>
    public int LocalIndex;

    /// <summary>Block type that was there before this write (0 = air). ushort,
    /// not byte (§1.5) — mirrors BlockElement.Value's width.</summary>
    public ushort OldValue;

    /// <summary>Block type written (0 = cleared to air).</summary>
    public ushort NewValue;

    /// <summary>True when this write brought the chunk into existence for the first time.</summary>
    public bool IsNewChunk;
}

/// <summary>
/// Emitted by ServerPartSystem every time a part is installed onto or removed
/// from a tile — the sparse-layer counterpart to BlockChangedEvent (§2.3, §7.1).
///
/// When Installed is false and this removal emptied the tile (its last part),
/// ServerPartSystem destroys the tile entity BEFORE this event is queryable by
/// other systems this frame — TileEntity will already be dead. Treat WorldBlock +
/// PartId as the durable identity for a removal; only dereference TileEntity
/// after checking EntityManager.Exists, and only meaningfully for Installed == true.
///
/// Swept by the same BlockChangedEventCleanupSystem, same frame-end timing.
/// </summary>
public struct PartChangedEvent : IComponentData
{
    /// <summary>Global block coordinate of the tile this part changed on.</summary>
    public int3 WorldBlock;

    /// <summary>The tile entity — see the "already destroyed" caveat above.</summary>
    public Entity TileEntity;

    /// <summary>PartRegistry numeric id of the part that was installed or removed.</summary>
    public ushort PartId;

    /// <summary>True = installed, false = removed.</summary>
    public bool Installed;
}

/// <summary>
/// Destroys all BlockChangedEvent and PartChangedEvent entities at the end of
/// each server frame so consequence systems always see only the current frame's
/// writes.
///
/// Runs last in SimulationSystemGroup so every system that wants to react to the
/// events can do so first.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
public partial struct BlockChangedEventCleanupSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        // Collect and destroy all event entities in one ECB pass.
        var ecb = new EntityCommandBuffer(Unity.Collections.Allocator.Temp);

        foreach (var (_, entity) in
            SystemAPI.Query<RefRO<BlockChangedEvent>>().WithEntityAccess())
        {
            ecb.DestroyEntity(entity);
        }

        foreach (var (_, entity) in
            SystemAPI.Query<RefRO<PartChangedEvent>>().WithEntityAccess())
        {
            ecb.DestroyEntity(entity);
        }

        ecb.Playback(state.EntityManager);
        ecb.Dispose();
    }
}