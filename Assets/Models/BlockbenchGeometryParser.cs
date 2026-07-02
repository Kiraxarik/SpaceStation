using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Parses a Bedrock-format .geo.json (what Blockbench exports) into Unity mesh
/// arrays. Client-only for render geometry (architecture §0.3: the server never
/// renders); the RIG output's collision boxes are the one part the server also
/// consumes (hit volumes, Models doc §3/§5).
///
/// TWO OUTPUT MODES (Models doc §2 — the static/animated split):
///   • Load()    — STATIC BAKE. Every bone transform baked into vertices, one
///                 flat mesh. Blocks, static props. Unchanged behavior.
///   • LoadRig() — ANIMATED. No baking: geometry grouped per bone, plus the bone
///                 tree (parent indices, rest transforms) and the model's
///                 Collision_ hit volumes. Characters, items, NPCs, the player.
///
/// SCOPE (v1):
///   • Cubes only (no freeform "mesh"-type elements, no Java block-model format).
///   • Both UV modes supported: Box UV ("uv": [u,v]) and per-face UV
///     ("uv": {"north": {...}, ...}, renamed to "uvFaces" internally — see the
///     LoadGeometryEntry remarks for why). A cube with neither falls back to a
///     zeroed rect + warning.
///   • LoadRig returns the REST pose rig; animation playback (clip sampling,
///     writing bone transforms per tick) is the animation system's job, not the
///     parser's.
///   • Only the first entry in "minecraft:geometry" is used.
///
/// COORDINATE CONVERSION: positions scale from Blockbench "pixels" (16 units =
/// 1 block) into Unity's 1-unit-per-block grid, NO axis negation. A corner-
/// anchored cube (origin spanning pixel [0,16] = local [0,1]) therefore fills
/// exactly one voxel cell when placed at LocalTransform.FromPosition(worldBlock),
/// matching where the greedy mesher renders a solid block. (An earlier version
/// negated X, which shifted corner-anchored geometry a full cell west — removed.)
///
/// FACE ORIENTATION & WINDING (explicit, not guessed): each of the 6 faces is
/// defined by its four spatial corners in a fixed (bottomLeft, bottomRight,
/// topRight, topLeft) order AS VIEWED FROM OUTSIDE the cube, with the face's
/// "up" being +Y for the four side faces and the standard Minecraft north-up
/// convention for top/bottom. UVs map trivially: BL→(uMin,vBottom),
/// BR→(uMax,vBottom), TR→(uMax,vTop), TL→(uMin,vTop). One single winding rule is
/// emitted for every face (verified against the arrow-cube test asset). If a
/// specific face misbehaves, fix that ONE face's corner tuple in AppendCube:
///   • Face invisible from outside → wound backwards; swap BR and TL for it.
///   • Texture rotated 90°/180° on a face → rotate that face's 4 corners.
///   • Texture mirrored → swap BL and BR (and TL and TR) for that face.
///
/// RIG TRANSFORM MODEL (LoadRig — verified round-trip, see remarks on RigBone):
/// each bone becomes an entity whose ORIGIN IS ITS PIVOT, so animation rotating
/// the entity rotates around the joint naturally. Consequences, all derived and
/// numerically verified rather than assumed:
///   • Bone-local vertices = authored model-space position − bone pivot. (Exactly
///     that, regardless of rest rotations — the pivot-origin choice makes the
///     inverse collapse to a translation.)
///   • A bone entity's rest transform relative to its parent entity =
///     inverse(parentRestEntityMatrix) × childRestEntityMatrix, decomposed to
///     position + rotation (rigid matrices; no scale).
///   • Entities at their rest transforms reproduce the authored model exactly.
///
/// COLLISION CONVENTION (Models doc §3, amended): Bedrock cubes have NO names —
/// only bones (Blockbench groups) do. So a hit volume is a GROUP whose name
/// starts with "Collision_", placed inside the bone it should follow (e.g. group
/// "Collision_Chest" inside group "chest"). Every cube in that group becomes an
/// oriented hit box with zone label = the name after the prefix, attached to the
/// nearest non-collision ancestor bone; the group contributes NO render geometry.
/// </summary>
public static class BlockbenchGeometryParser
{
    public const float UnitsPerBlock = 16f;

