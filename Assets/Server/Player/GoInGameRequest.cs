using Unity.NetCode;

/// <summary>
/// Sent by the client to the server once the connection is established,
/// meaning "I'm ready — please spawn my player and start sending me
/// snapshots." Contains no data; just acts as a signal.
/// </summary>
public struct GoInGameRequest : IRpcCommand { }
