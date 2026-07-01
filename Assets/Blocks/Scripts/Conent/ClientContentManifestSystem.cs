using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

/// <summary>
/// Client side of the manifest handshake (architecture §1.5). On going in-game:
///   1. Send ContentManifestRequestRpc once.
///   2. Collect ContentManifestEntryRpc into a staging table, keyed by numeric id.
///   3. On ContentManifestCompleteRpc, once all entries are present, adopt the
///      server ordering via BlockRegistry.InitializeFromManifest and tag the
///      connection ContentManifestReady.
///
/// Staging is a Dictionary<ushort, string> rather than a fixed-size array: numeric
/// ids are ushort-bounded (§1.5, up to 65536), and real content is nowhere near
/// that — a flat `new string[65536]` would just be a wasted allocation for actual
/// mod-scale counts. The dictionary costs memory proportional to what's actually
/// received.
///
/// Local definitions (loaded at startup by ContentBootstrap and cached in
/// BlockRegistry) supply the DATA — faces, sim properties. The server supplies the
/// IDENTITY ordering. Adopting it overwrites the client's own startup-derived
/// numbering, which is what makes the server authoritative when content differs.
/// When content matches (incl. host mode over loopback) the adopted ordering is
/// identical to what the client already had, so the rebuild is a harmless no-op.
///
/// Runs before ClientChunkReceiveSystem so the readiness tag is visible to its
/// reception gate the same frame it's set.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateBefore(typeof(ClientChunkReceiveSystem))]
public partial class ClientContentManifestSystem : SystemBase
{
    // Staging for the in-flight manifest. A single manifest per connection.
    readonly Dictionary<ushort, string> _staging = new();
    int _received;
    int _expected = -1;
    bool _adopted;

    protected override void OnCreate()
    {
        RequireForUpdate<NetworkStreamInGame>();
    }

    protected override void OnUpdate()
    {
        var ecb = new EntityCommandBuffer(Allocator.Temp);

        // ── 1. Send the request once per (re)connection ────────────────────────
        foreach (var (_, connectionEntity) in
            SystemAPI.Query<RefRO<NetworkId>>()
                     .WithAll<NetworkStreamInGame>()
                     .WithNone<ContentManifestRequested>()
                     .WithEntityAccess())
        {
            ResetStaging(); // fresh handshake (covers reconnects)

            var req = ecb.CreateEntity();
            ecb.AddComponent(req, new ContentManifestRequestRpc());
            ecb.AddComponent(req, new SendRpcCommandRequest { TargetConnection = connectionEntity });

            ecb.AddComponent<ContentManifestRequested>(connectionEntity);
        }

        // ── 2. Collect entries ─────────────────────────────────────────────────
        foreach (var (entry, rpcEntity) in
            SystemAPI.Query<RefRO<ContentManifestEntryRpc>>()
                     .WithAll<ReceiveRpcCommandRequest>()
                     .WithEntityAccess())
        {
            ushort nid = entry.ValueRO.NumericId;
            if (!_staging.ContainsKey(nid))
            {
                _staging[nid] = entry.ValueRO.StringId.ToString();
                _received++;
            }
            ecb.DestroyEntity(rpcEntity);
        }

        // ── 3. Read the completion marker ──────────────────────────────────────
        foreach (var (complete, rpcEntity) in
            SystemAPI.Query<RefRO<ContentManifestCompleteRpc>>()
                     .WithAll<ReceiveRpcCommandRequest>()
                     .WithEntityAccess())
        {
            _expected = complete.ValueRO.Count;
            ecb.DestroyEntity(rpcEntity);
        }

        ecb.Playback(EntityManager);
        ecb.Dispose();

        // ── 4. Adopt once complete (marker seen AND all entries present) ───────
        if (!_adopted && _expected > 0 && _received >= _expected)
            TryAdopt();
    }

    void TryAdopt()
    {
        var order = new List<string>(_expected);
        for (int i = 0; i < _expected; i++)
        {
            if (!_staging.TryGetValue((ushort)i, out string name))
            {
                // Gap despite the count matching — a dropped/duplicate id. Bail and
                // wait; reliable delivery should fill it on a subsequent frame.
                return;
            }
            order.Add(name);
        }

        BlockRegistry.InitializeFromManifest(order);
        _adopted = true;

        // Tag every in-game connection ready so ClientChunkReceiveSystem proceeds.
        var ecb = new EntityCommandBuffer(Allocator.Temp);
        foreach (var (_, connectionEntity) in
            SystemAPI.Query<RefRO<NetworkId>>()
                     .WithAll<NetworkStreamInGame>()
                     .WithNone<ContentManifestReady>()
                     .WithEntityAccess())
        {
            ecb.AddComponent<ContentManifestReady>(connectionEntity);
        }
        ecb.Playback(EntityManager);
        ecb.Dispose();

        Debug.Log($"[ClientContentManifest] Adopted server manifest ({_expected} ids). World reception unlocked.");
    }

    void ResetStaging()
    {
        _staging.Clear();
        _received = 0;
        _expected = -1;
        _adopted = false;
    }
}