using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Parses a Bedrock-format .geo.json (what Blockbench exports) into Unity mesh
/// arrays. Client-only — this produces render geometry, never touched by the
/// server (architecture §0.3: the server never renders).
///
/// SCOPE (v1):
///   • Cubes only (no freeform "mesh"-type elements, no Java block-model format).
///   • Box UV only ("uv": [u,v]). Per-face UV isn't supported yet (see the uv
///     field remarks) — a cube using it falls back to a zeroed rect + warning.
///   • Static bone/cube pivot + rotation (posing only), no animation playback —
///     items/NPCs/player need a real skeletal system later; not this one.
///   • Only the first entry in "minecraft:geometry" is used.
///
/// COORDINATE CONVERSION: positions scale from Blockbench "pixels" (16 units =
/// 1 block) into Unity's 1-unit-per-block grid, NO axis negation. A corner-
/// anchored cube (origin spanning pixel [0,16] = local [0,1]) therefore fills
/// exactly one voxel cell when placed at LocalTransform.FromPosition(worldBlock),
/// matching where the greedy mesher renders a solid block. (An earlier version
/// negated X, which shifted corner-anchored geometry a full cell west — removed.)
///
/// FACE ORIENTATION & WINDING (rewritten to be explicit, not guessed): each of
/// the 6 faces is defined below by its four spatial corners in a fixed
/// (bottomLeft, bottomRight, topRight, topLeft) order AS VIEWED FROM OUTSIDE the
/// cube, with the face's "up" being +Y for the four side faces and the standard
/// Minecraft north-up convention for top/bottom. UVs then map trivially:
/// BL→(uMin,vBottom), BR→(uMax,vBottom), TR→(uMax,vTop), TL→(uMin,vTop). One
/// single winding rule is emitted for every face (Unity front-face = clockwise
/// as seen from outside): triangles (BL,TL,TR) and (BL,TR,BR). There is NO
/// post-hoc winding reversal or axis flip anymore — if a specific face renders
/// inside-out or with a rotated/mirrored texture, the fix is a localized swap of
/// that ONE face's corner tuple below, which the arrow-cube test asset
/// (frame.geo.json + the 6-region arrow texture) is designed to pinpoint:
///   • Face invisible from outside but visible from inside → that face's corner
///     order is wound backwards; swap BR and TL for it.
///   • Texture rotated 90°/180° on a face → rotate that face's 4 corners.
///   • Texture mirrored → swap BL and BR (and TL and TR) for that face.
/// Getting one face right tells you the pattern for the rest.
/// </summary>
public static class BlockbenchGeometryParser
{
    public const float UnitsPerBlock = 16f;

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

    public static ParsedMesh? Load(string path, string modelIdForLogging)
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

        string patched = raw.Replace("\"minecraft:geometry\"", "\"geometry\"");

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

        return BuildMesh(file.geometry[0], modelIdForLogging);
    }

    // ── Mesh assembly ──────────────────────────────────────────────────────────

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

    // ── Bone hierarchy (static pose only) ──────────────────────────────────────

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

        float u0 = 0f, v0 = 0f;
        if (cube.uv != null && cube.uv.Length >= 2) { u0 = cube.uv[0]; v0 = cube.uv[1]; }
        else Debug.LogWarning($"[BlockbenchGeometryParser] '{modelId}': cube has no box UV; zeroed UV rect.");

        // Standard Minecraft/Bedrock box-UV cross layout (pixel coords, +y down):
        //            up     down
        //     east  north  west   south
        Rect upR = PixelRect(u0 + d, v0, w, d, tw, th);
        Rect downR = PixelRect(u0 + d + w, v0, w, d, tw, th);
        Rect eastR = PixelRect(u0, v0 + d, d, h, tw, th);
        Rect northR = PixelRect(u0 + d, v0 + d, w, h, tw, th);
        Rect westR = PixelRect(u0 + d + w, v0 + d, d, h, tw, th);
        Rect southR = PixelRect(u0 + d + w + d, v0 + d, w, h, tw, th);

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

    /// <summary>
    /// Emits one quad from four outside-viewed corners + its UV rect. Winding is
    /// fixed here for ALL faces (Unity front-face = clockwise from outside):
    /// (BL,TL,TR) + (BL,TR,BR). UVs: BL=(uMin,vMin_uv), TL=(uMin,vMax_uv), etc.,
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