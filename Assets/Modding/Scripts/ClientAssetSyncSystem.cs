using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

/// <summary>
/// Client side of asset distribution (architecture §7.4). Runs after the block
/// manifest is adopted (ContentManifestReady) and before anything that needs
/// complete local content — world/chunk reception (ClientChunkReceiveSystem)
/// and lobby entry (LobbyUIController) both gate on AssetSyncReady, which this
/// system is the sole owner of.
///
/// Sequence, matching the Round Lifecycle doc's SYNC step (§1.B):
///   1. Send AssetManifestRequestRpc once.
///   2. Collect AssetManifestFileEntryRpc into a flat list until
///      AssetManifestCompleteRpc's count is met, then diff against the
///      client's own local manifest (ContentBootstrap.AssetManifest).
///   3. If identical, done immediately. Otherwise send one RequestAssetFileRpc
///      per NeededFile and reassemble the returned AssetFileFragmentRpc stream
///      per (ModId, RelativePath), writing each completed file to disk under
///      StreamingAssets/Mods/&lt;modId&gt;/&lt;relativePath&gt; — the exact
///      location ModPackageLoader.Discover already scans, so a mod that was
///      entirely absent (including its mod.json) is picked up on reload the
///      same as a hand-installed one, and a mod missing only a few files gets
///      them filled in in place.
///   4. Once every needed file has arrived, call ContentBootstrap.Reload() to
///      re-run discovery + content loading, then re-apply the already-adopted
///      server block-id ordering (BlockRegistry.Manifest.Order, captured
///      before the reload) since Reload() resets BlockRegistry to a fresh
///      local ordering. Tag the connection AssetSyncReady.
///
/// Writing into StreamingAssets at runtime works because the current target is
/// Windows Standalone, where StreamingAssets is a plain writable folder next to
/// the exe. This would need a separate writable cache + path-resolution change
/// (rather than writing into StreamingAssets directly) if this ever ships to a
/// platform where StreamingAssets is packed/read-only (mobile, WebGL) or is
/// integrity-checked (a Steam depot verifying local files against depot
/// manifests would flag runtime-written files here).
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateAfter(typeof(ClientContentManifestSystem))]
[UpdateBefore(typeof(ClientChunkReceiveSystem))]
public partial class ClientAssetSyncSystem : SystemBase
{
    // ── Manifest collection ──────────────────────────────────────────────────
    readonly List<(string ModId, string RelativePath, string Hash, int Size)> _serverFiles = new();
    int _expectedFileCount = -1;
    bool _diffed;

    // ── Download tracking ─────────────────────────────────────────────────────
    readonly HashSet<(string ModId, string RelativePath)> _pending = new();
    int _neededTotal;
    int _completedFiles;
    bool _syncComplete;

    Entity _connection;

    // ── Fragment reassembly ───────────────────────────────────────────────────
    sealed class FileAssembly
    {
        public byte[] Data;
        public bool[] Got;
        public int Received;
        public int Expected = -1; // FragmentCount, set from the first fragment seen
    }
    readonly Dictionary<(string ModId, string RelativePath), FileAssembly> _assembling = new();

    protected override void OnCreate()
    {
        RequireForUpdate<NetworkStreamInGame>();
    }

