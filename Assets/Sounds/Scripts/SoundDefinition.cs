using System;
using System.Collections.Generic;

/// <summary>
/// JSON-serializable definition for one logical sound — the third asset-mod
/// category (architecture §1.6: textures, meshes, sounds). Authored in a mod's
/// sounds.json. Like models, a sound is sparse, string-keyed content referenced
/// by its namespaced id ("base:footstep_metal"), never a tile byte.
///
/// A logical sound maps to one or MORE clip files. When more than one is given the
/// runtime picks among them at random, so repeated plays (footsteps, hits) don't
/// feel mechanical. All listed clips are part of the asset footprint and ship.
///
/// The fields here are playback PARAMETERS, not runtime state — the audio runtime
/// (later, consumer-driven) reads them when it plays the sound. Streaming-vs-memory
/// is deliberately omitted; that's a runtime concern, added when the player lands.
///
/// Example — sounds.json entry:
/// {
///   "id":        "footstep_metal",
///   "clips":     [ "sounds/step_metal_1.ogg", "sounds/step_metal_2.ogg" ],
///   "volume":    0.8,
///   "pitch_min": 0.95,
///   "pitch_max": 1.05,
///   "loop":      false,
///   "spatial":   true,
///   "min_distance": 1.0,
///   "max_distance": 16.0
/// }
/// </summary>
[Serializable]
public class SoundDefinitionData
{
    /// <summary>Bare id ("footstep_metal"); namespaced by source → "base:footstep_metal".</summary>
    public string id = "";

    /// <summary>Relative path(s) to clip file(s). More than one → random selection per play.</summary>
    public string[] clips = Array.Empty<string>();

    /// <summary>Base linear volume multiplier (0..1).</summary>
    public float volume = 1f;

    /// <summary>Random pitch range per play. Equal values = no variation.</summary>
    public float pitch_min = 1f;
    public float pitch_max = 1f;

    /// <summary>Whether the sound loops while active (ambience, machine hum).</summary>
    public bool loop = false;

    /// <summary>True = positional/3D (attenuates with distance); false = 2D UI/global.</summary>
    public bool spatial = true;

    /// <summary>3D attenuation distances (world units). Ignored when spatial is false.</summary>
    public float min_distance = 1f;
    public float max_distance = 32f;
}

/// <summary>
/// A loaded, resolved sound: namespaced id, source mod, playback params, and the
/// absolute on-disk paths of every clip it ships. The asset pipeline consumes
/// AssetFiles (Module 2 hashes, Module 5 distributes); the handshake agrees on Id.
/// Holds no decoded audio — that's the runtime's job, later.
/// </summary>
public sealed class SoundContent
{
    public string Id;             // namespaced, e.g. "base:footstep_metal"
    public string SourceMod;

    public string[] ClipPaths;    // absolute

    public float Volume;
    public float PitchMin;
    public float PitchMax;
    public bool Loop;
    public bool Spatial;
    public float MinDistance;
    public float MaxDistance;

    /// <summary>Every file this sound ships, absolute paths — the asset footprint.</summary>
    public IEnumerable<string> AssetFiles
    {
        get
        {
            foreach (var c in ClipPaths) yield return c;
        }
    }
}