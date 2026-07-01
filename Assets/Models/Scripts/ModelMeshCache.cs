using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Client-only. Builds and caches a Unity Mesh + Material for a piece of model
/// content (ModelRegistry/ModelContent), on first request. Mirrors
/// TileAtlasBaker's "load once, cache" shape but per-model rather than one
/// global bake, since models are used on demand rather than every frame.
///
/// Geometry comes from BlockbenchGeometryParser (Bedrock .geo.json, cubes +
/// box UV only — see its remarks for current format coverage). Texture is the
/// model's first declared texture, loaded as a standalone Texture2D — model
/// textures are NOT part of the block tile atlas (architecture, Models §1.E).
///
/// A model that fails to parse or load gets a fallback unit cube with a
/// magenta/dark checker texture (reusing TileAtlasBaker's "missing" color
/// scheme), so a broken model shows up obviously wrong in-game instead of
/// silently not rendering — same principle as the missing-tile slice.
/// </summary>
public static class ModelMeshCache
{
    struct Entry
    {
        public Mesh Mesh;
        public Material Material;
    }

    static readonly Dictionary<string, Entry> _cache = new(StringComparer.Ordinal);

    /// <summary>Resets cached state at the start of every play session — see
    /// TileAtlasBaker.ResetForPlaySession for why this matters with domain
    /// reload disabled (cached Mesh/Material/Texture2D objects are destroyed on
    /// play exit; without this, the dictionary would keep returning dead
    /// references on the next play).</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetForPlaySession() => _cache.Clear();

    /// <summary>Returns the built (Mesh, Material) for a model id, building and
    /// caching on first request. Always returns something renderable, even on
    /// failure (see class remarks).</summary>
    public static (Mesh Mesh, Material Material) Get(string modelId)
    {
        if (_cache.TryGetValue(modelId, out var cached))
            return (cached.Mesh, cached.Material);

        var built = Build(modelId);
        _cache[modelId] = built;
        return (built.Mesh, built.Material);
    }

    static Entry Build(string modelId)
    {
        var content = ModelRegistry.Get(modelId);
        if (content == null)
        {
            Debug.LogError($"[ModelMeshCache] '{modelId}': not found in ModelRegistry.");
            return Fallback();
        }

        var parsed = BlockbenchGeometryParser.Load(content.GeometryPath, modelId);
        if (parsed == null)
            return Fallback();

        var mesh = new Mesh { name = $"Model {modelId}" };
        mesh.indexFormat = parsed.Value.Vertices.Length > 65000
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(parsed.Value.Vertices);
        mesh.SetUVs(0, parsed.Value.Uvs);
        mesh.SetTriangles(parsed.Value.Triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        var material = BuildMaterial(content, modelId);
        return new Entry { Mesh = mesh, Material = material };
    }

    // ── Shader resolution ──────────────────────────────────────────────────────

    static Shader _resolvedShader;
    static bool _shaderResolutionLogged;

    /// <summary>
    /// This project renders on HDRP (Assets/Settings/HDRPDefaultResources) —
    /// confirmed by ChunkMaterial/VoxelAtlas.shader's own
    /// Tags{"RenderPipeline"="HDRenderPipeline"}. "HDRP/Lit" is the correct
    /// first choice; the URP/Standard fallbacks only exist for a future project
    /// that switches pipelines, or a Standard one that at least won't throw —
    /// note Standard specifically does NOT support DOTS instancing
    /// (BatchRendererGroup/Entities Graphics), which is what produced the
    /// "does not define a DOTS_INSTANCING_ON variant" warning when this
    /// resolved to Standard. If it ever falls all the way through to Standard
    /// again, that warning will resurface — it means none of the pipeline
    /// shaders were found, worth checking the HDRP package is actually present.
    /// </summary>
    static Shader ResolveShader()
    {
        if (_resolvedShader != null) return _resolvedShader;

        _resolvedShader = Shader.Find("HDRP/Lit")
            ?? Shader.Find("Universal Render Pipeline/Lit")
            ?? Shader.Find("Standard");

        if (!_shaderResolutionLogged)
        {
            _shaderResolutionLogged = true;
            if (_resolvedShader == null)
                Debug.LogError("[ModelMeshCache] No usable shader found (HDRP/Lit, URP/Lit, Standard all missing).");
            else if (_resolvedShader.name == "Standard")
                Debug.LogWarning("[ModelMeshCache] Falling back to 'Standard' — this shader does NOT support " +
                                  "DOTS instancing (Entities Graphics) and model instances will render wrong. " +
                                  "Expected 'HDRP/Lit' to resolve; check the HDRP package is installed.");
            else
                Debug.Log($"[ModelMeshCache] Using shader '{_resolvedShader.name}' for model materials.");
        }

        return _resolvedShader;
    }

    static Material BuildMaterial(ModelContent content, string modelId)
    {
        var material = new Material(ResolveShader());

        if (content.TexturePaths != null && content.TexturePaths.Length > 1)
            Debug.LogWarning($"[ModelMeshCache] '{modelId}': {content.TexturePaths.Length} textures declared; " +
                              "only the first is used (multi-material models aren't supported yet).");

        string texPath = (content.TexturePaths != null && content.TexturePaths.Length > 0)
            ? content.TexturePaths[0]
            : null;

        Texture2D tex = LoadTexture(texPath, modelId);
        tex.filterMode = FilterMode.Point; // Blockbench textures are pixel art — keep them crisp, matches TileAtlasBaker.

        // HDRP/Lit's base color map property is "_BaseColorMap", not the legacy
        // "_MainTex" that material.mainTexture writes to on Built-in shaders —
        // setting mainTexture on an HDRP/Lit material silently does nothing.
        if (material.HasProperty("_BaseColorMap"))
            material.SetTexture("_BaseColorMap", tex);
        else
            material.mainTexture = tex; // Built-in/URP Lit fallback path

        return material;
    }

    static Texture2D LoadTexture(string path, string modelId)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Debug.LogError($"[ModelMeshCache] '{modelId}': texture missing, using placeholder.");
            return MissingTexture();
        }