    protected override void OnUpdate()
    {
        var ecb = new EntityCommandBuffer(Allocator.Temp);

        // ── 1. Send the request once, gated on the block manifest already
        //       being adopted ──────────────────────────────────────────────────
        foreach (var (_, connectionEntity) in
            SystemAPI.Query<RefRO<NetworkId>>()
                     .WithAll<NetworkStreamInGame, ContentManifestReady>()
                     .WithNone<AssetManifestRequested>()
                     .WithEntityAccess())
        {
            ResetStaging(); // fresh handshake (covers reconnects)
            _connection = connectionEntity;

            var req = ecb.CreateEntity();
            ecb.AddComponent(req, new AssetManifestRequestRpc());
            ecb.AddComponent(req, new SendRpcCommandRequest { TargetConnection = connectionEntity });

            ecb.AddComponent<AssetManifestRequested>(connectionEntity);
        }

        // ── 2. Collect manifest entries ─────────────────────────────────────────
        foreach (var (entry, rpcEntity) in
            SystemAPI.Query<RefRO<AssetManifestFileEntryRpc>>()
                     .WithAll<ReceiveRpcCommandRequest>()
                     .WithEntityAccess())
        {
            _serverFiles.Add((
                entry.ValueRO.ModId.ToString(),
                entry.ValueRO.RelativePath.ToString(),
                entry.ValueRO.Hash.ToString(),
                entry.ValueRO.Size));
            ecb.DestroyEntity(rpcEntity);
        }

        // ── 3. Completion marker ─────────────────────────────────────────────
        foreach (var (complete, rpcEntity) in
            SystemAPI.Query<RefRO<AssetManifestCompleteRpc>>()
                     .WithAll<ReceiveRpcCommandRequest>()
                     .WithEntityAccess())
        {
            _expectedFileCount = complete.ValueRO.Count;
            ecb.DestroyEntity(rpcEntity);
        }

        // ── 4. Receive file fragments ────────────────────────────────────────
        foreach (var (frag, rpcEntity) in
            SystemAPI.Query<RefRO<AssetFileFragmentRpc>>()
                     .WithAll<ReceiveRpcCommandRequest>()
                     .WithEntityAccess())
        {
            AccumulateFragment(frag.ValueRO);
            ecb.DestroyEntity(rpcEntity);
        }

        ecb.Playback(EntityManager);
        ecb.Dispose();

        // ── 5. Diff once the manifest is fully collected ────────────────────
        if (!_diffed && _expectedFileCount >= 0 && _serverFiles.Count >= _expectedFileCount)
            RunDiff();

        // ── 6. Finish once every needed file has arrived ────────────────────
        if (_diffed && !_syncComplete && _neededTotal > 0 && _completedFiles >= _neededTotal)
            FinishSync();
    }

    // ── Diff ───────────────────────────────────────────────────────────────────

    void RunDiff()
    {
        _diffed = true;

        var server = BuildServerManifest();
        var local = ContentBootstrap.AssetManifest;
        var diff = AssetManifest.Diff(server, local);

        if (diff.Identical)
        {
            Debug.Log("[ClientAssetSync] Local content matches server manifest — no downloads needed.");
            MarkReady();
            return;
        }

        _neededTotal = diff.Needed.Count;
        _completedFiles = 0;

        Debug.Log($"[ClientAssetSync] Missing/stale {diff.Needed.Count} file(s), " +
                  $"{diff.TotalBytes} byte(s) total. Requesting downloads.");

        var ecb = new EntityCommandBuffer(Allocator.Temp);
        foreach (var needed in diff.Needed)
        {
            _pending.Add((needed.ModId, needed.RelativePath));

            var req = ecb.CreateEntity();
            ecb.AddComponent(req, new RequestAssetFileRpc
            {
                ModId = AssetFragmentCodec.ToFixed64(needed.ModId),
                RelativePath = AssetFragmentCodec.ToFixed128(needed.RelativePath),
            });
            ecb.AddComponent(req, new SendRpcCommandRequest { TargetConnection = _connection });
        }
        ecb.Playback(EntityManager);
        ecb.Dispose();
    }

    AssetManifest BuildServerManifest()
    {
        var manifest = new AssetManifest();
        var byMod = new Dictionary<string, ModAssetEntry>(System.StringComparer.Ordinal);

        foreach (var f in _serverFiles)
        {
            if (!byMod.TryGetValue(f.ModId, out var entry))
            {
                // ModHash is intentionally left blank: the rollup hash isn't sent
                // over the wire (would need per-mod file lists sorted client-side
                // to reproduce it), so Diff() always falls through to per-file
                // comparison instead of taking the "whole mod matches" fast path.
                // Correct either way — this only skips an optimization that
                // doesn't matter at the current file-count scale.
                entry = new ModAssetEntry { ModId = f.ModId };
                byMod[f.ModId] = entry;
                manifest.Mods.Add(entry);
            }
            entry.Files.Add(new FileAssetEntry
            {
                RelativePath = f.RelativePath,
                Hash = f.Hash,
                Size = f.Size,
            });
        }

        return manifest;
    }

    // ── Fragment reassembly ───────────────────────────────────────────────────

