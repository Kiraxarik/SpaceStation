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
///   Left click          — destroy the targeted block (send air to server)
///   Right click          — place the selected block on the targeted face
///   Middle click          — TEST PATH: install a hardcoded test part on the
///                           targeted (solid) block — see remarks below
///   Shift + Middle click  — TEST PATH: remove that test part
///   1                    — cycle selection forward (smallest id → largest, wraps)
///   2                    — cycle selection backward (largest id → smallest, wraps)
///
/// Cycling replaces the old hardcoded "1 = floor_tile, 2 = wall_panel" — that
/// only ever reached two specific base-game blocks by name. Cycling walks every
/// block id that actually has local content (BlockRegistry.Definitions), so any
/// block from any loaded mod is reachable without knowing its string id up
/// front — including one that only just finished downloading via asset sync.
///
/// Uses a DDA (Digital Differential Analyser) voxel raycast to find which
/// block the camera is looking at. This is more reliable than a physics
/// raycast against greedy-merged meshes, which have no 1:1 block colliders.
///
/// Only interacts while the cursor is locked (Tab toggles, or click-to-lock —
/// see SpectatorInputSystem). Interacting while the cursor is free would mean
/// clicking on UI or outside the game window. Also skips the exact frame the
/// cursor just locked (SpectatorInputSystem.JustLocked), so the click that
/// re-engages control doesn't also fire a destroy/place.
///
/// The middle-click part path is a TEMPORARY stand-in — there is no real
/// interaction dispatch yet (§4, two-hand left=attack/right=interact with
/// tool-gated handlers). It exists only to exercise the sparse tile-entity +
/// parts layer (§2.3, ServerPartSystem) end-to-end before that lands. Once §4
/// exists, part install/remove should route through its right-click world
/// interaction handler instead, and this block can be deleted.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
public partial class BlockInteractionSystem : SystemBase
{
    // ── Config ────────────────────────────────────────────────────────────────

    const float ReachBlocks = 6f; // max interaction distance in world-space blocks

    /// <summary>Hardcoded test part for the middle-click debug path — see the
    /// TEMPORARY note above. Matches Assets/StreamingAssets/Mods/base/parts.json.</summary>
    const string TestPartId = "base:wiring";

    // ── State ─────────────────────────────────────────────────────────────────

    // ushort, not byte (§1.5) — mirrors BlockElement.Value's width.
    ushort _selectedBlock; // runtime numeric id of the currently held block type
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
        // Resolve a default selection lazily — BlockRegistry may not be ready at
        // OnCreate, and content can grow later (asset sync). Gate on Definitions
        // specifically, not Faces: Faces can be nonzero from manifest ids that
        // don't have local data yet (§1.5 "awaiting asset sync"), which aren't
        // valid to select for placement.
        if (!_registryReady && BlockRegistry.Definitions.Count > 0)
        {
            var ids = new List<ushort>(BlockRegistry.Definitions.Keys);
            ids.Sort();
            _selectedBlock = ids[0];
            _registryReady = true;
            Debug.Log($"[BlockInteraction] Default selection: " +
                      $"{BlockRegistry.NameById.GetValueOrDefault(_selectedBlock, "?")} (id={_selectedBlock})");
        }
        if (!_registryReady) return;

        var keyboard = Keyboard.current;
        var mouse = Mouse.current;
        if (keyboard == null || mouse == null) return;

        // ── Block selection (cycle forward/backward) ────────────────────────────
        if (keyboard.digit1Key.wasPressedThisFrame)
            CycleSelection(forward: true);
        if (keyboard.digit2Key.wasPressedThisFrame)
            CycleSelection(forward: false);

        // ── Interaction requires locked cursor ─────────────────────────────────
        bool leftClick = mouse.leftButton.wasPressedThisFrame;
        bool rightClick = mouse.rightButton.wasPressedThisFrame;
        bool middleClick = mouse.middleButton.wasPressedThisFrame;

        if ((!leftClick && !rightClick && !middleClick)
            || Cursor.lockState != CursorLockMode.Locked
            || SpectatorInputSystem.JustLocked)
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

        // ── Middle click: TEST PATH for the parts layer (see class remarks) ────
        if (middleClick)
        {
            SendPartRpc(connection, hitBlock, remove: keyboard.leftShiftKey.isPressed);
            return;
        }

        // ── Determine target block and send RPC ────────────────────────────────
        int3 worldBlock;
        // ushort, not byte (§1.5) — mirrors BlockElement.Value's width.
        ushort newValue;

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

    // ── Parts test path ────────────────────────────────────────────────────────

    /// <summary>TEMPORARY — see class remarks. Sends Install/RemovePartRpc for
    /// TestPartId targeting the SOLID block the raycast hit (not the adjacent
    /// face, unlike placement).</summary>
    void SendPartRpc(Entity connection, int3 hitBlock, bool remove)
    {
        var ecb = new EntityCommandBuffer(Allocator.Temp);
        Entity req = ecb.CreateEntity();

        // FixedString64Bytes has no safe truncating constructor of its own —
        // reuse the codebase's existing wire-safe string helper (mirrors
        // ServerContentManifestSystem.ToFixed) rather than a raw conversion.
        FixedString64Bytes partId = AssetFragmentCodec.ToFixed64(TestPartId);

        if (remove)
        {
            ecb.AddComponent(req, new RemovePartRpc
            {
                WorldBlock = hitBlock,
                PartId = partId,
            });
            Debug.Log($"[BlockInteraction] Remove part '{TestPartId}' at " +
                      $"({hitBlock.x},{hitBlock.y},{hitBlock.z})");
        }
        else
        {
            ecb.AddComponent(req, new InstallPartRpc
            {
                WorldBlock = hitBlock,
                PartId = partId,
            });
            Debug.Log($"[BlockInteraction] Install part '{TestPartId}' at " +
                      $"({hitBlock.x},{hitBlock.y},{hitBlock.z})");
        }

        ecb.AddComponent(req, new SendRpcCommandRequest { TargetConnection = connection });
        ecb.Playback(EntityManager);
        ecb.Dispose();
    }

    // ── Block selection cycling ───────────────────────────────────────────────

    /// <summary>
    /// Advances _selectedBlock to the next (or previous) valid block id, wrapping
    /// at either end. Rebuilds the sorted id list on demand — only runs on an
    /// actual keypress, not every frame, so there's no reason to cache it and
    /// risk it going stale after content downloads mid-session (asset sync).
    /// </summary>
    void CycleSelection(bool forward)
    {
        var ids = new List<ushort>(BlockRegistry.Definitions.Keys);
        if (ids.Count == 0) return;
        ids.Sort();

        int currentIndex = ids.IndexOf(_selectedBlock);
        int nextIndex;
        if (currentIndex < 0)
        {
            // Current selection isn't in the valid set (shouldn't normally
            // happen once _registryReady is true, but content can change under
            // us) — land on the first entry either direction rather than guess.
            nextIndex = forward ? 0 : ids.Count - 1;
        }
        else
        {
            nextIndex = forward
                ? (currentIndex + 1) % ids.Count
                : (currentIndex - 1 + ids.Count) % ids.Count;
        }

        _selectedBlock = ids[nextIndex];
        string name = BlockRegistry.NameById.GetValueOrDefault(_selectedBlock, "?");
        Debug.Log($"[BlockInteraction] Selected: {name} (id={_selectedBlock})");
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