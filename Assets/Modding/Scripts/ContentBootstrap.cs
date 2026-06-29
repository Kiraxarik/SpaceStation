using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The single content load entry point. Runs once per process before any scene or
/// ECS world, replacing the old per-registry AutoLoad hooks. It:
///   1. Discovers and resolves the mod set into a deterministic load order
///      (ModPackageLoader — dependencies, versions, api gate).
///   2. Loads each mod's blocks, models, and sounds in that order, so override
///      precedence is determined by declared dependencies, not filesystem order.
///   3. Builds BlockRegistry (dense tile ids), ModelRegistry and SoundRegistry
///      (sparse asset content).
///   4. Builds the AssetManifest (per-file + per-mod hashes) for the integrity /
///      distribution handshake.
///
/// Both registries and the asset manifest are built from the locally-resolved set
/// on every process. The server's set is authoritative; a connecting client
/// re-numbers its blocks against the server manifest at handshake
/// (BlockRegistry.InitializeFromManifest) and diffs asset hashes to find downloads.
///
/// The base game is mod "base" (a StreamingAssets/Mods/base/ folder with mod.json +
/// blocks.json + models.json + sounds.json + tiles.json) — it loads through exactly this path.
/// </summary>
public static class ContentBootstrap
{
    /// <summary>Most recent mod resolution (load order + diagnostics).</summary>
    public static ModResolution LastResolution { get; private set; }

    /// <summary>Hash manifest of all loaded content, for the distribution handshake.</summary>
    public static AssetManifest AssetManifest { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot()
    {
        var resolution = ModPackageLoader.DiscoverAndResolve();
        LastResolution = resolution;

        // Load content from every cleanly-resolved mod, in dependency order.
        var allBlocks = new List<BlockDefinitionData>();
        var allModels = new List<ModelContent>();
        var allSounds = new List<SoundContent>();
        var allTiles = new List<TileContent>();

        foreach (var pkg in resolution.Order)
        {
            allBlocks.AddRange(BlockContentLoader.LoadFromMod(pkg.Directory, pkg.Id));
            allModels.AddRange(ModelContentLoader.LoadFromMod(pkg.Directory, pkg.Id));
            allSounds.AddRange(SoundContentLoader.LoadFromMod(pkg.Directory, pkg.Id));
            allTiles.AddRange(TileContentLoader.LoadFromMod(pkg.Directory, pkg.Id));
        }

        BlockRegistry.BuildLocal(allBlocks);
        ModelRegistry.Initialize(allModels);
        SoundRegistry.Initialize(allSounds);
        TileRegistry.Initialize(allTiles);

        // Hash the loaded content for the integrity / distribution handshake.
        // Built after the registries so referenced asset files are known.
        AssetManifest = AssetManifestBuilder.Build(resolution);
        LogAssetManifest(AssetManifest);

        if (resolution.Order.Count == 0)
            Debug.LogError("[ContentBootstrap] No mods loaded. Is StreamingAssets/Mods/base/ present " +
                           "with a mod.json? The world will have no content beyond air.");
    }

    static void LogAssetManifest(AssetManifest manifest)
    {
        foreach (var mod in manifest.Mods)
            Debug.Log($"[ContentBootstrap] Asset hash — {mod.ModId} {mod.Version}: " +
                      $"{mod.Files.Count} file(s), mod hash {Short(mod.ModHash)}.");
    }

    static string Short(string hash) => string.IsNullOrEmpty(hash) ? "?" : hash.Substring(0, System.Math.Min(8, hash.Length));
}