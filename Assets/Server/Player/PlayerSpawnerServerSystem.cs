using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
public partial struct ServerPlayerSpawnerSystem : ISystem
{
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
            Debug.LogWarning("[ServerPlayerSpawner] Player ghost prefab not found yet.");
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