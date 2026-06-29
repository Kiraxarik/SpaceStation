using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Bakes the loaded TileRegistry into a Texture2DArray and exposes the tile-id →
/// slice map the mesh systems use. Client-only (it creates a GPU texture); the
/// server never renders.
///
/// Each tile becomes one array slice — its own full image with its own mip chain
/// and Repeat wrap. That's what eliminates the atlas bleeding the old single-image
/// path had: there are no neighbouring tiles to bleed into, and per-block tiling is
/// just the sampler wrapping within a slice (mesh emits uv0 = 0..w, no frac, so uv
/// derivatives stay continuous and mips select correctly).
///
/// Slice 0 is reserved as a visible magenta "missing" tile, so any block whose tile
/// id doesn't resolve renders obviously-wrong instead of sampling garbage.
/// Deterministic order (ordinal by tile id) so the slice assignment is stable.
/// </summary>
public static class TileAtlasBaker
{
    public const int TileSize = 32;

    public static Texture2DArray Array { get; private set; }

    static Dictionary<string, int> _sliceById;
    static bool _attempted;

    /// <summary>Bakes once. Returns false if baking failed (e.g. no GPU array support).</summary>
    public static bool EnsureBaked()
    {
        if (_attempted) return Array != null;
        _attempted = true;

        if (!SystemInfo.supports2DArrayTextures)
        {
            Debug.LogError("[TileAtlasBaker] GPU does not support 2D array textures; blocks cannot render.");
            return false;
        }

        var tiles = new List<TileContent>(TileRegistry.All);
        tiles.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        int sliceCount = tiles.Count + 1; // +1 for the reserved "missing" slice 0
        var array = new Texture2DArray(TileSize, TileSize, sliceCount, TextureFormat.RGBA32, true, false)
        {
            wrapMode   = TextureWrapMode.Repeat,
            filterMode = FilterMode.Point,
            anisoLevel = 0,
        };

        array.SetPixels(MissingPixels(), 0);

        _sliceById = new Dictionary<string, int>(tiles.Count, StringComparer.Ordinal);
        for (int i = 0; i < tiles.Count; i++)
        {
            int slice = i + 1;
            _sliceById[tiles[i].Id] = slice;
            array.SetPixels(LoadTilePixels(tiles[i]), slice);
        }

        array.Apply(updateMipmaps: true, makeNoLongerReadable: false);
        Array = array;

        Debug.Log($"[TileAtlasBaker] Baked Texture2DArray — {sliceCount} slice(s) @ {TileSize}px (slice 0 = missing).");
        return true;
    }

    /// <summary>Slice index for a tile id, or 0 (the missing slice) if unknown/empty.</summary>
    public static int SliceOf(string tileId)
    {
        if (string.IsNullOrEmpty(tileId) || _sliceById == null) return 0;
        if (_sliceById.TryGetValue(tileId, out int s)) return s;
        if (!tileId.Contains(':') && _sliceById.TryGetValue($"base:{tileId}", out s)) return s;
        return 0;
    }

    // ── Pixel loading ──────────────────────────────────────────────────────────

    static Color[] LoadTilePixels(TileContent tile)
    {
        if (string.IsNullOrEmpty(tile.TexturePath) || !File.Exists(tile.TexturePath))
        {
            Debug.LogError($"[TileAtlasBaker] '{tile.Id}': texture missing, using placeholder.");
            return MissingPixels();
        }

        Texture2D tex = null;
        try
        {
            tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(File.ReadAllBytes(tile.TexturePath)))
            {
                Debug.LogError($"[TileAtlasBaker] '{tile.Id}': failed to decode PNG.");
                return MissingPixels();
            }

            if (tex.width != TileSize || tex.height != TileSize)
            {
                Debug.LogWarning($"[TileAtlasBaker] '{tile.Id}' is {tex.width}x{tex.height}, expected " +
                                 $"{TileSize}x{TileSize}; resizing.");
                var resized = Resize(tex, TileSize, TileSize);
                UnityEngine.Object.Destroy(tex);
                tex = resized;
            }

            return tex.GetPixels();
        }
        catch (Exception e)
        {
            Debug.LogError($"[TileAtlasBaker] '{tile.Id}': {e.Message}");
            return MissingPixels();
        }
        finally
        {
            if (tex != null) UnityEngine.Object.Destroy(tex);
        }
    }

    static Texture2D Resize(Texture2D src, int w, int h)
    {
        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
        var prev = RenderTexture.active;
        Graphics.Blit(src, rt);
        RenderTexture.active = rt;

        var dst = new Texture2D(w, h, TextureFormat.RGBA32, false);
        dst.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        dst.Apply();

        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        return dst;
    }

    static Color[] MissingPixels()
    {
        var px = new Color[TileSize * TileSize];
        var magenta = new Color(1f, 0f, 0.86f, 1f);
        var dark    = new Color(0.08f, 0.08f, 0.08f, 1f);
        for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                px[y * TileSize + x] = ((x / 8) + (y / 8)) % 2 == 0 ? magenta : dark;
        return px;
    }
}
