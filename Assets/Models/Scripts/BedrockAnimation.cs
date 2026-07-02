using System.Collections.Generic;
using Unity.Mathematics;

/// <summary>
/// One rotation keyframe on a bone's channel. Time is seconds from clip start;
/// EulerDegrees matches the Blockbench/Bedrock authoring convention (same units
/// BlockbenchGeometryParser reads for bone.rotation) — converted to radians only
/// at the point of use (ModelAnimationSystem), not here, so this stays a plain
/// data mirror of the file.
/// </summary>
public struct AnimKeyframe
{
    public float Time;
    public float3 EulerDegrees;
}

/// <summary>
/// One bone's animation channel within a clip. v1 supports rotation only —
/// position/scale channels exist in the Bedrock format but aren't read yet
/// (TODO: needed once a clip wants to slide a bone, not just rotate it; no
/// current test asset needs it). Keyframes are stored sorted by Time so sampling
/// is a linear scan without a sort step at playback time.
/// </summary>
public sealed class BoneAnimationChannel
{
    public List<AnimKeyframe> Rotation = new();
}

/// <summary>
/// One parsed named animation ("animation.model_id.clip_name" in the file) —
/// Bedrock's own clip-naming convention, kept as authored rather than
/// reinterpreted. Loop/Length come straight from the file's "loop" and
/// "animation_length" fields.
/// </summary>
public sealed class AnimationClip
{
    public string Id = "";
    public bool Loop;
    public float Length;

    /// <summary>Keyed by bone name (matches BoneData.name / SkeletonBone.Name in
    /// BlockbenchGeometryParser) — a bone absent from this dictionary has no
    /// channel in this clip and simply stays at its bind pose.</summary>
    public Dictionary<string, BoneAnimationChannel> Bones = new();

    /// <summary>
    /// Samples this clip's rotation for one bone at time t (seconds, expected
    /// pre-wrapped into [0, Length] by the caller — see ModelAnimationSystem).
    /// Returns false (bindRotation unmodified) if the bone has no channel here,
    /// so the caller's fallback is simply "leave the bone at its bind pose."
    /// Linear interpolation between the two surrounding keyframes; clamps to the
    /// first/last keyframe outside the authored range.
    /// </summary>
    public bool TrySampleRotation(string boneName, float t, out float3 eulerDegrees)
    {
        eulerDegrees = default;
        if (!Bones.TryGetValue(boneName, out var channel) || channel.Rotation.Count == 0)
            return false;

        var kf = channel.Rotation;
        if (kf.Count == 1 || t <= kf[0].Time)
        {
            eulerDegrees = kf[0].EulerDegrees;
            return true;
        }
        if (t >= kf[kf.Count - 1].Time)
        {
            eulerDegrees = kf[kf.Count - 1].EulerDegrees;
            return true;
        }

        for (int i = 0; i < kf.Count - 1; i++)
        {
            var a = kf[i];
            var b = kf[i + 1];
            if (t >= a.Time && t <= b.Time)
            {
                float span = b.Time - a.Time;
                float f = span > 0f ? (t - a.Time) / span : 0f;
                eulerDegrees = math.lerp(a.EulerDegrees, b.EulerDegrees, f);
                return true;
            }
        }

        eulerDegrees = kf[kf.Count - 1].EulerDegrees;
        return true;
    }
}