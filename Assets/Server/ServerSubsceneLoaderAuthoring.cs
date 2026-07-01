using Unity.Entities;
using Unity.Entities.Serialization;
using Unity.NetCode;
using Unity.Scenes;
using UnityEngine;

/// <summary>
/// Loads the ChunkWorld subscene into the SERVER world at runtime via an
/// EntitySceneReference — the ONLY runtime-load path Unity's build pipeline
/// detects and ships.
///
/// Per Unity's Entities docs:
///   "The build process only detects authoring scenes that are referenced by
///    EntitySceneReference and SubScene MonoBehaviours. The build process
///    doesn't detect scenes referenced by GUIDs, and their entity scene files
///    will be missing from builds."
///
/// A previous attempt loaded the subscene by raw Hash128 GUID, which works in
/// the editor but fails in a build with:
///   "Loading Entity Scene failed because the entity header file couldn't be
///    resolved."
/// This uses a baked EntitySceneReference instead, so the entity header ships
/// and resolves in the dedicated-server build.
///
/// SETUP (one-time, in the editor):
///   1. Create an empty GameObject in the ServerInGameScene (NOT inside the
///      ChunkWorld subscene — it must bake from a normally-loaded place).
///      Name it e.g. "ServerSubsceneLoader".
///   2. Add the ServerSubsceneLoaderAuthoring component below to it.
///   3. Drag ChunkWorld.unity (the SubScene's .unity asset) into its
///      "Chunk World Scene" field in the Inspector.
///   4. Save the scene. Build. Done.
///
/// The dedicated server otherwise never loads ServerInGameScene's SubScene
/// (that only happens on the client via the Join button), so this closes the
/// gap: the server loads the subscene's baked entity data — PlayerSpawner +
/// LocalPlayer ghost prefab — when the round goes Running.
/// </summary>

// ── Runtime component: holds the build-safe scene reference ───────────────────
public struct ServerSubsceneToLoad : IComponentData
{
    public EntitySceneReference ChunkWorldScene;
}

// ── Authoring: drag ChunkWorld.unity into the field, bakes the reference ──────
#if UNITY_EDITOR
public class ServerSubsceneLoaderAuthoring : MonoBehaviour
{
    [Tooltip("Drag ChunkWorld.unity (the SubScene asset) here.")]
    public UnityEditor.SceneAsset ChunkWorldScene;

    class Baker : Baker<ServerSubsceneLoaderAuthoring>
    {
        public override void Bake(ServerSubsceneLoaderAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            AddComponent(entity, new ServerSubsceneToLoad
            {
                ChunkWorldScene = new EntitySceneReference(authoring.ChunkWorldScene)
            });
        }
    }
}
#endif

// ── System: loads the referenced subscene into ServerWorld when Running ───────
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
public partial struct ServerSubsceneLoadSystem : ISystem
{
    Entity _sceneEntity;
    bool _requested;

    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<ServerSubsceneToLoad>();
        state.RequireForUpdate<RoundState>();
    }

    public void OnUpdate(ref SystemState state)
    {
        if (!_requested)
        {
            // Wait until the round is Running before loading the play world.
            if (SystemAPI.GetSingleton<RoundState>().Phase != RoundPhase.Running)
                return;

            _requested = true;

            var toLoad = SystemAPI.GetSingleton<ServerSubsceneToLoad>();
            _sceneEntity = SceneSystem.LoadSceneAsync(
                state.WorldUnmanaged,
                toLoad.ChunkWorldScene);

            UnityEngine.Debug.Log("[ServerSubsceneLoad] Requested ChunkWorld subscene load " +
                                  "via EntitySceneReference (build-safe path).");
            return;
        }

        if (SceneSystem.IsSceneLoaded(state.WorldUnmanaged, _sceneEntity))
        {
            UnityEngine.Debug.Log("[ServerSubsceneLoad] ChunkWorld subscene LOADED in ServerWorld.");
            state.Enabled = false;
        }
    }
}
