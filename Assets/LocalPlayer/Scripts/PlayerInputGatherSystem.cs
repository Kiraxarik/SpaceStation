using Unity.Entities;
using Unity.NetCode;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Reads raw Unity Input and writes it into the PlayerInput component on the
/// entity owned by this client. Runs in GhostInputSystemGroup, the standard
/// place for capturing input before it's packed into the command buffer.
/// </summary>
[UpdateInGroup(typeof(GhostInputSystemGroup))]
public partial struct PlayerInputGatherSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        foreach (var input in SystemAPI.Query<RefRW<PlayerInput>>()
                     .WithAll<GhostOwnerIsLocal>())
        {
            input.ValueRW.Move = new float2(
                Input.GetAxisRaw("Horizontal"),
                Input.GetAxisRaw("Vertical"));

            input.ValueRW.Look = new float2(
                Input.GetAxis("Mouse X"),
                Input.GetAxis("Mouse Y"));

            int vertical = 0;
            if (Input.GetKey(KeyCode.Space)) vertical += 1;
            if (Input.GetKey(KeyCode.LeftControl)) vertical -= 1;
            input.ValueRW.VerticalAxis = vertical;

            if (Input.GetKey(KeyCode.LeftShift))
                input.ValueRW.Boost.Set();
        }
    }
}