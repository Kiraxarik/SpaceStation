using System;

/// <summary>
/// JSON-serializable shape of a Bedrock-format .geo.json file (what Blockbench
/// exports). See BlockbenchGeometryParser for what's actually supported —
/// these types mirror the file's full shape, but the parser only reads a
/// subset (cubes + box UV, no per-face UV, no animation).
///
/// Field name note: the real top-level JSON key is "minecraft:geometry", which
/// isn't a legal C# identifier. BlockbenchGeometryParser string-replaces that
/// key to "geometry" before handing the text to JsonUtility — this class's
/// field is named to match that patched key, not the raw file.
/// </summary>
[Serializable]
public class GeometryFileData
{
    public string format_version = "";
    public GeometryEntryData[] geometry = Array.Empty<GeometryEntryData>();
}

[Serializable]
public class GeometryEntryData
{
    public GeometryDescriptionData description = new GeometryDescriptionData();
    public BoneData[] bones = Array.Empty<BoneData>();
}

[Serializable]
public class GeometryDescriptionData
{
    public string identifier = "";
    public float texture_width = 16f;
    public float texture_height = 16f;
}

[Serializable]
public class BoneData
{
    public string name = "";

    /// <summary>Parent bone name, or empty for a root bone. Used only to compose
    /// static pivot/rotation posing — see BlockbenchGeometryParser remarks.</summary>
    public string parent = "";

    public float[] pivot;      // [x,y,z], optional
    public float[] rotation;   // [x,y,z] degrees, optional — static pose, not animation
    public CubeData[] cubes = Array.Empty<CubeData>();
}

[Serializable]
public class CubeData
{
    /// <summary>[x,y,z] — the corner nearest -X,-Y,-Z, in Blockbench units
    /// (16 units = 1 block, same scale as texture_width/height being pixel
    /// counts).</summary>
    public float[] origin;

    /// <summary>[width, height, depth].</summary>
    public float[] size;

    /// <summary>[u, v] — BOX UV ONLY. Per-face UV (a JSON object instead of a
    /// 2-element array) isn't supported yet; see BlockbenchGeometryParser
    /// remarks for why and what that means for a cube using it.</summary>
    public float[] uv;

    public float[] pivot;      // [x,y,z], optional — cube-local rotation pivot
    public float[] rotation;   // [x,y,z] degrees, optional — cube-local static rotation
    public float inflate = 0f; // optional, expands the cube outward on all axes
}