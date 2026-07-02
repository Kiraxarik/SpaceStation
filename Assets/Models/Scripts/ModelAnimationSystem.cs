using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

/// <summary>
/// Drives every AnimatedBone entity's LocalTransform.Rotation each tick by
/// sampling that bone's clip (AnimationClipRegistry, keyed by the bone's
/// ModelId) at the current time and converting the authored Blockbench degrees
/// to the radians LocalTransform.Rotation expects.
///
/// v1 SIMPLIFICATION (flagged deliberately, not an oversight): time comes from
/// a single global clock (SystemAPI.Time.ElapsedTime, wrapped per-clip's own
/// Length), so every instance of every animated model plays in lockstep from
/// world start rather than each instance having its own independent playback
/// state (idle-desync, state-driven start/stop, blending). That's real work
/// for the eventual animation state machine (pairs with Phase 4 roles/combat) —
/// this system exists to prove bone hierarchy + keyframe sampling render
/// correctly end-to-end for a test clip, per the "quick test animation" ask.
/// Swapping in per-instance timing later only touches the `t` computation
/// below plus adding a per-root AnimationPlayer-style time component; it does
/// NOT change how a bone is sampled or how its rotation gets written.
///
/// Runs client-only, like every other render/model system in this project —
/// the server never plays animations, it only needs the pose reconstructible
/// for hit validation later (architecture, Models §0.4 — not implemented yet;
/// this system does not touch that).
///
/// Managed/non-Burst SystemBase, matching ChunkModelInstanceSystem/
/// ChunkMeshSystem's own style — AnimationClip's keyframe sampling walks a
/// managed List&lt;&gt; via a Dictionary lookup, neither of which is
/// Burst-compatible, and the instance counts here (a handful of animated
/// models on screen) don't currently justify a NativeArray/blob rewrite.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
public partial class ModelAnimationSystem : SystemBase
{
    protected override void OnCreate()
    {
        if (World.Name != "ClientWorld") { Enabled = false; return; }
    }

    protected override void OnUpdate()
    {
        float globalTime = (float)SystemAPI.Time.ElapsedTime;

        foreach (var (bone, transform) in
            SystemAPI.Query<RefRO<AnimatedBone>, RefRW<LocalTransform>>())
        {
            quaternion rotation = bone.ValueRO.BindRotation;

            var clip = AnimationClipRegistry.GetDefaultClip(bone.ValueRO.ModelId.ToString());
            if (clip != null)
            {
                float t = clip.Length > 0f ? globalTime % clip.Length : 0f;
                if (clip.TrySampleRotation(bone.ValueRO.BoneName.ToString(), t, out var eulerDegrees))
                    rotation = quaternion.Euler(math.radians(eulerDegrees));
            }

            transform.ValueRW.Rotation = rotation;
        }
    }
}