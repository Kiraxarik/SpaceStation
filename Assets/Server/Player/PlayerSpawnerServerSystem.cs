using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
public partial struct ServerPlayerSpawnerSystem : ISystem
{
    // Throttle the diagnostic so it prints about once a second instead of every tick.
    double _nextLogTime;

    public void OnCreate(ref SystemState state)
    {
        // Wait until ghost prefabs are available
        state.RequireForUpdate<GhostCollection>();
    }

    public void OnUpdate(ref SystemState state)
    {
        if (SystemAPI.HasSingleton<PlayerSpawner>())
        {
            state.Enabled = false;
            return;
        }

        // ── DIAGNOSTIC ───────────────────────────────────────────────────────
        // Count every Prefab+GhostType entity, and how many of those also carry
        // a GhostOwner. This tells us whether GhostCollection is empty (ghost
        // never registered) or populated-but-missing-GhostOwner (different bug).
        int totalGhostPrefabs = 0;
        int withGhostOwner = 0;
        foreach (var (ghostType, entity) in
            SystemAPI.Query<RefRO<GhostType>>()
                     .WithAll<Prefab>()
                     .WithEntityAccess())
        {
            totalGhostPrefabs++;
            if (state.EntityManager.HasComponent<GhostOwner>(entity))
                withGhostOwner++;
        }

        double now = SystemAPI.Time.ElapsedTime;
        if (now >= _nextLogTime)
        {
            _nextLogTime = now + 1.0;

            // Also report whether the GhostCollection singleton's prefab list has
            // been populated yet — this is NetCode's own registered-ghost list.
            int collectionCount = -1;
            if (SystemAPI.TryGetSingletonEntity<GhostCollection>(out var gcEntity)
                && state.EntityManager.HasBuffer<GhostCollectionPrefab>(gcEntity))
            {
                collectionCount = state.EntityManager
                    .GetBuffer<GhostCollectionPrefab>(gcEntity).Length;
            }

            Debug.Log($"[ServerPlayerSpawner][DIAG] Prefab+GhostType entities={totalGhostPrefabs}, " +
                      $"of which have GhostOwner={withGhostOwner}. " +
                      $"GhostCollectionPrefab buffer length={collectionCount}.");
        }
        // ── END DIAGNOSTIC ───────────────────────────────────────────────────

        // Ghost prefabs are entities with the Prefab tag and GhostType component
        Entity playerPrefab = Entity.Null;
        foreach (var (ghostType, entity) in
            SystemAPI.Query<RefRO<GhostType>>()
                     .WithAll<Prefab>()
                     .WithEntityAccess())
        {
            // GhostOwner is added to player ghosts by NetCode baking
            if (state.EntityManager.HasComponent<GhostOwner>(entity))
            {
                playerPrefab = entity;
                break;
            }
        }

        if (playerPrefab == Entity.Null)
        {
            // (per-tick warning kept silent; the DIAG line above covers this)
            return;
        }

        var singleton = state.EntityManager.CreateEntity();
        state.EntityManager.AddComponentData(singleton, new PlayerSpawner
        {
            PlayerPrefab = playerPrefab
        });

        Debug.Log("[ServerPlayerSpawner] PlayerSpawner singleton created.");
        state.Enabled = false;
    }
}