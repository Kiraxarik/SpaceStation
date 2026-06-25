using Unity.NetCode;
using Unity.Mathematics;

/// <summary>
/// Networked input for the flying player. NetCode auto-generates the
/// underlying input buffer / command-target wiring for IInputComponentData.
/// </summary>
public struct PlayerInput : IInputComponentData
{
    public float2 Move;        // x = strafe, y = forward/back
    public float2 Look;        // mouse delta (x = yaw, y = pitch)
    public int VerticalAxis;   // -1 = descend, 0 = none, 1 = ascend
    public InputEvent Boost;   // example one-shot button event
}