    void AccumulateFragment(in AssetFileFragmentRpc frag)
    {
        string modId = frag.ModId.ToString();
        string relativePath = frag.RelativePath.ToString();
        var key = (modId, relativePath);

        if (!_assembling.TryGetValue(key, out var asm))
        {
            asm = new FileAssembly
            {
                Data = new byte[frag.TotalBytes],
                Got = new bool[frag.FragmentCount],
                Expected = frag.FragmentCount,
            };
            _assembling[key] = asm;
        }

        if (frag.FragmentIndex < asm.Got.Length && !asm.Got[frag.FragmentIndex])
        {
            AssetFragmentCodec.Scatter(frag, asm.Data);
            asm.Got[frag.FragmentIndex] = true;
            asm.Received++;
        }

        if (asm.Received >= asm.Expected)
        {
            WriteFile(modId, relativePath, asm.Data);
            _assembling.Remove(key);

            if (_pending.Remove(key))
                _completedFiles++;
        }
    }

    static void WriteFile(string modId, string relativePath, byte[] data)
    {
        string fullPath = Path.Combine(Application.streamingAssetsPath, "Mods", modId, relativePath);
        string dir = Path.GetDirectoryName(fullPath);

        try
        {
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(fullPath, data);
            Debug.Log($"[ClientAssetSync] Wrote '{modId}/{relativePath}' ({data.Length}B) to '{fullPath}'.");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[ClientAssetSync] Failed writing '{fullPath}': {e.Message}");
        }
    }

    // ── Finish ─────────────────────────────────────────────────────────────────

    void FinishSync()
    {
        // BlockRegistry.Manifest.Order is the server ordering ContentManifestReady
        // already adopted. Reload() rebuilds every registry from scratch off the
        // (now more complete) mod set, which resets BlockRegistry to a fresh LOCAL
        // ordering — so the server ordering has to be re-applied afterward, or
        // block ids silently drift back out of sync with the server.
        var previousOrder = new List<string>(BlockRegistry.Manifest.Order);

        ContentBootstrap.Reload();
        BlockRegistry.InitializeFromManifest(previousOrder);

        Debug.Log($"[ClientAssetSync] {_completedFiles} file(s) downloaded and content reloaded.");

        InvalidateRenderedContent();
        MarkReady();
    }

    /// <summary>
    /// Forces a full re-bake/re-mesh of everything that could have gone stale from
    /// the reload above. TileAtlasBaker and both mesh systems each cache their own
    /// atlas state and only ever build it once per session — without this, a
    /// tile/model that downloaded successfully and now has real BlockRegistry data
    /// would still render as the placeholder, because the GPU array was already
    /// baked (and possibly meshes already built) before the download landed.
    ///   1. Re-bake the Texture2DArray from the now-complete TileRegistry.
    ///   2. Invalidate both mesh systems' cached slice-index arrays (and rebind
    ///      their material to the freshly-baked array) so the next mesh build
    ///      reads correct indices instead of the stale cached ones.
    ///   3. Re-dirty every existing chunk entity so it actually re-meshes with the
    ///      corrected atlas/indices — chunks meshed before this point (e.g. from a
    ///      locally-streamed subscene at boot, before any of this ran) would
    ///      otherwise sit there showing the old bake forever.
    /// </summary>
    void InvalidateRenderedContent()
    {
        TileAtlasBaker.InvalidateAndRebake();

        World.GetExistingSystemManaged<ChunkMeshSystem>()?.InvalidateAtlasCache();
        World.GetExistingSystemManaged<ChunkLODMeshSystem>()?.InvalidateAtlasCache();

        using var staleChunks = EntityManager.CreateEntityQuery(
            ComponentType.ReadOnly<ChunkPosition>(),
            ComponentType.Exclude<ChunkDirty>());
        EntityManager.AddComponent<ChunkDirty>(staleChunks);

        Debug.Log("[ClientAssetSync] Invalidated tile atlas and re-dirtied existing chunks for remesh.");
    }

    void MarkReady()
    {
        _syncComplete = true;

        // _connection was captured in OnUpdate when the manifest request was
        // sent (step 1) — always set by the time either code path (identical /
        // downloaded) reaches here. Tagging directly avoids re-querying.
        if (_connection != Entity.Null && !EntityManager.HasComponent<AssetSyncReady>(_connection))
            EntityManager.AddComponent<AssetSyncReady>(_connection);

        Debug.Log("[ClientAssetSync] Asset sync ready. World reception and lobby entry unlocked.");
    }

    void ResetStaging()
    {
        _serverFiles.Clear();
        _expectedFileCount = -1;
        _diffed = false;
        _pending.Clear();
        _neededTotal = 0;
        _completedFiles = 0;
        _syncComplete = false;
        _assembling.Clear();
    }
}