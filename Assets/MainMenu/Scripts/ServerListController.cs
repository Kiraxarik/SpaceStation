using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>One server entry's data.</summary>
[Serializable]
public class ServerInfo
{
    public string Name;
    public string Address = "127.0.0.1";
    public ushort Port = 7979;
    public int Players;
    public int MaxPlayers;
    public int Ping;
    [TextArea] public string Description;
}

/// <summary>
/// Fills the server list scroll view with one row per server. Refreshes when
/// the panel is shown. The server source is a placeholder for now — swap
/// GetServers() for a real fetch later without touching the UI.
/// </summary>
public class ServerListController : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] Transform contentRoot;       // ScrollView > Viewport > Content
    [SerializeField] ServerListEntry entryPrefab; // the row prefab

    void OnEnable() => Refresh();

    /// <summary>Wire a Refresh button to this.</summary>
    public void Refresh()
    {
        for (int i = contentRoot.childCount - 1; i >= 0; i--)
            Destroy(contentRoot.GetChild(i).gameObject);

        foreach (var info in GetServers())
        {
            var entry = Instantiate(entryPrefab, contentRoot);
            entry.Setup(info, JoinServer);
        }
    }

    // TODO(server): replace with a real fetch from your master-server list
    // (and probe each for live player count / ping). Keep the signature.
    static IEnumerable<ServerInfo> GetServers() => new[]
    {
        new ServerInfo { Name = "Local Dev",           Address = "127.0.0.1", Port = 7979, Players = 0,  MaxPlayers = 50, Ping = 1,  Description = "Local editor/dev server." },
        new ServerInfo { Name = "Placeholder Station", Address = "127.0.0.1", Port = 7980, Players = 12, MaxPlayers = 50, Ping = 42, Description = "Example entry. Real servers and descriptions come from the master list later." },
    };

    void JoinServer(ServerInfo info)
    {
        // TODO: connect to info.Address:info.Port via your client connect path.
        Debug.Log($"[ServerList] Join {info.Name} ({info.Address}:{info.Port})");
    }
}