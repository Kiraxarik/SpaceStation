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
///
/// Both registries are built from the locally-resolved set on every process. The
/// server's set is authoritative; a connecting client re-numbers its blocks against
/// the server manifest at handshake (BlockRegistry.InitializeFromManifest).
///
/// The base game is mod "base" (a StreamingAssets/Mods/base/ folder with mod.json +
/// blocks.json + models.json + sounds.json) — it loads through exactly this path,
/// no special case.
/// </summary>
public static class ContentBootstrap
{
    /// <summary>
    /// The most recent mod resolution. Exposed so a later server policy system can
    /// inspect Errors and refuse connections when required content failed to load.
    /// </summary>
    public static ModResolution LastResolution { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot()
    {
        var resolution = ModPackageLoader.DiscoverAndResolve();
        LastResolution = resolution;

        // Load content from every cleanly-resolved mod, in dependency order.
        // (resolution.Order already excludes mods that failed to resolve.)
        var allBlocks = new List<BlockDefinitionData>();
        var allModels = new List<ModelContent>();
        var allSounds = new List<SoundContent>();

        foreach (var pkg in resolution.Order)
        {
            allBlocks.AddRange(BlockContentLoader.LoadFromMod(pkg.Directory, pkg.Id));
            allModels.AddRange(ModelContentLoader.LoadFromMod(pkg.Directory, pkg.Id));
            allSounds.AddRange(SoundContentLoader.LoadFromMod(pkg.Directory, pkg.Id));
        }

        BlockRegistry.BuildLocal(allBlocks);
        ModelRegistry.Initialize(allModels);
        SoundRegistry.Initialize(allSounds);

        if (resolution.Order.Count == 0)
            Debug.LogError("[ContentBootstrap] No mods loaded. Is StreamingAssets/Mods/base/ present " +
                           "with a mod.json? The world will have no content beyond air.");
    }
}