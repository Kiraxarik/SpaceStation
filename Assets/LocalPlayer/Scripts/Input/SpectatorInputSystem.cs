using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.InputSystem;

[UpdateInGroup(typeof(GhostInputSystemGroup))]
public partial struct SpectatorInputSystem : ISystem
{
    public static bool JustLocked;

    static bool _locked;
    static bool _hadLocalPlayer;
    static bool _lastFocused = true;
    static bool _loggedNullDevices;

    // 1x1 transparent texture used as a "hidden" cursor. Swapping the cursor
    // image via SetCursor instead of toggling Cursor.visible sidesteps the OS
    // ShowCursor race entirely — there's nothing to win, Windows just renders
    // a transparent image.
    static Texture2D _blankCursor;
    static Texture2D BlankCursor()
    {
        if (_blankCursor == null)
        {
            _blankCursor = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _blankCursor.SetPixel(0, 0, new Color(0f, 0f, 0f, 0f));
            _blankCursor.Apply();
        }
        return _blankCursor;
    }

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

        bool hasLocalPlayer = !SystemAPI.QueryBuilder()
            .WithAll<SpectatorInput, GhostOwnerIsLocal>()
            .Build()
            .IsEmpty;

        if (hasLocalPlayer && !_hadLocalPlayer)
            _locked = true;
        else if (!hasLocalPlayer && _hadLocalPlayer)
            _locked = false;
        _hadLocalPlayer = hasLocalPlayer;

        bool wasLocked = _locked;

        if (hasLocalPlayer && keyboard.tabKey.wasPressedThisFrame)
            _locked = !_locked;

        Cursor.lockState = _locked ? CursorLockMode.Locked : CursorLockMode.None;
        if (_locked)
            Cursor.SetCursor(BlankCursor(), Vector2.zero, CursorMode.ForceSoftware);
        else
            Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);

        if (!wasLocked && _locked)
            JustLocked = true;

        float3 move = float3.zero;
        if (keyboard.wKey.isPressed) move.z += 1f;
        if (keyboard.sKey.isPressed) move.z -= 1f;
        if (keyboard.aKey.isPressed) move.x -= 1f;
        if (keyboard.dKey.isPressed) move.x += 1f;
        if (keyboard.eKey.isPressed) move.y += 1f;
        if (keyboard.qKey.isPressed) move.y -= 1f;

        float2 look = float2.zero;
        if (_locked)
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