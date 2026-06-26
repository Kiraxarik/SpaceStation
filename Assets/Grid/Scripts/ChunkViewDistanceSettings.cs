using Unity.Entities;

/// <summary>
/// Runtime-adjustable version of the radii that used to live as consts in
/// ChunkLODSettings. ChunkStreamingSystem reads this singleton each frame,
/// so changing these values (e.g. from a UI slider) takes effect immediately
/// — no restart needed.
///
/// ChunkLODSettings.MediumFactor / FarFactor / VeryFarFactor (the mesh
/// downsample factors) are left as compile-time consts — those affect mesh
/// build complexity, not "how far can I see," so they don't need a slider.
/// </summary>
public struct ChunkViewDistanceSettings : IComponentData
{
    public int FullDetailRadius;
    public int MediumLODRadius;
    public int FarLODRadius;
    public int VeryFarRadius;

    public static ChunkViewDistanceSettings Defaults => new ChunkViewDistanceSettings
    {
        FullDetailRadius = 8,
        MediumLODRadius = 16,
        FarLODRadius = 32,
        VeryFarRadius = 64,
    };
}

/// <summary>
/// Creates the ChunkViewDistanceSettings singleton with default values on
/// world startup, if nothing else (e.g. a save/load system) has created one
/// already. Runs once then disables itself.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
public partial struct ChunkViewDistanceBootstrapSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        if (!SystemAPI.HasSingleton<ChunkViewDistanceSettings>())
        {
            state.EntityManager.CreateEntity(
                ComponentType.ReadWrite<ChunkViewDistanceSettings>());
            SystemAPI.SetSingleton(ChunkViewDistanceSettings.Defaults);
        }

        state.Enabled = false;
    }
}