    public const string CollisionPrefix = "Collision_";

    // ── Static output ──────────────────────────────────────────────────────────

    public struct ParsedMesh
    {
        public Vector3[] Vertices;
        public Vector2[] Uvs;
        public int[] Triangles;

        /// <summary>Axis-aligned bounds of the mesh in Unity block units, as
        /// authored (after the /16 scale, before any consumer repositioning).
        /// Lets a consumer anchor the model geometrically from its real extent
        /// instead of assuming where the author placed the origin — see
        /// ChunkModelInstanceSystem for how block placement uses this.</summary>
        public Bounds Bounds;
    }

    // ── Animated (rig) output ──────────────────────────────────────────────────

    /// <summary>One bone of an animated model: its identity, its place in the
    /// tree, its rest transform, and the render geometry rigidly attached to it
    /// (in bone-local space, origin at the bone's pivot).</summary>
    public struct RigBone
    {
        /// <summary>Bone (Blockbench group) name — the animation-binding key
        /// (Models doc §4) and the thing clips address tracks to.</summary>
        public string Name;

        /// <summary>Index into ParsedRig.Bones of the parent, or -1 for a root
        /// bone. Collision_ groups are excluded from this array entirely, so a
        /// bone whose authored parent was a Collision_ group parents through it
        /// to the nearest non-collision ancestor (with a warning — that's a
        /// modeling mistake worth surfacing).</summary>
        public int ParentIndex;

        /// <summary>Bone pivot in model space, block units. The bone entity's
        /// origin sits here; animation rotation is around this point.</summary>
        public Vector3 Pivot;

        /// <summary>Rest transform RELATIVE TO THE PARENT bone entity (or to the
        /// model root for ParentIndex -1): what the bone entity's LocalTransform
        /// should be at rest so the composed hierarchy reproduces the authored
        /// model exactly. Derived as inverse(parentRestEntity) × ownRestEntity.</summary>
        public Vector3 RestLocalPosition;
        public Quaternion RestLocalRotation;

        /// <summary>Render geometry rigidly attached to this bone, in bone-local
        /// space (vertex = authored model position − Pivot, cube-local rotation
        /// baked). Empty arrays for a bone with no cubes (pure joint).</summary>
        public Vector3[] Vertices;
        public Vector2[] Uvs;
        public int[] Triangles;
    }

    /// <summary>One authored hit volume: a cube from a Collision_ group,
    /// expressed as an oriented box relative to the bone entity it follows.
    /// Never rendered. The server's rewind poses these from animation state and
    /// ray-tests them (Models doc §5.3); on a hit, Zone is what's reported —
    /// Core never interprets it (Models doc §3.2).</summary>
    public struct RigCollisionBox
    {
        /// <summary>Zone label — the Collision_ group's name after the prefix
        /// ("Collision_Chest" → "Chest"). Free-form; the mod's damage handler
        /// owns its meaning.</summary>
        public string Zone;

        /// <summary>Index into ParsedRig.Bones of the bone this box follows: the
        /// nearest non-collision ancestor of its Collision_ group. -1 if the
        /// group had no visible ancestor (box follows the model root).</summary>
        public int BoneIndex;

        /// <summary>Box center/rotation relative to that bone entity (origin at
        /// the bone pivot), and full box size, block units.</summary>
        public Vector3 LocalCenter;
        public Quaternion LocalRotation;
        public Vector3 Size;
    }

    public struct ParsedRig
    {
        /// <summary>Bones in file order (parents may appear after children in
        /// the file; ParentIndex is resolved regardless). Collision_ groups are
        /// not bones — they appear only as CollisionBoxes.</summary>
        public RigBone[] Bones;

        public RigCollisionBox[] CollisionBoxes;

