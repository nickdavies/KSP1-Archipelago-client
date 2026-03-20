using System;
using UnityEngine;
using KSP.UI.Screens;

namespace KSPArchipelago
{
    /// <summary>
    /// In-game UI: AppLauncher toolbar button + connection/status panel.
    /// Registered for all game scenes so the player can connect from the Space Center.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.AllGameScenes, false)]
    public class ArchipelagoUI : MonoBehaviour
    {
        private static ArchipelagoUI instance;

        private KSPArchipelagoMod mod;
        private ApplicationLauncherButton toolbarButton;

        // Panel state.
        private bool showPanel = false;
        private Rect windowRect = new Rect(Screen.width / 2 - 200, Screen.height / 2 - 150, 400, 300);

        // Connection form fields.
        private string host = "localhost";
        private string portStr = "38281";
        private string slot = "";
        private string password = "";

        private void Start()
        {
            instance = this;
            // Find the mod (persists across scenes via DontDestroyOnLoad).
            mod = FindObjectOfType<KSPArchipelagoMod>();

            GameEvents.onGUIApplicationLauncherReady.Add(AddToolbarButton);
            GameEvents.onGUIApplicationLauncherUnreadifying.Add(RemoveToolbarButton);
        }

        private void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(AddToolbarButton);
            GameEvents.onGUIApplicationLauncherUnreadifying.Remove(RemoveToolbarButton);
            RemoveToolbarButton();
            if (instance == this) instance = null;
        }

        private void AddToolbarButton()
        {
            if (toolbarButton != null) return;
            toolbarButton = ApplicationLauncher.Instance.AddModApplication(
                onTrue: () => showPanel = true,
                onFalse: () => showPanel = false,
                onHover: null,
                onHoverOut: null,
                onEnable: null,
                onDisable: null,
                visibleInScenes: ApplicationLauncher.AppScenes.ALWAYS,
                texture: GameDatabase.Instance.GetTexture("KSPArchipelago/ap_icon", false)
                         ?? Texture2D.whiteTexture);
        }

        private void RemoveToolbarButton(GameScenes _ = default)
        {
            if (toolbarButton == null) return;
            ApplicationLauncher.Instance?.RemoveModApplication(toolbarButton);
            toolbarButton = null;
        }

        private void OnGUI()
        {
            if (!showPanel) return;
            windowRect = GUILayout.Window(
                id: 0xA9C1A60,
                screenRect: windowRect,
                func: DrawWindow,
                text: "Archipelago",
                options: GUILayout.Width(400));
        }

        private void DrawWindow(int id)
        {
            if (mod == null)
            {
                GUILayout.Label("Mod not found — check installation.");
                DrawCloseButton();
                GUI.DragWindow();
                return;
            }

            if (mod.IsConnected)
                DrawConnectedPanel();
            else
                DrawConnectionForm();

            DrawCloseButton();
            GUI.DragWindow();
        }

        private void DrawConnectedPanel()
        {
            GUILayout.Space(4);
            GUILayout.Label($"Status: Connected ({mod.ConnectedSlot})");
            GUILayout.Label($"Items received:      {mod.ItemsReceivedCount}");
            GUILayout.Label($"Locations checked:   {mod.LocationsCheckedCount}");
            GUILayout.Space(8);
            if (GUILayout.Button("Disconnect"))
                mod.HandleDisconnect();
        }

        private void DrawConnectionForm()
        {
            GUILayout.Space(4);
            GUILayout.Label("Status: Disconnected");
            GUILayout.Space(8);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Host", GUILayout.Width(80));
            host = GUILayout.TextField(host);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Port", GUILayout.Width(80));
            portStr = GUILayout.TextField(portStr, 5);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Slot name", GUILayout.Width(80));
            slot = GUILayout.TextField(slot);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Password", GUILayout.Width(80));
            password = GUILayout.PasswordField(password, '*');
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            if (GUILayout.Button("Connect"))
                TriggerConnect();
        }

        private void TriggerConnect()
        {
            if (!int.TryParse(portStr, out int port) || port < 1 || port > 65535)
            {
                ScreenMessages.PostScreenMessage(
                    "Invalid port number.", 3f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }
            if (mod?.Console == null) return;

            // Run on a background thread so the UI stays responsive.
            string h = host, s = slot, pw = string.IsNullOrEmpty(password) ? null : password;
            int p = port;
            var console = mod.Console;
            System.Threading.ThreadPool.QueueUserWorkItem(_ => console.RunConnectDirect(h, p, s, pw));
        }

        private void DrawCloseButton()
        {
            GUILayout.Space(4);
            if (GUILayout.Button("Close"))
                showPanel = false;
        }
    }
}
