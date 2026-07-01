using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

/// <summary>
/// Answers ContentManifestRequestRpc by streaming the server's authoritative
/// content manifest to the requesting connection: one ContentManifestEntryRpc
/// per tile id, then a ContentManifestCompleteRpc.
///
/// The manifest is sent as individual reliable RPCs in one pass — no
/// fragmentation codec needed. Up to 65536 entries (§1.5, ushort-bounded) is a
/// lot of individual RPCs if a modpack ever gets that large; pace the sends or
/// pack entries into a fragmented blob if the reliable pipeline ever visibly
/// stresses under one frame's worth. Not a concern at current content scale.
///
/// BlockRegistry.Manifest is populated at server startup (AutoLoad,
/// BeforeSceneLoad), so it's available well before any client can request it; the
/// null-guard only covers a pathological "registry failed to load" case by
/// leaving the request to retry.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
public partial struct ServerContentManifestSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        // NOT NetworkStreamInGame: server-side, that tag is only added by
        // PlayerSpawnServerSystem once RoundPhase.Running ∧ Committed — i.e.
        // only after the player has already clicked Ready. Gating this system
        // on it meant the manifest handshake could never run at all, since
        // reaching Committed requires getting through the lobby first, which
        // (once anything downstream depends on the handshake finishing) can
        // never happen. NetworkId is present the instant the connection is
        // established, both sides, which is all this system actually needs.
        state.RequireForUpdate<NetworkId>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var manifest = BlockRegistry.Manifest;
        var ecb = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (_, source, requestEntity) in
            SystemAPI.Query<RefRO<ContentManifestRequestRpc>, RefRO<ReceiveRpcCommandRequest>>()
                     .WithEntityAccess())
        {
            if (manifest == null)
            {
                Debug.LogError("[ServerContentManifest] BlockRegistry.Manifest is null; " +
                               "deferring manifest send. Did the server registry fail to load?");
                continue; // leave the request for a later frame
            }

            Entity connection = source.ValueRO.SourceConnection;
            var order = manifest.Order;

            for (int i = 0; i < order.Count; i++)
            {
                var entry = ecb.CreateEntity();
                ecb.AddComponent(entry, new ContentManifestEntryRpc
                {
                    NumericId = (ushort)i,
                    StringId = ToFixed(order[i]),
                });
                ecb.AddComponent(entry, new SendRpcCommandRequest { TargetConnection = connection });
            }

            var done = ecb.CreateEntity();
            ecb.AddComponent(done, new ContentManifestCompleteRpc { Count = order.Count });
            ecb.AddComponent(done, new SendRpcCommandRequest { TargetConnection = connection });

            ecb.DestroyEntity(requestEntity);

            Debug.Log($"[ServerContentManifest] Sent manifest ({order.Count} ids) to connection {connection.Index}.");
        }

        ecb.Playback(state.EntityManager);
        ecb.Dispose();
    }

    /// <summary>Safe string → FixedString128Bytes with a loud error on overflow.</summary>
    static FixedString128Bytes ToFixed(string s)
    {
        FixedString128Bytes fs = default;
        if (fs.CopyFrom(s) != CopyError.None)
        {
            Debug.LogError($"[ServerContentManifest] Content id '{s}' exceeds the 125-byte wire limit; " +
                           "truncating. Clients will mismatch — shorten this id.");
            fs = default;
            fs.CopyFromTruncated(s);
        }
        return fs;
    }
}