        /// <summary>Authored rest-pose bounds of the RENDER geometry (collision
        /// boxes excluded), model space, block units — same convention as
        /// ParsedMesh.Bounds.</summary>
        public Bounds Bounds;
    }

    // ── Entry points ───────────────────────────────────────────────────────────

    public static ParsedMesh? Load(string path, string modelIdForLogging)
    {
        var geo = LoadGeometryEntry(path, modelIdForLogging);
        if (geo == null) return null;
        return BuildMesh(geo, modelIdForLogging);
    }

    /// <summary>Animated-mode parse: per-bone geometry + bone tree + collision
    /// boxes, rest pose only. Returns null (logging why) on failure. The static
    /// Load() is untouched by this path — same file, either mode.</summary>
    public static ParsedRig? LoadRig(string path, string modelIdForLogging)
    {
        var geo = LoadGeometryEntry(path, modelIdForLogging);
        if (geo == null) return null;
        return BuildRig(geo, modelIdForLogging);
    }

    static GeometryEntryData LoadGeometryEntry(string path, string modelIdForLogging)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Debug.LogError($"[BlockbenchGeometryParser] '{modelIdForLogging}': geometry file not found at '{path}'.");
            return null;
        }

        string raw;
        try { raw = File.ReadAllText(path); }
        catch (Exception e)
        {
            Debug.LogError($"[BlockbenchGeometryParser] '{modelIdForLogging}': failed to read '{path}': {e.Message}");
            return null;
        }

        // Two text-level patches ahead of JsonUtility, both for the same reason:
        // a JSON key whose value type varies can't map to one typed C# field.
        //   1. Top-level "minecraft:geometry" -> "geometry" (':' isn't a legal
        //      C# identifier).
        //   2. A cube's "uv" key is a 2-element ARRAY for Box UV but an OBJECT
        //      (keyed by face name) for per-face UV. Renaming the object form to
        //      "uvFaces" lets CubeData carry both as separate typed fields
        //      instead of one field JsonUtility would fail to populate for
        //      whichever shape didn't match. This regex only matches "uv"
        //      immediately followed by '{' — a per-face entry's OWN nested
        //      "uv": [u,v] is still an array and is untouched.
        string patched = raw.Replace("\"minecraft:geometry\"", "\"geometry\"");
        patched = System.Text.RegularExpressions.Regex.Replace(
            patched, "\"uv\"\\s*:\\s*\\{", "\"uvFaces\": {");

        GeometryFileData file;
        try { file = JsonUtility.FromJson<GeometryFileData>(patched); }
        catch (Exception e)
        {
            Debug.LogError($"[BlockbenchGeometryParser] '{modelIdForLogging}': failed to parse '{path}': {e.Message}");
            return null;
        }

        if (file?.geometry == null || file.geometry.Length == 0)
        {
            Debug.LogError($"[BlockbenchGeometryParser] '{modelIdForLogging}': no geometry entries in '{path}'.");
            return null;
        }

        if (file.geometry.Length > 1)
            Debug.LogWarning($"[BlockbenchGeometryParser] '{modelIdForLogging}': {file.geometry.Length} geometry " +
                              "entries; using the first.");

        return file.geometry[0];
    }

    // ── Static mesh assembly (unchanged) ──────────────────────────────────────

    static ParsedMesh BuildMesh(GeometryEntryData geo, string modelId)
    {
        float tw = (geo.description != null && geo.description.texture_width > 0) ? geo.description.texture_width : 16f;
        float th = (geo.description != null && geo.description.texture_height > 0) ? geo.description.texture_height : 16f;

        var bonesByName = new Dictionary<string, BoneData>(StringComparer.Ordinal);
        foreach (var b in geo.bones ?? Array.Empty<BoneData>())
            if (!string.IsNullOrEmpty(b.name)) bonesByName[b.name] = b;

        var worldMatrixCache = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);

        var verts = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();

        foreach (var bone in geo.bones ?? Array.Empty<BoneData>())
        {
            // Collision_ groups are hit volumes, never render geometry — the
            // static path simply skips them (a static model CAN still author
            // them; a static prop with hit zones is legitimate, but its boxes
            // are consumed via LoadRig by whatever spawns it hittable).
            if (IsCollisionName(bone.name)) continue;

            Matrix4x4 boneMatrix = ResolveBoneWorldMatrix(bone, bonesByName, worldMatrixCache, modelId);
            foreach (var cube in bone.cubes ?? Array.Empty<CubeData>())
                AppendCube(cube, boneMatrix, tw, th, modelId, verts, uvs, tris);
        }

        // Pixel units → block units. No axis negation, no winding reversal.
        var finalVerts = new Vector3[verts.Count];
        for (int i = 0; i < verts.Count; i++)
            finalVerts[i] = verts[i] / UnitsPerBlock;

        // Authored bounds, for geometry-based anchoring by consumers (see the
        // ParsedMesh.Bounds remarks). Encloses every vertex; empty mesh → zero bounds.
        Bounds bounds = default;
        if (finalVerts.Length > 0)
        {
            bounds = new Bounds(finalVerts[0], Vector3.zero);
            for (int i = 1; i < finalVerts.Length; i++)
                bounds.Encapsulate(finalVerts[i]);
        }

        return new ParsedMesh
        {
            Vertices = finalVerts,
            Uvs = uvs.ToArray(),
            Triangles = tris.ToArray(),
            Bounds = bounds,
        };
    }

    // ── Rig assembly (animated mode) ──────────────────────────────────────────

    static ParsedRig BuildRig(GeometryEntryData geo, string modelId)
    {
        float tw = (geo.description != null && geo.description.texture_width > 0) ? geo.description.texture_width : 16f;
        float th = (geo.description != null && geo.description.texture_height > 0) ? geo.description.texture_height : 16f;

        var fileBones = geo.bones ?? Array.Empty<BoneData>();

        var bonesByName = new Dictionary<string, BoneData>(StringComparer.Ordinal);
        foreach (var b in fileBones)
            if (!string.IsNullOrEmpty(b.name)) bonesByName[b.name] = b;

        var worldMatrixCache = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);

        // Visible (non-collision) bones become rig bones, in file order.
        var visibleBones = new List<BoneData>();
        var rigIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var b in fileBones)
        {
            if (IsCollisionName(b.name)) continue;
            rigIndexByName[b.name] = visibleBones.Count;
            visibleBones.Add(b);
        }

        var rigBones = new RigBone[visibleBones.Count];
        var allModelSpaceVerts = new List<Vector3>(); // for Bounds

        for (int i = 0; i < visibleBones.Count; i++)
        {
            var bone = visibleBones[i];

            // Rest matrices in PIXEL units throughout; scale to blocks at the end.
            Matrix4x4 restWorld = ResolveBoneWorldMatrix(bone, bonesByName, worldMatrixCache, modelId);
            Vector3 pivotPx = ToVector3(bone.pivot, Vector3.zero);

            // Entity convention: origin AT the pivot → rest entity matrix =
            // restWorld × T(pivot). Verified round-trip (see class remarks).
            Matrix4x4 restEntity = restWorld * Matrix4x4.Translate(pivotPx);

            // Parent: nearest non-collision ancestor. Parenting THROUGH a
            // Collision_ group is a modeling mistake (hit volumes shouldn't own
            // visible children) — warn but recover.
            int parentIndex = -1;
            {
                string p = bone.parent;
                bool skippedCollision = false;
                while (!string.IsNullOrEmpty(p) && bonesByName.TryGetValue(p, out var pb))
                {
                    if (!IsCollisionName(pb.name)) { rigIndexByName.TryGetValue(pb.name, out parentIndex); break; }
                    skippedCollision = true;
                    p = pb.parent;
                    parentIndex = -1;
                }
                if (skippedCollision)
                    Debug.LogWarning($"[BlockbenchGeometryParser] '{modelId}': bone '{bone.name}' is parented " +
                                      "under a Collision_ group; re-parented to the nearest visible ancestor. " +
                                      "Collision groups should not contain visible bones.");
            }

            // Rest transform relative to parent entity: inverse(parentRestEntity) × restEntity.
            Matrix4x4 parentRestEntity = Matrix4x4.identity;
            if (parentIndex >= 0)
            {
                var pb = visibleBones[parentIndex];
                Matrix4x4 pWorld = ResolveBoneWorldMatrix(pb, bonesByName, worldMatrixCache, modelId);
                parentRestEntity = pWorld * Matrix4x4.Translate(ToVector3(pb.pivot, Vector3.zero));
            }
            Matrix4x4 rel = parentRestEntity.inverse * restEntity;

            // Rigid decompose (these matrices carry no scale).
            Vector3 restLocalPos = rel.GetColumn(3);
            Quaternion restLocalRot = Quaternion.LookRotation(rel.GetColumn(2), rel.GetColumn(1));

            // Geometry: bake cube-local rotation in MODEL space (same as the
            // static path), then express bone-locally. With the pivot-origin
            // convention this is exactly inverse(restEntity) × modelSpaceVertex —
            // which collapses to (v − pivot) when the bone chain has no rest
            // rotation, but is computed via the full inverse so authored rest
            // rotations are also correct.
            Matrix4x4 invRestEntity = restEntity.inverse;

            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            foreach (var cube in bone.cubes ?? Array.Empty<CubeData>())
            {
                // NOTE: AppendCube composes boneMatrix × cubeLocalPivotRotation.
                // For the rig we want inverse(restEntity) × restWorld × cubeRot =
                // invRestEntity × restWorld as the "bone matrix" seen by AppendCube.
                AppendCube(cube, invRestEntity * restWorld, tw, th, modelId, verts, uvs, tris);

                // Track model-space verts for Bounds (re-derive cheaply: the last
                // 24 verts added, mapped back through restEntity).
                for (int k = verts.Count - 24; k < verts.Count; k++)
                    if (k >= 0) allModelSpaceVerts.Add(restEntity.MultiplyPoint3x4(verts[k]));
            }

            var finalVerts = new Vector3[verts.Count];
            for (int k = 0; k < verts.Count; k++) finalVerts[k] = verts[k] / UnitsPerBlock;

            rigBones[i] = new RigBone
            {
                Name = bone.name,
                ParentIndex = parentIndex,
                Pivot = pivotPx / UnitsPerBlock,
                RestLocalPosition = restLocalPos / UnitsPerBlock,
                RestLocalRotation = restLocalRot,
                Vertices = finalVerts,
                Uvs = uvs.ToArray(),
                Triangles = tris.ToArray(),
            };
        }

        // ── Collision groups → oriented boxes ─────────────────────────────────
        var collisionBoxes = new List<RigCollisionBox>();
        foreach (var b in fileBones)
        {
            if (!IsCollisionName(b.name)) continue;

            string zone = b.name.Substring(CollisionPrefix.Length);
            if (zone.Length == 0)
            {
                Debug.LogWarning($"[BlockbenchGeometryParser] '{modelId}': collision group '{b.name}' has an " +
                                  "empty zone label; using \"Unnamed\".");
                zone = "Unnamed";
            }

            // Owner: nearest non-collision ancestor bone.
            int ownerIndex = -1;
            {
                string p = b.parent;
                while (!string.IsNullOrEmpty(p) && bonesByName.TryGetValue(p, out var pb))
                {
                    if (!IsCollisionName(pb.name)) { rigIndexByName.TryGetValue(pb.name, out ownerIndex); break; }
                    p = pb.parent;
                }
            }

            Matrix4x4 ownerRestEntity = Matrix4x4.identity;
            if (ownerIndex >= 0)
            {
                var ob = visibleBones[ownerIndex];
                Matrix4x4 oWorld = ResolveBoneWorldMatrix(ob, bonesByName, worldMatrixCache, modelId);
                ownerRestEntity = oWorld * Matrix4x4.Translate(ToVector3(ob.pivot, Vector3.zero));
            }
            Matrix4x4 invOwner = ownerRestEntity.inverse;

            Matrix4x4 groupWorld = ResolveBoneWorldMatrix(b, bonesByName, worldMatrixCache, modelId);

            foreach (var cube in b.cubes ?? Array.Empty<CubeData>())
            {
                if (cube?.origin == null || cube.origin.Length < 3 || cube.size == null || cube.size.Length < 3)
                {
                    Debug.LogWarning($"[BlockbenchGeometryParser] '{modelId}': collision cube in '{b.name}' " +
                                      "missing origin/size. Skipped.");
                    continue;
                }

                Vector3 o = new Vector3(cube.origin[0], cube.origin[1], cube.origin[2]);
                Vector3 s = new Vector3(cube.size[0], cube.size[1], cube.size[2]);
                float inf = cube.inflate;
                Vector3 fullSize = s + 2f * inf * Vector3.one;
                Vector3 centerAuthored = o + s * 0.5f;

                // Box rest world = group's rest world × cube-local pivot rotation;
                // then express relative to the owner bone entity.
                Matrix4x4 boxWorld = groupWorld * PivotRotationMatrix(cube.pivot, cube.rotation);
                Matrix4x4 relBox = invOwner * boxWorld;

                Vector3 centerLocal = relBox.MultiplyPoint3x4(centerAuthored);
                Quaternion rotLocal = Quaternion.LookRotation(relBox.GetColumn(2), relBox.GetColumn(1));

                collisionBoxes.Add(new RigCollisionBox
                {
                    Zone = zone,
                    BoneIndex = ownerIndex,
                    LocalCenter = centerLocal / UnitsPerBlock,
                    LocalRotation = rotLocal,
                    Size = fullSize / UnitsPerBlock,
                });
            }

            if ((b.cubes == null || b.cubes.Length == 0))
                Debug.LogWarning($"[BlockbenchGeometryParser] '{modelId}': collision group '{b.name}' contains " +
                                  "no cubes — it defines no hit volume.");
        }

        // Bounds over model-space render verts (pixel units accumulated → scale).
        Bounds rigBounds = default;
        if (allModelSpaceVerts.Count > 0)
        {
            rigBounds = new Bounds(allModelSpaceVerts[0] / UnitsPerBlock, Vector3.zero);
            for (int i = 1; i < allModelSpaceVerts.Count; i++)
                rigBounds.Encapsulate(allModelSpaceVerts[i] / UnitsPerBlock);
        }

        if (rigBones.Length == 0)
            Debug.LogWarning($"[BlockbenchGeometryParser] '{modelId}': rig has no visible bones.");

        return new ParsedRig
        {
            Bones = rigBones,
            CollisionBoxes = collisionBoxes.ToArray(),
            Bounds = rigBounds,
        };
    }

    static bool IsCollisionName(string name)
        => !string.IsNullOrEmpty(name) && name.StartsWith(CollisionPrefix, StringComparison.Ordinal);

    // ── Bone hierarchy (rest pose) ─────────────────────────────────────────────

    static Matrix4x4 ResolveBoneWorldMatrix(BoneData bone, Dictionary<string, BoneData> byName,
        Dictionary<string, Matrix4x4> cache, string modelId)
    {
        if (cache.TryGetValue(bone.name, out var cached)) return cached;

        Matrix4x4 parentMatrix = Matrix4x4.identity;
        if (!string.IsNullOrEmpty(bone.parent))
        {
            if (byName.TryGetValue(bone.parent, out var parentBone) && parentBone != bone)
                parentMatrix = ResolveBoneWorldMatrix(parentBone, byName, cache, modelId);
            else if (bone.parent != "root")
                Debug.LogWarning($"[BlockbenchGeometryParser] '{modelId}': bone '{bone.name}' references " +
                                  $"unknown parent '{bone.parent}'; treating as root.");
        }

        Matrix4x4 local = PivotRotationMatrix(bone.pivot, bone.rotation);
        Matrix4x4 world = parentMatrix * local;
        cache[bone.name] = world;
        return world;
    }

    static Matrix4x4 PivotRotationMatrix(float[] pivotArr, float[] rotationArr)
    {
        if (rotationArr == null || rotationArr.Length < 3) return Matrix4x4.identity;
        Vector3 pivot = ToVector3(pivotArr, Vector3.zero);
        Quaternion rot = Quaternion.Euler(rotationArr[0], rotationArr[1], rotationArr[2]);
        return Matrix4x4.Translate(pivot) * Matrix4x4.Rotate(rot) * Matrix4x4.Translate(-pivot);
    }

    static Vector3 ToVector3(float[] arr, Vector3 fallback)
        => (arr != null && arr.Length >= 3) ? new Vector3(arr[0], arr[1], arr[2]) : fallback;

    // ── Cube → 6 quads ─────────────────────────────────────────────────────────

    static void AppendCube(CubeData cube, Matrix4x4 boneMatrix, float tw, float th, string modelId,
        List<Vector3> verts, List<Vector2> uvs, List<int> tris)
    {
        if (cube?.origin == null || cube.origin.Length < 3 || cube.size == null || cube.size.Length < 3)
        {
            Debug.LogWarning($"[BlockbenchGeometryParser] '{modelId}': cube missing origin/size. Skipped.");
            return;
        }

        float inflate = cube.inflate;
        float x0 = cube.origin[0] - inflate, x1 = cube.origin[0] + cube.size[0] + inflate;
        float y0 = cube.origin[1] - inflate, y1 = cube.origin[1] + cube.size[1] + inflate;
        float z0 = cube.origin[2] - inflate, z1 = cube.origin[2] + cube.size[2] + inflate;

        Matrix4x4 cubeMatrix = boneMatrix * PivotRotationMatrix(cube.pivot, cube.rotation);
        Vector3 P(float x, float y, float z) => cubeMatrix.MultiplyPoint3x4(new Vector3(x, y, z));

        // Eight corners, named by which extreme on each axis (0 = min, 1 = max).
        var c000 = P(x0, y0, z0); var c100 = P(x1, y0, z0);
        var c010 = P(x0, y1, z0); var c110 = P(x1, y1, z0);
        var c001 = P(x0, y0, z1); var c101 = P(x1, y0, z1);
        var c011 = P(x0, y1, z1); var c111 = P(x1, y1, z1);

        float w = cube.size[0], h = cube.size[1], d = cube.size[2];

        Rect upR, downR, eastR, northR, westR, southR;

        if (cube.uvFaces != null)
        {
            upR = FaceUvRect(cube.uvFaces.up, tw, th);
            downR = FaceUvRect(cube.uvFaces.down, tw, th);
            eastR = FaceUvRect(cube.uvFaces.east, tw, th);
            northR = FaceUvRect(cube.uvFaces.north, tw, th);
            westR = FaceUvRect(cube.uvFaces.west, tw, th);
            southR = FaceUvRect(cube.uvFaces.south, tw, th);
        }
        else
        {
            float u0 = 0f, v0 = 0f;
            if (cube.uv != null && cube.uv.Length >= 2) { u0 = cube.uv[0]; v0 = cube.uv[1]; }
            else Debug.LogWarning($"[BlockbenchGeometryParser] '{modelId}': cube has neither box UV nor " +
                                   "per-face UV; zeroed UV rect.");

            // Standard Minecraft/Bedrock box-UV cross layout (pixel coords, +y down):
            //            up     down
            //     east  north  west   south
            upR = PixelRect(u0 + d, v0, w, d, tw, th);
            downR = PixelRect(u0 + d + w, v0, w, d, tw, th);
            eastR = PixelRect(u0, v0 + d, d, h, tw, th);
            northR = PixelRect(u0 + d, v0 + d, w, h, tw, th);
            westR = PixelRect(u0 + d + w, v0 + d, d, h, tw, th);
            southR = PixelRect(u0 + d + w + d, v0 + d, w, h, tw, th);
        }

        // Each face: (bottomLeft, bottomRight, topRight, topLeft) as seen from
        // OUTSIDE, "up" = +Y for sides, MC north-up for top/bottom. See the class
        // remarks for how to correct a single misbehaving face — change only its
        // tuple here, nothing else.
        //
        //                        BL     BR     TR     TL     uvRect
        BuildFace(verts, uvs, tris, c101, c100, c110, c111, eastR);   // +X east  (looking -X; right=-Z)
        BuildFace(verts, uvs, tris, c000, c001, c011, c010, westR);   // -X west  (looking +X; right=+Z)
        BuildFace(verts, uvs, tris, c100, c000, c010, c110, northR);  // -Z north (looking +Z; right=-X)
        BuildFace(verts, uvs, tris, c001, c101, c111, c011, southR);  // +Z south (looking -Z; right=+X)
        BuildFace(verts, uvs, tris, c011, c111, c110, c010, upR);     // +Y up    (looking -Y; north=-Z at top)
        BuildFace(verts, uvs, tris, c000, c100, c101, c001, downR);   // -Y down  (looking +Y)
    }

    static Rect PixelRect(float x, float y, float w, float h, float tw, float th)
        => new Rect(x / tw, 1f - (y + h) / th, w / tw, h / th); // image-space +y-down → UV +y-up

    /// <summary>Per-face UV rect straight from a Bedrock face entry. Reuses
    /// PixelRect's pixel→UV conversion; a negative uv_size component (mirror)
    /// passes through unchanged since Rect's xMin/xMax getters are just
    /// x and x+width (not sorted), so BuildFace's fixed BL→xMin/BR→xMax mapping
    /// naturally reverses for that axis without any extra logic here.</summary>
    static Rect FaceUvRect(FaceUvEntry entry, float tw, float th)
    {
        if (entry?.uv == null || entry.uv.Length < 2) return default;
        float x = entry.uv[0], y = entry.uv[1];
        float w = (entry.uv_size != null && entry.uv_size.Length > 0) ? entry.uv_size[0] : 0f;
        float h = (entry.uv_size != null && entry.uv_size.Length > 1) ? entry.uv_size[1] : 0f;
        return PixelRect(x, y, w, h, tw, th);
    }

    /// <summary>
    /// Emits one quad from four outside-viewed corners + its UV rect. Winding is
    /// fixed here for ALL faces. UVs: BL=(uMin,vMin_uv), TL=(uMin,vMax_uv), etc.,
    /// where the Rect already has y flipped to UV space by PixelRect.
    /// </summary>
    static void BuildFace(List<Vector3> verts, List<Vector2> uvs, List<int> tris,
        Vector3 bl, Vector3 br, Vector3 tr, Vector3 tl, Rect uv)
    {
        int b = verts.Count;
        verts.Add(bl); verts.Add(br); verts.Add(tr); verts.Add(tl);

        uvs.Add(new Vector2(uv.xMin, uv.yMin)); // BL
        uvs.Add(new Vector2(uv.xMax, uv.yMin)); // BR
        uvs.Add(new Vector2(uv.xMax, uv.yMax)); // TR
        uvs.Add(new Vector2(uv.xMin, uv.yMax)); // TL

        // Indices: BL=b, BR=b+1, TR=b+2, TL=b+3
        // Winding: Unity front-face = clockwise from outside. Verified against
        // the arrow-cube test asset — the opposite order rendered inside-out
        // (backface-culled from outside), so faces are emitted (BL,BR,TR)+(BL,TR,TL).
        tris.Add(b); tris.Add(b + 1); tris.Add(b + 2); // BL, BR, TR
        tris.Add(b); tris.Add(b + 2); tris.Add(b + 3); // BL, TR, TL
    }
}