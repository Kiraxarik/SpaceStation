using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

/// <summary>
/// Hybrid bridge: copies the LocalPlayer ghost's transform onto the scene's
/// main camera every frame. Plain SystemBase (not Burst) since it touches a
/// managed UnityEngine.Camera object.
/// </summary>
public partial class CameraFollowSystem : SystemBase
{
    protected override void OnUpdate()
    {
        var cam = Camera.main;
        if (cam == null)
            return;

        foreach (var transform in SystemAPI.Query<RefRO<LocalTransform>>().WithAll<LocalPlayer>())
        {
            cam.transform.SetPositionAndRotation(transform.ValueRO.Position, transform.ValueRO.Rotation);
        }
    }
}