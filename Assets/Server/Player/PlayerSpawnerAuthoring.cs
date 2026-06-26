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
/// Anchors the player prefab into baking so NetCode includes it in the
/// GhostCollection. Doesn't create the PlayerSpawner singleton itself —
/// ServerPlayerSpawnerSystem auto-discovers the prefab at runtime (by
/// finding the ghost prefab that has a GhostOwner component) and creates
/// the singleton from that. This script's only job is making sure the
/// prefab is reachable by baking at all; without a reference like this
/// somewhere in a loaded sub-scene, the prefab never becomes a registered
/// ghost in the first place.
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
        // Registering the reference is enough to pull the prefab into baking
        // and therefore into the GhostCollection. We don't need to store it
        // on an entity ourselves.
        GetEntity(authoring.PlayerPrefab, TransformUsageFlags.Dynamic);
    }
}