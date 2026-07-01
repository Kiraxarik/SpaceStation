using System;
using System.IO;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

/// <summary>
/// Server side of asset distribution (architecture §7.4). Two responsibilities:
///
///   1. Answers AssetManifestRequestRpc by streaming ContentBootstrap's hash
///      manifest — one AssetManifestFileEntryRpc per shippable file across
///      every loaded mod, then AssetManifestCompleteRpc. Mirrors
///      ServerContentManifestSystem, one layer up (files, not tile ids).
///
///   2. Answers RequestAssetFileRpc by reading the requested file from disk and
///      streaming it back as AssetFileFragmentRpc pieces.
///
/// The requested (ModId, RelativePath) in step 2 is validated against the
/// server's own AssetManifest by exact match before any filesystem read. A
/// client can only ever ask for a path that is already a known, hashed,
/// shippable file — there is no client-supplied string that reaches
/// File.ReadAllBytes without first being found verbatim in a list built from
/// files the server itself enumerated. No path-traversal surface.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
public partial struct ServerAssetSyncSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        // NOT NetworkStreamInGame: server-side, that tag is only added by
        // PlayerSpawnServerSystem once RoundPhase.Running ∧ Committed — i.e.
        // only after the player has already clicked Ready. Gating this system
        // on it would mean the manifest handshake can never run at all, since
        // reaching Committed requires getting through the lobby first. NetworkId
        // is present the instant the connection is established, both sides,
        // which is all this system actually needs to answer RPCs.
        state.RequireForUpdate<NetworkId>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var ecb = new EntityCommandBuffer(Allocator.Temp);

        // ── 1. Manifest streaming ────────────────────────────────────────────
        var manifest = ContentBootstrap.AssetManifest;

        foreach (var (_, source, requestEntity) in
            SystemAPI.Query<RefRO<AssetManifestRequestRpc>, RefRO<ReceiveRpcCommandRequest>>()
                     .WithEntityAccess())
        {
            if (manifest == null)
            {
                Debug.LogError("[ServerAssetSync] ContentBootstrap.AssetManifest is null; deferring request.");
                continue; // leave the request for a later frame
            }

            Entity connection = source.ValueRO.SourceConnection;
            int total = 0;

            foreach (var mod in manifest.Mods)
            {
                foreach (var file in mod.Files)
                {
                    var entry = ecb.CreateEntity();
                    ecb.AddComponent(entry, new AssetManifestFileEntryRpc
                    {
                        ModId = AssetFragmentCodec.ToFixed64(mod.ModId),
                        RelativePath = AssetFragmentCodec.ToFixed128(file.RelativePath),
                        Hash = AssetFragmentCodec.ToFixed128(file.Hash),
                        Size = (int)file.Size,
                    });
                    ecb.AddComponent(entry, new SendRpcCommandRequest { TargetConnection = connection });
                    total++;
                }
            }

            var done = ecb.CreateEntity();
            ecb.AddComponent(done, new AssetManifestCompleteRpc { Count = total });
            ecb.AddComponent(done, new SendRpcCommandRequest { TargetConnection = connection });

            ecb.DestroyEntity(requestEntity);

            Debug.Log($"[ServerAssetSync] Sent asset manifest ({total} file(s) across " +
                      $"{manifest.Mods.Count} mod(s)) to connection {connection.Index}.");
        }

        // ── 2. File requests ─────────────────────────────────────────────────
        foreach (var (req, source, requestEntity) in
            SystemAPI.Query<RefRO<RequestAssetFileRpc>, RefRO<ReceiveRpcCommandRequest>>()
                     .WithEntityAccess())
        {
            Entity connection = source.ValueRO.SourceConnection;
            string modId = req.ValueRO.ModId.ToString();
            string relativePath = req.ValueRO.RelativePath.ToString();

            ecb.DestroyEntity(requestEntity);

            byte[] bytes = ResolveAndRead(modId, relativePath);
            if (bytes == null)
            {
                Debug.LogError($"[ServerAssetSync] Rejected file request '{modId}/{relativePath}' " +
                                $"from connection {connection.Index} — not a known shippable file.");
                continue;
            }

            int count = AssetFragmentCodec.FragmentCountFor(bytes.Length);
            for (int f = 0; f < count; f++)
            {
                var rpc = AssetFragmentCodec.Build(modId, relativePath, bytes, f);
                var e = ecb.CreateEntity();
                ecb.AddComponent(e, rpc);
                ecb.AddComponent(e, new SendRpcCommandRequest { TargetConnection = connection });
            }

            Debug.Log($"[ServerAssetSync] Sent '{modId}/{relativePath}' ({bytes.Length}B, " +
                      $"{count} fragment(s)) to connection {connection.Index}.");
        }

        ecb.Playback(state.EntityManager);
        ecb.Dispose();
    }

    /// <summary>
    /// Validates the request against the server's own AssetManifest (exact
    /// match required) before touching the filesystem, then reads the file
    /// from the mod's real directory. Returns null on any failure — unknown
    /// mod, unknown file, or a read error — so the caller rejects cleanly.
    /// </summary>
    static byte[] ResolveAndRead(string modId, string relativePath)
    {
        var manifest = ContentBootstrap.AssetManifest;
        var modEntry = manifest?.FindMod(modId);
        if (modEntry == null) return null;

        FileAssetEntry fileEntry = modEntry.Files.Find(f =>
            string.Equals(f.RelativePath, relativePath, StringComparison.Ordinal));
        if (fileEntry == null) return null;

        string modDir = ContentBootstrap.FindModDirectory(modId);
        if (modDir == null) return null;

        string fullPath = Path.Combine(modDir, relativePath);
        if (!File.Exists(fullPath)) return null;

        try { return File.ReadAllBytes(fullPath); }
        catch (Exception e)
        {
            Debug.LogError($"[ServerAssetSync] Failed reading '{fullPath}': {e.Message}");
            return null;
        }
    }
}