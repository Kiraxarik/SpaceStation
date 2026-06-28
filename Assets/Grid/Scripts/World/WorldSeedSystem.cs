using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

/// <summary>
/// Seeds the starting platform once, on the server, as soon as the world is
/// ready. Emits ServerBlockChange events — one per block — which ServerChunkSystem
/// consumes and broadcasts exactly like a player placement. This system knows
/// nothing about chunks, stores, or fragmentation; it only declares which blocks
/// should exist at startup.
///
/// Runs before ServerChunkSystem so the seed events are produced and consumed
/// in the same frame they're created.
///
/// Waits until BlockRegistry is populated (so block name → id resolution works)
/// before seeding, then disables itself so it never runs again.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateBefore(typeof(ServerChunkSystem))]
public partial struct WorldSeedSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        // Wait for the block registry to load before resolving block ids.
        if (BlockRegistry.Faces.Length == 0) return;

        byte floorId = BlockRegistry.GetId("floor_tile");
        if (floorId == 0) floorId = 1; // fallback numeric id

        var ecb = new EntityCommandBuffer(Unity.Collections.Allocator.Temp);

        // 5×5 floor platform centred on the origin at y = 0.
        for (int x = -2; x <= 2; x++)
            for (int z = -2; z <= 2; z++)
            {
                Entity e = ecb.CreateEntity();
                ecb.AddComponent(e, new ServerBlockChange
                {
                    WorldBlock = new int3(x, 0, z),
                    NewValue = floorId,
                });
            }

        ecb.Playback(state.EntityManager);
        ecb.Dispose();

        Debug.Log("[WorldSeed] Starting platform seeded (5×5 floor at y=0).");

        // One-shot: never run again.
        state.Enabled = false;
    }
}