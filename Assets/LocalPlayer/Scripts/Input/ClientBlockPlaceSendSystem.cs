using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Client-only input → network seam for block placement.
/// On E keypress, computes a global block coordinate a few blocks ahead of the
/// local player (camera) and sends a PlaceBlockRpc to the server.
///
/// Block type is resolved by name from BlockRegistry so this system never
/// needs updating when new block types are added.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
public partial struct ClientBlockPlaceSendSystem : ISystem
{
    const float PlaceAheadBlocks = 3f;

    // Resolved once on first use so GetId isn't called every frame.
    // BlockRegistry is populated before any scene loads so this is safe.
    static byte _placeValue = 0;
    static bool _valueResolved = false;

    static byte PlaceValue
    {
        get
        {
            if (!_valueResolved)
            {
                _placeValue = BlockRegistry.GetId("wall_panel");
                _valueResolved = true;
            }
            return _placeValue;
        }
    }

    public void OnUpdate(ref SystemState state)
    {
        var keyboard = Keyboard.current;
        if (keyboard == null || !keyboard.eKey.wasPressedThisFrame)
            return;

        // Resolve the server connection entity.
        Entity connection = Entity.Null;
        foreach (var (netId, entity) in
            SystemAPI.Query<RefRO<NetworkId>>()
                     .WithAll<NetworkStreamInGame>()
                     .WithEntityAccess())
        {
            connection = entity;
            break;
        }
        if (connection == Entity.Null) return;

        // Local player transform == camera location.
        bool found = false;
        float3 camPos = default;
        quaternion camRot = quaternion.identity;

        foreach (var t in SystemAPI.Query<RefRO<LocalTransform>>()
                                   .WithAll<LocalPlayer, GhostOwnerIsLocal>())
        {
            camPos = t.ValueRO.Position;
            camRot = t.ValueRO.Rotation;
            found = true;
            break;
        }
        if (!found) return;

        float3 forward = math.mul(camRot, new float3(0f, 0f, 1f));
        float3 target = camPos + forward * PlaceAheadBlocks;
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

        Debug.Log($"[BlockPlace] '{BlockRegistry.NameById.GetValueOrDefault(PlaceValue, "?")}' " +
                  $"(id={PlaceValue}) at ({worldBlock.x},{worldBlock.y},{worldBlock.z})");
    }
}