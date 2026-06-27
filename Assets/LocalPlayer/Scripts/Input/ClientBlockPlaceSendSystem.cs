using Unity.Entities;
using Unity.NetCode;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Client-only input → network seam for block placement.
/// On keypress, computes a global block coordinate a few cells ahead of the
/// local player (camera) and sends a single PlaceBlockRpc to the server.
///
/// The server (ServerChunkSystem) resolves which chunk that coord lands in,
/// creates the chunk on demand if needed, applies the change, and broadcasts
/// the result to all clients — so nothing chunk-aware happens here.
///
/// Not Burst-compiled: reads the managed Input System Keyboard device.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
public partial struct ClientBlockPlaceSendSystem : ISystem
{
    const float PlaceAheadBlocks = 3f; // 0 = exactly at camera (you'd be inside it)
    const byte PlaceValue = 2;  // 2 = wall panel, 1 = floor tile, 0 = clear/erase

    public void OnUpdate(ref SystemState state)
    {
        // New Input System: read the keyboard device, not UnityEngine.Input.
        var keyboard = Keyboard.current;
        if (keyboard == null || !keyboard.eKey.wasPressedThisFrame)
            return;

        // Resolve the in-game server connection to address the RPC to.
        Entity connection = Entity.Null;
        foreach (var (netId, entity) in
            SystemAPI.Query<RefRO<NetworkId>>()
                     .WithAll<NetworkStreamInGame>()
                     .WithEntityAccess())
        {
            connection = entity;
            break;
        }
        if (connection == Entity.Null)
            return; // not in-game yet

        // Local player transform == camera location.
        bool found = false;
        float3 camPos = default;
        quaternion camRot = quaternion.identity;
        foreach (var t in SystemAPI.Query<RefRO<LocalTransform>>().WithAll<LocalPlayer>())
        {
            camPos = t.ValueRO.Position;
            camRot = t.ValueRO.Rotation;
            found = true;
            break;
        }
        if (!found)
            return;

        float3 forward = math.mul(camRot, new float3(0f, 0f, 1f));
        float3 target = camPos + forward * PlaceAheadBlocks;

        // Global block coordinate. Floored so negative space resolves correctly;
        // the server does chunk-boundary math from here.
        int3 worldBlock = (int3)math.floor(target);

        var ecb = new EntityCommandBuffer(Unity.Collections.Allocator.Temp);
        Entity req = ecb.CreateEntity();
        ecb.AddComponent(req, new PlaceBlockRpc
        {
            WorldBlock = worldBlock,
            NewValue = PlaceValue,
        });
        ecb.AddComponent(req, new SendRpcCommandRequest { TargetConnection = connection });
        ecb.Playback(state.EntityManager);
        ecb.Dispose();

        Debug.Log($"[BlockPlace] PlaceBlockRpc worldBlock=({worldBlock.x},{worldBlock.y},{worldBlock.z}) val={PlaceValue}");
    }
}