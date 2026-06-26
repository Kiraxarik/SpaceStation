using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

public struct SpectatorInput : IInputComponentData
{
    public float3 Move;
    public float2 Look;
    public float Speed;
}

public struct SpectatorState : IComponentData
{
    [GhostField] public float Yaw;
    [GhostField] public float Pitch;
}