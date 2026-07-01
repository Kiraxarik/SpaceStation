using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.InputSystem;

[UpdateInGroup(typeof(GhostInputSystemGroup))]
public partial struct SpectatorInputSystem : ISystem
{
    /// <summary>
    /// True for exactly the one frame the cursor transitions unlocked → locked.
    /// Consumers (e.g. BlockInteractionSystem) check this to swallow a click that
    /// lands on the same frame Tab re-engages control, so an unlucky same-frame
    /// Tab+click doesn't also fire a block destroy/place. Reset to false at the
    /// top of every OnUpdate, so it only ever reads true on the transition frame
    /// itself — this system runs in GhostInputSystemGroup, which executes before
    /// the default SimulationSystemGroup BlockInteractionSystem lives in, so the
    /// flag is always current by the time anything else reads it this frame.
    /// </summary>
    public static bool JustLocked;

    // Tracks Application.isFocused so we can log the instant it changes — cheap,
    // and the single most useful fact for diagnosing "Tab does nothing": if the
    // window never reports focused, the OS likely isn't delivering keyboard
    // events to the process at all, which would explain Tab doing nothing while
    // still leaving open why a click (which grants OS focus as a side effect on
    // most platforms) behaved differently.
    static bool _lastFocused = true;
    static bool _loggedNullDevices;

    public void OnUpdate(ref SystemState state)
    {
        JustLocked = false;

        bool focused = Application.isFocused;
        if (focused != _lastFocused)
        {
            Debug.Log($"[SpectatorInput] Application.isFocused changed: {_lastFocused} -> {focused}");
            _lastFocused = focused;
        }

        var keyboard = Keyboard.current;
        var mouse = Mouse.current;

        if (keyboard == null || mouse == null)
        {
            if (!_loggedNullDevices)
            {
                Debug.LogWarning("[SpectatorInput] Keyboard.current or Mouse.current is null — " +
                                 "no keyboard/mouse device detected yet.");
                _loggedNullDevices = true;
            }
            return;
        }

        bool wasLocked = Cursor.lockState == CursorLockMode.Locked;

        // Tab is the sole lock/unlock trigger. A click-based version was tried
        // and reverted: it fired on ANY click, including UI buttons like the
        // main menu's Play — the cursor would lock (and hide) before or instead
        // of the click reaching the button's OnClick, breaking menu interaction
        // entirely. Tab doesn't have that problem since it's never a UI input.
        if (keyboard.tabKey.wasPressedThisFrame)
        {
            bool locking = !wasLocked;
            Cursor.lockState = locking ? CursorLockMode.Locked : CursorLockMode.None;

            // lockState alone doesn't reliably hide the cursor on every platform —
            // Cursor.visible is a separate flag and was never being set anywhere
            // in this codebase, which is the most likely reason the cursor stayed
            // visible even on a successful lock.
            //
            Cursor.visible = !locking;
        }

        if (!wasLocked && Cursor.lockState == CursorLockMode.Locked)
            JustLocked = true;

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