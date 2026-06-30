using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>One server entry's data.</summary>
[System.Serializable]
public class ServerInfo
{
    public string Name;
    public string Address = "127.0.0.1";
    public ushort Port = 7979;
    public int Players;
    public int MaxPlayers;
    public int Ping;
    [TextArea] public string Description; // currently Steam game tags
}

/// <summary>
/// Populates the server list from Steam's browser. Refreshes when shown.
/// Join hands the server's IP:port to the UDP connect path unchanged.
/// </summary>
public class ServerListController : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] Transform contentRoot;   // ScrollView > Viewport > Content
    [SerializeField] GameObject entryPrefab;  // row prefab (has a ServerListEntry)

    [Header("Steam")]
    [SerializeField] uint appId = 480;        // Spacewar (dev). Your AppID later.

    [Header("Join")]
    [SerializeField] string gameplayScene = "OutdoorsScene"; // scene whose SubScene has the player ghost prefab

    SteamServerBrowser _browser;

    void OnEnable()
    {
        if (!SteamBootstrap.Initialized)
        {
            Debug.LogWarning("[ServerList] Steam not initialized — can't query servers.");
            return;
        }

        _browser ??= new SteamServerBrowser(appId);
        _browser.OnRefreshComplete -= Populate;
        _browser.OnRefreshComplete += Populate;
        Refresh();
    }

    void OnDisable()
    {
        if (_browser == null) return;
        _browser.OnRefreshComplete -= Populate;
        _browser.Cancel();
    }

    /// <summary>Wire a Refresh button to this.</summary>
    public void Refresh() => _browser?.Refresh();

    void Populate(List<ServerInfo> servers)
    {
        Clear();
        foreach (var info in servers)
        {
            var entry = Instantiate(entryPrefab, contentRoot)
                .GetComponentInChildren<ServerListEntry>();
            entry.Setup(info, JoinServer);
        }
    }

    void Clear()
    {
        for (int i = contentRoot.childCount - 1; i >= 0; i--)
            Destroy(contentRoot.GetChild(i).gameObject);
    }

    void JoinServer(ServerInfo info)
    {
        Debug.Log($"[ServerList] Joining {info.Name} ({info.Address}:{info.Port})");

        // The ECS connection lives in ClientWorld and survives the scene load.
        if (GameClient.Connect(info.Address, info.Port))
            SceneManager.LoadScene(gameplayScene);
    }
}