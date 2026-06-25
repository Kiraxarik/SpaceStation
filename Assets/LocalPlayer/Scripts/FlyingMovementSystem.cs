using Unity.Entities;
using Unity.NetCode;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Burst;

/// <summary>
/// Free-flight movement + look, predicted identically on client and server.
/// Runs in PredictedSimulationSystemGroup so it executes for both the
/// server's single authoritative pass and the client's prediction/replay.
/// </summary>
[UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
[BurstCompile]
public partial struct FlyingMovementSystem : ISystem
{
    private const float LookSpeed = 2f;
    private const float MoveSpeed = 10f;
    private const float BoostMultiplier = 2f;

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        float dt = SystemAPI.Time.DeltaTime;
        var networkTime = SystemAPI.GetSingleton<NetworkTime>();

        foreach (var (input, transform, prediction) in
                 SystemAPI.Query<RefRO<PlayerInput>, RefRW<LocalTransform>, RefRO<PredictedGhost>>())
        {
            if (!prediction.ValueRO.ShouldPredict(networkTime.ServerTick))
                continue;

            quaternion rot = transform.ValueRO.Rotation;

            // Mouse look: yaw around world up, pitch around local right
            float yaw = input.ValueRO.Look.x * LookSpeed * dt;
            float pitch = -input.ValueRO.Look.y * LookSpeed * dt;
            rot = math.mul(rot, quaternion.Euler(0f, yaw, 0f));
            rot = math.mul(rot, quaternion.Euler(pitch, 0f, 0f));
            transform.ValueRW.Rotation = rot;

            // Flight movement relative to facing direction, free on all 3 axes
            float3 forward = math.mul(rot, new float3(0f, 0f, 1f));
            float3 right = math.mul(rot, new float3(1f, 0f, 0f));
            float3 up = new float3(0f, 1f, 0f);

            float3 moveDir =
                forward * input.ValueRO.Move.y +
                right * input.ValueRO.Move.x +
                up * input.ValueRO.VerticalAxis;

            float speed = input.ValueRO.Boost.IsSet ? MoveSpeed * BoostMultiplier : MoveSpeed;

            transform.ValueRW.Position += moveDir * speed * dt;
        }
    }
}