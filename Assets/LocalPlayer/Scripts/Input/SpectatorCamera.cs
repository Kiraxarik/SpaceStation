using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

// Follows the local player entity's position each frame
public class SpectatorCamera : MonoBehaviour
{
    World _clientWorld;
    EntityQuery _playerQuery;

    void Start()
    {
        foreach (var world in World.All)
        {
            if (world.Name == "ClientWorld")
            {
                _clientWorld = world;
                _playerQuery = world.EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<LocalTransform>(),
                    ComponentType.ReadOnly<GhostOwnerIsLocal>());
                break;
            }
        }
    }

    void LateUpdate()
    {
        if (_clientWorld == null || _playerQuery.IsEmpty) return;

        var transform = _playerQuery.GetSingleton<LocalTransform>();
        gameObject.transform.position = new Vector3(
            transform.Position.x,
            transform.Position.y,
            transform.Position.z);
        gameObject.transform.rotation = transform.Rotation;
    }
}