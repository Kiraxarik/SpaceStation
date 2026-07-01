using Unity.Entities;
using Unity.NetCode;

/// <summary>Client → server: "I want a body" — the commit (ready / join) action.</summary>
public struct CommitToPlayRequest : IRpcCommand { }

/// <summary>Server tag on a connection: it has committed (ready / joined).</summary>
public struct Committed : IComponentData { }

/// <summary>Server tag on a connection: it already has a spawned body.</summary>
public struct PlayerSpawned : IComponentData { }