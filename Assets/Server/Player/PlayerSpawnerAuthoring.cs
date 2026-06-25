using Unity.Entities;
using UnityEngine;

/// <summary>
/// Runtime singleton holding a reference to the player ghost prefab Entity.
/// GoInGameServerSystem reads this to know what to instantiate.
/// </summary>
public struct PlayerSpawner : IComponentData
{
    public Entity PlayerPrefab;
}

/// <summary>
/// Drag your player prefab (the one with GhostAuthoringComponent on it) into
/// the PlayerPrefab field, then place this on any GameObject inside your
/// sub-scene. It bakes down into the PlayerSpawner singleton above.
/// </summary>
[DisallowMultipleComponent]
public class PlayerSpawnerAuthoring : MonoBehaviour
{
    public GameObject PlayerPrefab;
}

public class PlayerSpawnerBaker : Baker<PlayerSpawnerAuthoring>
{
    public override void Bake(PlayerSpawnerAuthoring authoring)
    {
        Entity entity = GetEntity(TransformUsageFlags.None);
        AddComponent(entity, new PlayerSpawner
        {
            PlayerPrefab = GetEntity(authoring.PlayerPrefab, TransformUsageFlags.Dynamic)
        });
    }
}