        try
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(File.ReadAllBytes(path)))
            {
                Debug.LogError($"[ModelMeshCache] '{modelId}': failed to decode '{path}'.");
                UnityEngine.Object.Destroy(tex);
                return MissingTexture();
            }
            return tex;
        }
        catch (Exception e)
        {
            Debug.LogError($"[ModelMeshCache] '{modelId}': {e.Message}");
            return MissingTexture();
        }
    }

    static Texture2D MissingTexture()
    {
        const int size = 16;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        var magenta = new Color(1f, 0f, 0.86f, 1f);
        var dark = new Color(0.08f, 0.08f, 0.08f, 1f);
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                px[y * size + x] = ((x / 4) + (y / 4)) % 2 == 0 ? magenta : dark;
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    static Entry Fallback()
    {
        return new Entry { Mesh = UnitCubeMesh(), Material = BuildFallbackMaterial() };
    }

    static Material BuildFallbackMaterial()
    {
        var material = new Material(ResolveShader());
        var tex = MissingTexture();
        if (material.HasProperty("_BaseColorMap")) material.SetTexture("_BaseColorMap", tex);
        else material.mainTexture = tex;
        return material;
    }

    /// <summary>A plain 0..1 unit cube — hand-built rather than
    /// GameObject.CreatePrimitive so a broken model doesn't momentarily spawn
    /// and destroy a real GameObject (with its own collider/renderer
    /// registration) just to steal its mesh.</summary>
    static Mesh UnitCubeMesh()
    {
        Vector3[] c =
        {
            new(0,0,0), new(1,0,0), new(1,1,0), new(0,1,0),
            new(0,0,1), new(1,0,1), new(1,1,1), new(0,1,1),
        };
        int[][] faces =
        {
            new[]{0,1,2,3}, new[]{5,4,7,6}, new[]{4,0,3,7},
            new[]{1,5,6,2}, new[]{3,2,6,7}, new[]{4,5,1,0},
        };

        var verts = new Vector3[24];
        var uvs = new Vector2[24];
        var tris = new int[36];
        int vi = 0, ti = 0;

        foreach (var f in faces)
        {
            for (int k = 0; k < 4; k++) verts[vi + k] = c[f[k]];
            uvs[vi] = new Vector2(0, 0); uvs[vi + 1] = new Vector2(1, 0);
            uvs[vi + 2] = new Vector2(1, 1); uvs[vi + 3] = new Vector2(0, 1);
            tris[ti++] = vi; tris[ti++] = vi + 1; tris[ti++] = vi + 2;
            tris[ti++] = vi; tris[ti++] = vi + 2; tris[ti++] = vi + 3;
            vi += 4;
        }

        var mesh = new Mesh { name = "Model fallback cube" };
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}