using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Handles first-person block interaction:
///   Left click  — destroy the targeted block (send air to server)
///   Right click — place the selected block on the targeted face
///   1           — select floor_tile
///   2           — select wall_panel
///
/// Uses a DDA (Digital Differential Analyser) voxel raycast to find which
/// block the camera is looking at. This is more reliable than a physics
/// raycast against greedy-merged meshes, which have no 1:1 block colliders.
///
/// Only interacts while the cursor is locked (Tab toggles lock per
/// SpectatorInputSystem). Interacting while the cursor is free would mean
/// clicking on UI or outside the game window.
///
/// Delete ClientBlockPlaceSendSystem.cs — this replaces it entirely.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
public partial class BlockInteractionSystem : SystemBase
{
    // ── Config ────────────────────────────────────────────────────────────────

    const float ReachBlocks = 6f; // max interaction distance in world-space blocks

    // ── State ─────────────────────────────────────────────────────────────────

    byte _selectedBlock; // runtime byte id of the currently held block type
    bool _registryReady;

    EntityQuery _playerQuery;
    EntityQuery _connectionQuery;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override void OnCreate()
    {
        _playerQuery = GetEntityQuery(
            ComponentType.ReadOnly<LocalPlayer>(),
            ComponentType.ReadOnly<LocalTransform>(),
            ComponentType.ReadOnly<GhostOwnerIsLocal>());

        _connectionQuery = GetEntityQuery(
            ComponentType.ReadOnly<NetworkId>(),
            ComponentType.ReadOnly<NetworkStreamInGame>());

        RequireForUpdate<NetworkStreamInGame>();
        RequireForUpdate<ChunkCoordRegistry>();
    }

    // ── Main update ───────────────────────────────────────────────────────────

    protected override void OnUpdate()
    {
        // Resolve block ids lazily — BlockRegistry may not be ready at OnCreate.
        if (!_registryReady && BlockRegistry.Faces.Length > 0)
        {
            _selectedBlock = BlockRegistry.GetId("floor_tile");
            _registryReady = true;
        }
        if (!_registryReady) return;

        var keyboard = Keyboard.current;
        var mouse = Mouse.current;
        if (keyboard == null || mouse == null) return;

        // ── Block selection ────────────────────────────────────────────────────
        if (keyboard.digit1Key.wasPressedThisFrame)
        {
            _selectedBlock = BlockRegistry.GetId("floor_tile");
            Debug.Log($"[BlockInteraction] Selected: floor_tile (id={_selectedBlock})");
        }
        if (keyboard.digit2Key.wasPressedThisFrame)
        {
            _selectedBlock = BlockRegistry.GetId("wall_panel");
            Debug.Log($"[BlockInteraction] Selected: wall_panel (id={_selectedBlock})");
        }

        // ── Interaction requires locked cursor ─────────────────────────────────
        bool leftClick = mouse.leftButton.wasPressedThisFrame;
        bool rightClick = mouse.rightButton.wasPressedThisFrame;

        if ((!leftClick && !rightClick) || Cursor.lockState != CursorLockMode.Locked)
            return;

        // ── Resolve player transform ───────────────────────────────────────────
        if (_playerQuery.IsEmpty) return;

        var playerEntities = _playerQuery.ToEntityArray(Allocator.Temp);
        if (playerEntities.Length == 0) { playerEntities.Dispose(); return; }
        var xform = EntityManager.GetComponentData<LocalTransform>(playerEntities[0]);
        playerEntities.Dispose();

        // ── Resolve server connection ──────────────────────────────────────────
        if (_connectionQuery.IsEmpty) return;

        var connectionEntities = _connectionQuery.ToEntityArray(Allocator.Temp);
        if (connectionEntities.Length == 0) { connectionEntities.Dispose(); return; }
        Entity connection = connectionEntities[0];
        connectionEntities.Dispose();

        // ── DDA raycast ────────────────────────────────────────────────────────
        float3 origin = xform.Position;
        float3 forward = math.normalize(math.mul(xform.Rotation, new float3(0f, 0f, 1f)));
        var registry = SystemAPI.GetSingleton<ChunkCoordRegistry>().Map;

        if (!DDA(origin, forward, ReachBlocks, registry,
                 out int3 hitBlock, out int3 faceNormal))
            return;

        // ── Determine target block and send RPC ────────────────────────────────
        int3 worldBlock;
        byte newValue;

        if (leftClick)
        {
            // Destroy: replace targeted block with air
            worldBlock = hitBlock;
            newValue = 0;
            Debug.Log($"[BlockInteraction] Destroy ({worldBlock.x},{worldBlock.y},{worldBlock.z})");
        }
        else
        {
            // Place: put selected block on the adjacent face
            worldBlock = hitBlock + faceNormal;
            newValue = _selectedBlock;
            Debug.Log($"[BlockInteraction] Place '{BlockRegistry.NameById.GetValueOrDefault(newValue, "?")}' " +
                      $"at ({worldBlock.x},{worldBlock.y},{worldBlock.z})");
        }

        var ecb = new EntityCommandBuffer(Allocator.Temp);
        Entity req = ecb.CreateEntity();
        ecb.AddComponent(req, new PlaceBlockRpc { WorldBlock = worldBlock, NewValue = newValue });
        ecb.AddComponent(req, new SendRpcCommandRequest { TargetConnection = connection });
        ecb.Playback(EntityManager);
        ecb.Dispose();
    }

