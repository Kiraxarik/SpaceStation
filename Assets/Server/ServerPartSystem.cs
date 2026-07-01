using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

/// <summary>
/// The sparse-layer construction handler (§2.3, §7.1). Owns the coord → tile
/// entity map (TileEntityRegistry) and is the ONLY system that creates,
/// mutates, or destroys tile entities and their InstalledPart buffers (§0.6,
/// one writer).
///
/// A tile entity exists only while it has at least one part installed — install
/// creates it on demand, removing the last part destroys it, so a plain block
/// never pays for this layer (§2.3, §0.4).
///
/// Handles InstallPartRpc / RemovePartRpc — see PartNetworkMessages.cs for why
/// these are a temporary stand-in for real interaction dispatch (§4). Validates
/// against ServerChunkSystem's dense store (a part can only attach to a solid
/// block), then writes the sparse layer and recomputes the tile's primitive
/// flag components from the full installed set (§0.2).
///
/// Emits PartChangedEvent for consequence systems, exactly like ServerChunkSystem
/// emits BlockChangedEvent for dense writes; BlockChangedEventCleanupSystem
/// (ConstructionEvents.cs) sweeps both at end of frame.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateAfter(typeof(ServerChunkSystem))]
public partial class ServerPartSystem : SystemBase
{
    NativeHashMap<int3, Entity> _tiles;

    protected override void OnCreate()
    {
        _tiles = new NativeHashMap<int3, Entity>(256, Allocator.Persistent);

        var registryEntity = EntityManager.CreateEntity();
        EntityManager.AddComponentData(registryEntity, new TileEntityRegistry { Map = _tiles });

        RequireForUpdate<NetworkId>();
    }

    protected override void OnDestroy()
    {
        if (_tiles.IsCreated) _tiles.Dispose();
    }

    protected override void OnUpdate()
    {
        // Pass 1: drain RPCs into plain lists and destroy the request entities via
        // ECB. Mirrors ClientChunkReceiveSystem's completedChunks pattern — keeps
        // structural changes (tile creation/destruction, buffer edits below) from
        // interleaving with iteration over the RPC query.
        var ecb = new EntityCommandBuffer(Allocator.Temp);

        var installs = new List<(int3 WorldBlock, string PartId)>();
        var removals = new List<(int3 WorldBlock, string PartId)>();

        foreach (var (rpc, _, entity) in
            SystemAPI.Query<RefRO<InstallPartRpc>, RefRO<ReceiveRpcCommandRequest>>().WithEntityAccess())
        {
            installs.Add((rpc.ValueRO.WorldBlock, rpc.ValueRO.PartId.ToString()));
            ecb.DestroyEntity(entity);
        }

        foreach (var (rpc, _, entity) in
            SystemAPI.Query<RefRO<RemovePartRpc>, RefRO<ReceiveRpcCommandRequest>>().WithEntityAccess())
        {
            removals.Add((rpc.ValueRO.WorldBlock, rpc.ValueRO.PartId.ToString()));
            ecb.DestroyEntity(entity);
        }

        ecb.Playback(EntityManager);
        ecb.Dispose();

        // Pass 2: apply.
        foreach (var (worldBlock, partId) in installs)
            Install(worldBlock, partId);

        foreach (var (worldBlock, partId) in removals)
            Remove(worldBlock, partId);
    }

    // ── Install ────────────────────────────────────────────────────────────────

    void Install(int3 worldBlock, string partName)
    {
        var chunkSystem = World.GetExistingSystemManaged<ServerChunkSystem>();
        if (chunkSystem == null) return;

        if (chunkSystem.GetBlockValue(worldBlock) == 0)
        {
            Debug.LogWarning($"[ServerPart] Install '{partName}' at " +
                $"({worldBlock.x},{worldBlock.y},{worldBlock.z}) rejected — no solid block there.");
            return;
        }

        ushort partId = PartRegistry.GetId(partName);
        if (partId == PartRegistry.InvalidId)
        {
            Debug.LogWarning($"[ServerPart] Install rejected — unknown part '{partName}'.");
            return;
        }

        Entity tileEntity = GetOrCreateTile(worldBlock);
        var parts = EntityManager.GetBuffer<InstalledPart>(tileEntity);

        for (int i = 0; i < parts.Length; i++)
            if (parts[i].PartId == partId)
            {
                Debug.Log($"[ServerPart] '{partName}' already installed at " +
                    $"({worldBlock.x},{worldBlock.y},{worldBlock.z}).");
                return;
            }

        parts.Add(new InstalledPart { PartId = partId });
        RecomputePrimitives(tileEntity, parts);

        var eventEntity = EntityManager.CreateEntity();
        EntityManager.AddComponentData(eventEntity, new PartChangedEvent
        {
            WorldBlock = worldBlock,
            TileEntity = tileEntity,
            PartId = partId,
            Installed = true,
        });

        Debug.Log($"[ServerPart] Installed '{partName}' at " +
            $"({worldBlock.x},{worldBlock.y},{worldBlock.z}). " +
            $"{parts.Length} part(s) now installed. Primitives: {DescribePrimitives(tileEntity)}.");
    }

