using UnityEngine;

/// <summary>
/// Main menu controller. Hook each button's OnClick to the matching method.
/// Play opens the server list; Tutorial and Settings are placeholders for now.
/// </summary>
public class MainMenuController : MonoBehaviour
{
    [Header("Panels")]
    [SerializeField] GameObject mainPanel;        // the menu itself
    [SerializeField] GameObject serverListPanel;  // placeholder, hidden at start

    public void OnPlay()
    {
        mainPanel.SetActive(false);
        serverListPanel.SetActive(true);
    }

    public void OnTutorial()
    {
        Debug.Log("[MainMenu] Tutorial — not implemented yet.");
    }

    public void OnSettings()
    {
        Debug.Log("[MainMenu] Settings — not implemented yet.");
    }

    public void OnQuit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    /// <summary>Wire a Back button on the server list panel to this.</summary>
    public void OnBackToMain()
    {
        serverListPanel.SetActive(false);
        mainPanel.SetActive(true);
    }
}