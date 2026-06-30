using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// One row in the server list. Setup() is called by ServerListController
/// for each server it shows.
/// </summary>
public class ServerListEntry : MonoBehaviour
{
    [SerializeField] TMP_Text nameText;
    [SerializeField] TMP_Text playersText;
    [SerializeField] TMP_Text pingText;
    [SerializeField] Button joinButton;

    public void Setup(ServerInfo info, Action<ServerInfo> onJoin)
    {
        nameText.text = info.Name;
        playersText.text = $"{info.Players}/{info.MaxPlayers}";
        pingText.text = $"{info.Ping} ms";

        joinButton.onClick.RemoveAllListeners();
        joinButton.onClick.AddListener(() => onJoin(info));
    }
}