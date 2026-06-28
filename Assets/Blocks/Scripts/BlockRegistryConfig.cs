using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// ScriptableObject that holds a reference to the base-game blocks.json and
/// triggers BlockRegistry initialization before any scene loads.
///
/// Setup:
///   1. Create the asset: Assets → Create → SpaceStation → Block Registry Config
///   2. Assign your blocks.json TextAsset to the Base Blocks File field
///   3. That's it — the registry loads automatically on play/build
///
/// The asset must live in a Resources/ folder (any depth) so Unity can find
/// it at runtime via Resources.Load. Suggested location:
///   Assets/Blocks/Resources/BlockRegistryConfig.asset
/// Your blocks.json can stay anywhere (Assets/Blocks/Define/blocks.json).
/// </summary>
[CreateAssetMenu(
    fileName = "BlockRegistryConfig",
    menuName = "SpaceStation/Block Registry Config")]
public class BlockRegistryConfig : ScriptableObject
{
    [Tooltip("The base-game blocks.json file. Can live anywhere in the project.")]
    public TextAsset BaseBlocksFile;

    // ── Auto-load ─────────────────────────────────────────────────────────────

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void AutoLoad()
    {
        // Find the config asset from any Resources/ folder in the project.
        var config = Resources.Load<BlockRegistryConfig>("BlockRegistryConfig");

        if (config == null)
        {
            Debug.LogError(
                "[BlockRegistry] BlockRegistryConfig.asset not found in any Resources/ folder. " +
                "Create it via Assets → Create → SpaceStation → Block Registry Config " +
                "and place it under a Resources/ folder (e.g. Assets/Blocks/Resources/).");
            return;
        }

        BlockRegistry.Initialize(config);
    }
}