    // ── DDA voxel raycast ─────────────────────────────────────────────────────

    /// <summary>
    /// Steps through voxels along <paramref name="dir"/> from <paramref name="origin"/>
    /// until it either hits a solid block or exceeds <paramref name="maxDist"/>.
    ///
    /// <paramref name="hitBlock"/>   — world block coord of the first solid block found.
    /// <paramref name="faceNormal"/> — integer face normal pointing from the hit block
    ///                                 back toward the ray origin (use hitBlock + faceNormal
    ///                                 to get the air block adjacent to the hit face).
    /// </summary>
    bool DDA(float3 origin, float3 dir, float maxDist,
             NativeHashMap<int3, Entity> registry,
             out int3 hitBlock, out int3 faceNormal)
    {
        hitBlock = default;
        faceNormal = default;

        int3 pos = (int3)math.floor(origin);
        int3 step = new int3(
            dir.x >= 0f ? 1 : -1,
            dir.y >= 0f ? 1 : -1,
            dir.z >= 0f ? 1 : -1);

        // Distance along the ray needed to cross one voxel on each axis.
        // If direction component is 0, tDelta is infinity (never crosses that axis).
        float3 tDelta = new float3(
            dir.x != 0f ? math.abs(1f / dir.x) : float.PositiveInfinity,
            dir.y != 0f ? math.abs(1f / dir.y) : float.PositiveInfinity,
            dir.z != 0f ? math.abs(1f / dir.z) : float.PositiveInfinity);

        // Distance to the first voxel boundary on each axis.
        float3 tMax = new float3(
            dir.x >= 0f ? (pos.x + 1f - origin.x) * tDelta.x : (origin.x - pos.x) * tDelta.x,
            dir.y >= 0f ? (pos.y + 1f - origin.y) * tDelta.y : (origin.y - pos.y) * tDelta.y,
            dir.z >= 0f ? (pos.z + 1f - origin.z) * tDelta.z : (origin.z - pos.z) * tDelta.z);

        int3 lastPos = pos;
        int3 lastStepAxis = int3.zero;

        while (true)
        {
            if (IsBlockSolid(pos, registry))
            {
                hitBlock = pos;
                faceNormal = lastPos - pos; // points from hit block toward the air we came from
                return true;
            }

            // Advance to the nearest voxel boundary
            lastPos = pos;
            if (tMax.x < tMax.y)
            {
                if (tMax.x < tMax.z)
                {
                    if (tMax.x > maxDist) return false;
                    pos.x += step.x;
                    tMax.x += tDelta.x;
                }
                else
                {
                    if (tMax.z > maxDist) return false;
                    pos.z += step.z;
                    tMax.z += tDelta.z;
                }
            }
            else
            {
                if (tMax.y < tMax.z)
                {
                    if (tMax.y > maxDist) return false;
                    pos.y += step.y;
                    tMax.y += tDelta.y;
                }
                else
                {
                    if (tMax.z > maxDist) return false;
                    pos.z += step.z;
                    tMax.z += tDelta.z;
                }
            }
        }
    }

    // ── Block lookup ──────────────────────────────────────────────────────────

    bool IsBlockSolid(int3 worldBlock, NativeHashMap<int3, Entity> registry)
    {
        int S = ChunkSettings.SIZE;
        int3 chunkCoord = new int3(
            (int)math.floor(worldBlock.x / (float)S),
            (int)math.floor(worldBlock.y / (float)S),
            (int)math.floor(worldBlock.z / (float)S));

        int3 local = worldBlock - chunkCoord * S;

        if (!registry.TryGetValue(chunkCoord, out Entity chunkEntity)) return false;
        if (!EntityManager.HasBuffer<BlockElement>(chunkEntity)) return false;

        var blocks = EntityManager.GetBuffer<BlockElement>(chunkEntity, isReadOnly: true);
        int idx = ChunkSettings.Index(local.x, local.y, local.z);
        if ((uint)idx >= (uint)blocks.Length) return false;

        return blocks[idx].Value != 0;
    }
}