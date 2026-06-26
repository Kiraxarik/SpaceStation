using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.InputSystem;

[UpdateInGroup(typeof(GhostInputSystemGroup))]
public partial struct SpectatorInputSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        var keyboard = Keyboard.current;
        var mouse = Mouse.current;

        if (keyboard == null || mouse == null) return;

        // Press Tab to lock/unlock cursor
        if (keyboard.tabKey.wasPressedThisFrame)
        {
            Cursor.lockState = Cursor.lockState == CursorLockMode.Locked
                ? CursorLockMode.None
                : CursorLockMode.Locked;
        }

        float3 move = float3.zero;
        if (keyboard.wKey.isPressed) move.z += 1f;
        if (keyboard.sKey.isPressed) move.z -= 1f;
        if (keyboard.aKey.isPressed) move.x -= 1f;
        if (keyboard.dKey.isPressed) move.x += 1f;
        if (keyboard.eKey.isPressed) move.y += 1f;
        if (keyboard.qKey.isPressed) move.y -= 1f;

        float2 look = float2.zero;
        if (Cursor.lockState == CursorLockMode.Locked)
        {
            var delta = mouse.delta.ReadValue();
            look.x = delta.x;
            look.y = delta.y;
        }

        float speed = keyboard.leftShiftKey.isPressed ? 3f : 1f;

        foreach (var input in
            SystemAPI.Query<RefRW<SpectatorInput>>()
                     .WithAll<GhostOwnerIsLocal>())
        {
            input.ValueRW.Move = move;
            input.ValueRW.Look = look;
            input.ValueRW.Speed = speed;
        }
    }
}