using Unity.Entities;
using UnityEngine;

[DisallowMultipleComponent]
public class SpectatorInputAuthoring : MonoBehaviour { }

public class SpectatorInputBaker : Baker<SpectatorInputAuthoring>
{
    public override void Bake(SpectatorInputAuthoring authoring)
    {
        var e = GetEntity(TransformUsageFlags.None);
        AddComponent<SpectatorInput>(e);
        AddComponent<SpectatorState>(e);
    }
}