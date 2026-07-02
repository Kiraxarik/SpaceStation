using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Client-only. Loads and caches every AnimationClip a model declares (via
/// ModelContent.AnimationPaths), keyed by model id, on first request. Mirrors
/// ModelMeshCache/TileAtlasBaker's "load once, cache" shape and the same
/// play-session reset need (cached managed objects don't survive domain-reload-
/// disabled play sessions cleanly — see TileAtlasBaker's remarks for the general
/// pattern this follows).
///
/// v1 SCOPE: a model with animation files gets ONE clip picked to drive it — the
/// first clip found across its declared animation files, in declaration order.
/// There's no clip-selection (idle/walk/attack) or blending yet; that's state-
/// machine work for later (Phase 4 roles/animation direction). This exists to
/// prove the bone-hierarchy + keyframe-sampling path end-to-end with a single
/// test clip, per the "quick test animation" ask — ModelAnimationSystem reads
/// through GetDefaultClip below, so swapping in real clip-selection later only
/// touches this one lookup, not the sampling system itself.
/// </summary>
public static class AnimationClipRegistry
{
    static readonly Dictionary<string, Dictionary<string, AnimationClip>> _clipsByModel =
        new(StringComparer.Ordinal);
    static readonly Dictionary<string, AnimationClip> _defaultClipByModel =
        new(StringComparer.Ordinal);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetForPlaySession()
    {
        _clipsByModel.Clear();
        _defaultClipByModel.Clear();
    }

    /// <summary>The clip driving a model's bones, or null if the model has no
    /// animation files (a purely static skeleton — bones held at bind pose) or
    /// none of its files parsed to a usable clip.</summary>
    public static AnimationClip GetDefaultClip(string modelId)
    {
        if (_defaultClipByModel.TryGetValue(modelId, out var cached)) return cached;

        var built = Build(modelId);
        _defaultClipByModel[modelId] = built; // null cached too — avoids re-parsing a model with no usable clip every frame
        return built;
    }

    static AnimationClip Build(string modelId)
    {
        var content = ModelRegistry.Get(modelId);
        if (content == null || content.AnimationPaths == null || content.AnimationPaths.Length == 0)
            return null;

        var merged = new Dictionary<string, AnimationClip>(StringComparer.Ordinal);
        foreach (var path in content.AnimationPaths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            var clips = BedrockAnimationParser.Load(path, modelId);
            foreach (var kv in clips)
                merged[kv.Key] = kv.Value; // later file wins on a duplicate clip id, matches mod-order-wins elsewhere
        }
        _clipsByModel[modelId] = merged;

        foreach (var clip in merged.Values)
            return clip; // first one, declaration order (Dictionary preserves insertion order in practice, not guaranteed by spec — acceptable for v1's single-clip-per-model scope)

        Debug.LogWarning($"[AnimationClipRegistry] '{modelId}': animation file(s) declared but no usable clip parsed.");
        return null;
    }
}