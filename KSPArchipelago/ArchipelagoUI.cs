using System;
using System.IO;
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
        private bool wasConnected = false;
        // Height 0 lets GUILayout.Window auto-size to fit content.
        private Rect windowRect = new Rect(Screen.width / 2 - 200, Screen.height / 2 - 150, 400, 0);

        // Connection form fields.
        private string host = "localhost";
        private string portStr = "38281";
        private string slot = "";
        private string password = "";

        private static string ConfigPath
        {
            get
            {
                string folder = HighLogic.SaveFolder ?? "default";
                return Path.Combine(KSPUtil.ApplicationRootPath, "saves", folder, "ksp_ap_connection.cfg");
            }
        }

        private void Start()
        {
            instance = this;
            mod = FindObjectOfType<KSPArchipelagoMod>();

            LoadConnectionSettings();

            GameEvents.onGUIApplicationLauncherReady.Add(AddToolbarButton);
            GameEvents.onGUIApplicationLauncherUnreadifying.Add(RemoveToolbarButton);
        }

        private void Update()
        {
            if (mod == null) return;

            // Auto-close the panel on successful connection (false → true transition).
            if (mod.IsConnected && !wasConnected)
            {
                showPanel = false;
                if (toolbarButton != null)
                    toolbarButton.SetFalse(false);
            }
            wasConnected = mod.IsConnected;

            bool shouldLock = !mod.IsConnected
                && HighLogic.LoadedScene == GameScenes.SPACECENTER;
            if (shouldLock)
            {
                InputLockManager.SetControlLock(
                    ControlTypes.KSC_FACILITIES, "AP_NotConnected");
                showPanel = true;
            }
            else
            {
                InputLockManager.RemoveControlLock("AP_NotConnected");
            }
        }

        private void OnDestroy()
        {
            InputLockManager.RemoveControlLock("AP_NotConnected");
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

        private Vector2 goalScrollPos;

        private void DrawConnectedPanel()
        {
            GUILayout.Space(4);
            GUILayout.Label($"Status: Connected ({mod.ConnectedSlot})");
            GUILayout.Label($"Items received:      {mod.ItemsReceivedCount}");
            GUILayout.Label($"Locations checked:   {mod.LocationsCheckedCount}");

            // Goal progress checklist
            int goalTotal = mod.GoalLocationCount;
            if (goalTotal > 0)
            {
                int goalDone = mod.GoalLocationsChecked;
                string goalName = mod.GoalDisplayName ?? "Unknown";
                GUILayout.Space(4);
                GUILayout.Label($"Goal: {goalName} ({goalDone}/{goalTotal})");

                var status = mod.GetGoalStatus();
                if (goalTotal <= 15)
                {
                    foreach (var entry in status)
                    {
                        string mark = entry.Value ? "\u2713" : "\u25CB";
                        GUILayout.Label($"  {mark} {entry.Key}");
                    }
                }
                else
                {
                    goalScrollPos = GUILayout.BeginScrollView(goalScrollPos, GUILayout.MaxHeight(400));
                    foreach (var entry in status)
                    {
                        string mark = entry.Value ? "\u2713" : "\u25CB";
                        GUILayout.Label($"  {mark} {entry.Key}");
                    }
                    GUILayout.EndScrollView();
                }
            }

            GUILayout.Space(8);
            if (GUILayout.Button("Re-award All Items"))
            {
                var scenario = ApScenarioModule.Instance;
                if (scenario != null && ResearchAndDevelopment.Instance != null)
                {
                    // Undo all previously awarded science.
                    ResearchAndDevelopment.Instance.AddScience(
                        -scenario.TotalApScienceAwarded, TransactionReasons.Cheating);
                    scenario.TotalApScienceAwarded = 0;
                    scenario.AwardedItemIndices.Clear();
                }
                KSPArchipelagoPartsManager.ScrubTechTree();
                KSPArchipelagoPartsManager.ClearAllExperimentalParts();
                mod.ResetProgressiveState();
                mod.ProcessAllItems();
                KSPArchipelagoPartsManager.ReconcileApScience(mod.Session);
            }
            GUILayout.Space(4);
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
            bool enterPressed = Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return
                    || Event.current.keyCode == KeyCode.KeypadEnter);
            if (GUILayout.Button("Connect") || enterPressed)
            {
                TriggerConnect();
                if (enterPressed) Event.current.Use();
            }
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

            SaveConnectionSettings();

            string h = host, s = slot, pw = string.IsNullOrEmpty(password) ? null : password;
            int p = port;
            var console = mod.Console;
            System.Threading.ThreadPool.QueueUserWorkItem(_ => console.RunConnectDirect(h, p, s, pw));
        }

        private bool IsConnectionRequired =>
            mod != null && !mod.IsConnected
            && HighLogic.LoadedScene == GameScenes.SPACECENTER;

        private void DrawCloseButton()
        {
            GUILayout.Space(4);
            if (IsConnectionRequired)
            {
                GUILayout.Label("Connect to play.");
            }
            else if (GUILayout.Button("Close"))
            {
                showPanel = false;
                if (toolbarButton != null)
                    toolbarButton.SetFalse(false);
            }
        }

        private void LoadConnectionSettings()
        {
            if (!File.Exists(ConfigPath)) return;
            try
            {
                var node = ConfigNode.Load(ConfigPath);
                if (node == null) return;
                var conn = node.GetNode("CONNECTION");
                if (conn == null) return;
                host = conn.GetValue("host") ?? host;
                portStr = conn.GetValue("port") ?? portStr;
                slot = conn.GetValue("slot") ?? slot;
                password = conn.GetValue("password") ?? password;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[KSP-AP] Failed to load connection settings: {e.Message}");
            }
        }

        private void SaveConnectionSettings()
        {
            try
            {
                var node = new ConfigNode();
                var conn = node.AddNode("CONNECTION");
                conn.AddValue("host", host);
                conn.AddValue("port", portStr);
                conn.AddValue("slot", slot);
                conn.AddValue("password", password);
                node.Save(ConfigPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[KSP-AP] Failed to save connection settings: {e.Message}");
            }
        }
    }
}
