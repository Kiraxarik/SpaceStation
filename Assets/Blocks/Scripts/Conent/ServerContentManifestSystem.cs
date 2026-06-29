using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

/// <summary>
/// Answers ContentManifestRequestRpc by streaming the server's authoritative
/// content manifest to the requesting connection: one ContentManifestEntryRpc
/// per tile id, then a ContentManifestCompleteRpc.
///
/// The manifest is small (≤256 tile ids), so it's sent as individual reliable
/// RPCs in one pass — no fragmentation codec needed. If manifests ever grow large
/// enough to stress the reliable pipeline in a single frame, pace the sends or
/// pack entries into a fragmented blob; not a concern at base-game scale.
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
        state.RequireForUpdate<NetworkStreamInGame>();
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
                    NumericId = (byte)i,
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