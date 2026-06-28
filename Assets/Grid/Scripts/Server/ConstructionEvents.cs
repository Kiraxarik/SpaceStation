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

    /// <summary>Block type that was there before this write (0 = air).</summary>
    public byte OldValue;

    /// <summary>Block type written (0 = cleared to air).</summary>
    public byte NewValue;

    /// <summary>True when this write brought the chunk into existence for the first time.</summary>
    public bool IsNewChunk;
}

/// <summary>
/// Destroys all BlockChangedEvent entities at the end of each server frame so
/// consequence systems always see only the current frame's writes.
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

        ecb.Playback(state.EntityManager);
        ecb.Dispose();
    }
}