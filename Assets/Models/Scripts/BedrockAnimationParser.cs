using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Parses a Bedrock-format .animation.json file (what Blockbench exports) into
/// AnimationClip objects, keyed by their authored clip id
/// ("animation.test_animated.swing", etc.) exactly as Bedrock names them —
/// Bedrock allows several clips per file under one top-level "animations" object,
/// so this always returns the full set found in the file, not just the first.
///
/// Uses MiniJson rather than JsonUtility — see MiniJson's remarks for why
/// (dynamic dictionary keys: bone name, keyframe timestamp).
///
/// SCOPE (v1, matches BlockbenchGeometryParser's own scoping style):
///   • Rotation channels only (position/scale not read yet — see
///     BoneAnimationChannel remarks).
///   • Keyframe values must be a plain [x,y,z] array (a Bedrock keyframe can
///     also be an object with "pre"/"post" easing values for discontinuous
///     jumps — that shape isn't read; falls back to treating the frame as
///     missing, logged once per clip).
///   • "animation_length" is required for looping to wrap correctly; if absent,
///     the clip is treated as non-looping and length is inferred from the last
///     keyframe found across all bones.
/// </summary>
public static class BedrockAnimationParser
{
    public static Dictionary<string, AnimationClip> Load(string path, string modelIdForLogging)
    {
        var result = new Dictionary<string, AnimationClip>(StringComparer.Ordinal);

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Debug.LogError($"[BedrockAnimationParser] '{modelIdForLogging}': animation file not found at '{path}'.");
            return result;
        }

        string raw;
        try { raw = File.ReadAllText(path); }
        catch (Exception e)
        {
            Debug.LogError($"[BedrockAnimationParser] '{modelIdForLogging}': failed to read '{path}': {e.Message}");
            return result;
        }

        Dictionary<string, object> root;
        try { root = MiniJson.Parse(raw) as Dictionary<string, object>; }
        catch (Exception e)
        {
            Debug.LogError($"[BedrockAnimationParser] '{modelIdForLogging}': failed to parse '{path}': {e.Message}");
            return result;
        }

        var animations = MiniJson.GetObject(root, "animations");
        if (animations == null || animations.Count == 0)
        {
            Debug.LogWarning($"[BedrockAnimationParser] '{modelIdForLogging}': no 'animations' entries in '{path}'.");
            return result;
        }

        foreach (var kv in animations)
        {
            string clipId = kv.Key;
            var clipObj = MiniJson.AsObject(kv.Value);
            if (clipObj == null) continue;

            var clip = new AnimationClip
            {
                Id = clipId,
                Loop = MiniJson.GetBool(clipObj, "loop", false),
                Length = (float)MiniJson.GetNumber(clipObj, "animation_length", -1.0),
            };

            var bones = MiniJson.GetObject(clipObj, "bones");
            float inferredLength = 0f;
            if (bones != null)
            {
                foreach (var boneKv in bones)
                {
                    string boneName = boneKv.Key;
                    var boneObj = MiniJson.AsObject(boneKv.Value);
                    if (boneObj == null) continue;

                    var rotationObj = MiniJson.GetObject(boneObj, "rotation");
                    if (rotationObj == null) continue; // no rotation channel for this bone in this clip

                    var channel = new BoneAnimationChannel();
                    foreach (var frameKv in rotationObj)
                    {
                        if (!float.TryParse(frameKv.Key, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float time))
                        {
                            Debug.LogWarning($"[BedrockAnimationParser] '{modelIdForLogging}'/{clipId}/{boneName}: " +
                                              $"non-numeric keyframe time '{frameKv.Key}', skipped.");
                            continue;
                        }

                        var arr = MiniJson.AsArray(frameKv.Value);
                        if (arr == null || arr.Count < 3)
                        {
                            Debug.LogWarning($"[BedrockAnimationParser] '{modelIdForLogging}'/{clipId}/{boneName}: " +
                                              $"keyframe at {time}s isn't a plain [x,y,z] array (pre/post easing " +
                                              "objects aren't supported yet), skipped.");
                            continue;
                        }

                        var euler = new Unity.Mathematics.float3(
                            (float)MiniJson.AsNumber(arr[0]),
                            (float)MiniJson.AsNumber(arr[1]),
                            (float)MiniJson.AsNumber(arr[2]));

                        channel.Rotation.Add(new AnimKeyframe { Time = time, EulerDegrees = euler });
                        inferredLength = Mathf.Max(inferredLength, time);
                    }

                    channel.Rotation.Sort((a, b) => a.Time.CompareTo(b.Time));
                    if (channel.Rotation.Count > 0)
                        clip.Bones[boneName] = channel;
                }
            }

            if (clip.Length < 0f)
                clip.Length = inferredLength;

            result[clipId] = clip;
        }

        Debug.Log($"[BedrockAnimationParser] '{modelIdForLogging}': {result.Count} clip(s) from '{Path.GetFileName(path)}'.");
        return result;
    }
}