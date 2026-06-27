using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

[UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
public partial struct SpectatorMovementSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        float dt = SystemAPI.Time.DeltaTime;

        foreach ((RefRO<SpectatorInput> input, RefRW<SpectatorState> spectatorState, RefRW<LocalTransform> transform) in
            SystemAPI.Query<RefRO<SpectatorInput>, RefRW<SpectatorState>, RefRW<LocalTransform>>()
                     .WithAll<Simulate>())
        {
            spectatorState.ValueRW.Yaw += input.ValueRO.Look.x * 0.2f;
            spectatorState.ValueRW.Pitch -= input.ValueRO.Look.y * 0.2f;
            spectatorState.ValueRW.Pitch = math.clamp(spectatorState.ValueRO.Pitch, -89f, 89f);

            quaternion rotation = math.mul(
                quaternion.RotateY(math.radians(spectatorState.ValueRO.Yaw)),
                quaternion.RotateX(math.radians(spectatorState.ValueRO.Pitch)));

            float3 worldMove = math.rotate(rotation,
                input.ValueRO.Move * 20f * input.ValueRO.Speed * dt);

            transform.ValueRW.Position += worldMove;
            transform.ValueRW.Rotation = rotation;
        }
    }
}