    // ── Remove ─────────────────────────────────────────────────────────────────

    void Remove(int3 worldBlock, string partName)
    {
        if (!_tiles.TryGetValue(worldBlock, out Entity tileEntity) || !EntityManager.Exists(tileEntity))
        {
            Debug.Log($"[ServerPart] Remove '{partName}' at " +
                $"({worldBlock.x},{worldBlock.y},{worldBlock.z}) — nothing installed there.");
            return;
        }

        ushort partId = PartRegistry.GetId(partName);
        if (partId == PartRegistry.InvalidId)
        {
            Debug.LogWarning($"[ServerPart] Remove rejected — unknown part '{partName}'.");
            return;
        }

        var parts = EntityManager.GetBuffer<InstalledPart>(tileEntity);
        int found = -1;
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].PartId == partId) { found = i; break; }

        if (found < 0)
        {
            Debug.Log($"[ServerPart] '{partName}' not installed at " +
                $"({worldBlock.x},{worldBlock.y},{worldBlock.z}).");
            return;
        }

        parts.RemoveAt(found);
        RecomputePrimitives(tileEntity, parts);

        // Emitted BEFORE the possible tear-down below — a consequence system
        // reading this event for a removal that emptied the tile will find
        // TileEntity already destroyed by the time it runs. Treat WorldBlock +
        // PartId as the durable identity for a removal, not the Entity.
        var eventEntity = EntityManager.CreateEntity();
        EntityManager.AddComponentData(eventEntity, new PartChangedEvent
        {
            WorldBlock = worldBlock,
            TileEntity = tileEntity,
            PartId = partId,
            Installed = false,
        });

        int remaining = parts.Length;

        if (remaining == 0)
        {
            // Sparse layer only holds entities for tiles with at least one part
            // (§2.3) — a tile with nothing installed goes back to costing
            // nothing, exactly like it never had an entity to begin with.
            _tiles.Remove(worldBlock);
            EntityManager.DestroyEntity(tileEntity);
        }

        Debug.Log($"[ServerPart] Removed '{partName}' at " +
            $"({worldBlock.x},{worldBlock.y},{worldBlock.z}). {remaining} part(s) remain.");
    }

    // ── Shared helpers ─────────────────────────────────────────────────────────

    Entity GetOrCreateTile(int3 worldBlock)
    {
        if (_tiles.TryGetValue(worldBlock, out Entity existing) && EntityManager.Exists(existing))
            return existing;

        Entity e = EntityManager.CreateEntity();
        EntityManager.AddComponentData(e, new TileCoord { WorldBlock = worldBlock });
        EntityManager.AddBuffer<InstalledPart>(e);
        _tiles.TryAdd(worldBlock, e);
        return e;
    }

    /// <summary>
    /// Recomputes this tile's primitive flag components from the union of every
    /// currently-installed part's declared primitives, adding/removing to match.
    /// Two parts can both grant the same primitive; removing one must not clear
    /// a flag the other still holds — recomputing from scratch each time makes
    /// that automatic instead of requiring per-part reference counting.
    /// </summary>
    void RecomputePrimitives(Entity tileEntity, DynamicBuffer<InstalledPart> parts)
    {
        var active = new HashSet<string>();
        for (int i = 0; i < parts.Length; i++)
        {
            var def = PartRegistry.GetDefinition(parts[i].PartId);
            if (def?.Primitives == null) continue;
            foreach (var prim in def.Primitives)
                active.Add(prim);
        }

        foreach (var name in PartPrimitives.KnownNames)
        {
            if (!PartPrimitives.TryGetComponentType(name, out var type)) continue;

            bool has = EntityManager.HasComponent(tileEntity, type);
            bool should = active.Contains(name);

            if (should && !has) EntityManager.AddComponent(tileEntity, type);
            else if (!should && has) EntityManager.RemoveComponent(tileEntity, type);
        }
    }

    string DescribePrimitives(Entity tileEntity)
    {
        var found = new List<string>();
        foreach (var name in PartPrimitives.KnownNames)
            if (PartPrimitives.TryGetComponentType(name, out var type) && EntityManager.HasComponent(tileEntity, type))
                found.Add(name);
        return found.Count > 0 ? string.Join(", ", found) : "none";
    }
}