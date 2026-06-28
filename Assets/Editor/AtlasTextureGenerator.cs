using UnityEngine;
using UnityEditor;
using System.IO;

/// <summary>
/// Generates a 512×512 test block atlas (16 cols × 16 rows of 32×32 px tiles).
///
/// Each tile has a distinct solid colour with a 2px dark border so you can see
/// tile boundaries clearly. Specific tiles match BlockRegistry entries:
///   Tile 0 — air        (transparent — should never be visible on a face)
///   Tile 1 — floor top  (light grey with a 16px sub-grid, like metal decking)
///   Tile 2 — floor base (dark grey)
///   Tile 3 — wall panel (steel blue with corner rivets)
///   Tiles 4+ — distinct HSV colours for future block types
///
/// Run via Tools → Generate Test Atlas. The PNG is saved to
/// Assets/Resources/ChunkAtlas.png with Point filter mode (pixel-perfect).
/// Assign it to the _BaseColorMap slot on your ChunkMaterial.
/// </summary>
public static class AtlasTextureGenerator
{
    const int TILE_PX = 32;
    const int ATLAS_COLS = 16;
    const int ATLAS_PX = TILE_PX * ATLAS_COLS;   // 512

    [MenuItem("Tools/Generate Test Atlas")]
    static void Generate()
    {
        var tex = new Texture2D(ATLAS_PX, ATLAS_PX, TextureFormat.RGBA32, mipChain: false);
        var pixels = new Color[ATLAS_PX * ATLAS_PX];

        for (int tileRow = 0; tileRow < ATLAS_COLS; tileRow++)
            for (int tileCol = 0; tileCol < ATLAS_COLS; tileCol++)
            {
                int idx = tileCol + tileRow * ATLAS_COLS;
                PaintTile(pixels, tileCol, tileRow, idx);
            }

        tex.SetPixels(pixels);
        tex.Apply();

        string dir = "Assets/Resources";
        string path = $"{dir}/ChunkAtlas.png";

        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllBytes(path, tex.EncodeToPNG());
        AssetDatabase.Refresh();

        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null)
        {
            importer.filterMode = FilterMode.Point;   // pixel-perfect, no blending between tiles
            importer.mipmapEnabled = true;
            importer.sRGBTexture = true;
            importer.maxTextureSize = 512;
            importer.textureType = TextureImporterType.Default;
            importer.SaveAndReimport();
        }

        Debug.Log($"[AtlasGen] Saved to {path}  ({ATLAS_PX}×{ATLAS_PX}, {TILE_PX}px tiles)");
    }

    // ── Per-tile painter ──────────────────────────────────────────────────────

    static void PaintTile(Color[] pixels, int tileCol, int tileRow, int tileIndex)
    {
        Color bg = TileBaseColour(tileIndex);

        for (int py = 0; py < TILE_PX; py++)
            for (int px = 0; px < TILE_PX; px++)
            {
                int ax = tileCol * TILE_PX + px;
                int ay = tileRow * TILE_PX + py;   // Y=0 is bottom in Unity textures

                Color c = bg;

                bool border = px < 2 || px >= TILE_PX - 2 || py < 2 || py >= TILE_PX - 2;
                if (border)
                {
                    // Tile 0 (air) stays transparent even at border
                    c = tileIndex == 0 ? Color.clear : new Color(0.1f, 0.1f, 0.1f, 1f);
                }
                else
                {
                    switch (tileIndex)
                    {
                        case 0: // air — fully transparent
                            c = Color.clear;
                            break;

                        case 1: // floor top — light grey metal with 16px sub-grid
                            bool gridLine = (px % 16 == 0) || (py % 16 == 0);
                            c = gridLine ? new Color(0.50f, 0.50f, 0.50f) : bg;
                            break;

                        case 3: // wall panel — steel blue with corner rivets
                            bool rivet =
                                (InRange(px, 4, 7) && InRange(py, 4, 7)) ||
                                (InRange(px, 4, 7) && InRange(py, 24, 27)) ||
                                (InRange(px, 24, 27) && InRange(py, 4, 7)) ||
                                (InRange(px, 24, 27) && InRange(py, 24, 27));
                            c = rivet ? new Color(0.9f, 0.9f, 0.9f) : bg;
                            break;
                    }
                }

                pixels[ay * ATLAS_PX + ax] = c;
            }
    }

    static Color TileBaseColour(int index) => index switch
    {
        0 => Color.clear,
        1 => new Color(0.72f, 0.72f, 0.72f),           // floor top — light grey
        2 => new Color(0.28f, 0.28f, 0.28f),           // floor base — dark grey
        3 => new Color(0.46f, 0.56f, 0.65f),           // wall panel — steel blue
        _ => Color.HSVToRGB(index / 64f % 1f, 0.65f, 0.80f),
    };

    static bool InRange(int v, int lo, int hi) => v >= lo && v <= hi;
}