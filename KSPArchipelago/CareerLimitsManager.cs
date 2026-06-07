using System;
using System.Collections.Generic;
using UnityEngine;

namespace KSPArchipelago
{
    /// <summary>
    /// Installs APCareerGameVariables as GameVariables.Instance when the
    /// current save is Career mode; restores the original instance otherwise.
    ///
    /// Lifecycle:
    ///   - Loads as Instantly+DontDestroyOnLoad like the main mod.
    ///   - Subscribes to onGameStateLoad + onGameSceneLoadRequested.
    ///   - On each transition: if Career → install (idempotent), otherwise
    ///     → restore (idempotent).
    ///
    /// Test factors are hard-coded for now (see CareerProbe validation —
    /// 0.5x mass / 0.5x parts confirmed enforced at editor + pad). Once the
    /// AP-side generation ships, the values come from slot_data via a
    /// `SetBodyFactors(Dictionary)` call from KSPArchipelagoMod.HandleConnect.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class CareerLimitsManager : MonoBehaviour
    {
        public static CareerLimitsManager Instance { get; private set; }

        private GameVariables _originalGameVariables;
        private APCareerGameVariables _override;
        private GameObject _overrideHost;
        private bool _installed;

        // Diagnostic GUI for root-causing the Kerbin click-launch regression.
        // Top-left position so it's visible regardless of multi-monitor / ultra-wide setups.
        private bool _showDiagnosticGui = true;
        private Rect _guiRect = new Rect(20, 60, 320, 200);
        private bool _userSuppressed = false;  // diagnostic override of normal install logic
        private bool _loggedFirstOnGUI = false;

        // Hard-coded test factors. Replaced by slot_data when generation
        // side ships. 1.0 = stock; <1.0 tighter limits; >1.0 more generous.
        // Verified with these values in CareerProbe — VAB editor display +
        // pre-launch enforcement at Kerbin both react correctly.
        private static readonly Dictionary<string, float> _testFactors = new Dictionary<string, float>
        {
            { "GetCraftMassLimit",   1.0f },
            { "GetPartCountLimit",   1.0f },
            { "GetCraftSizeLimit",   1.0f },
            { "GetDSNRange",         1.0f },
        };

        void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(this);
            GameEvents.onGameStateLoad.Add(OnGameStateLoad);
            GameEvents.onGameSceneLoadRequested.Add(OnSceneLoad);
        }

        void OnDestroy()
        {
            GameEvents.onGameStateLoad.Remove(OnGameStateLoad);
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoad);
            Uninstall();
            if (Instance == this) Instance = null;
        }

        private void OnGameStateLoad(ConfigNode _) => Sync();
        private void OnSceneLoad(GameScenes _) => Sync();

        private void Sync()
        {
            // Diagnostic suppression: when the user has toggled the override
            // off via the in-game GUI, never re-install regardless of mode.
            // The user toggles it back to re-enable.
            if (_userSuppressed)
            {
                if (_installed) Uninstall();
                return;
            }
            bool careerMode = HighLogic.CurrentGame != null
                              && HighLogic.CurrentGame.Mode == Game.Modes.CAREER;
            if (careerMode) Install();
            else Uninstall();
        }

        void OnGUI()
        {
            if (!_loggedFirstOnGUI)
            {
                _loggedFirstOnGUI = true;
                Debug.Log($"[KSP-AP] DIAGNOSTIC GUI: first OnGUI fired "
                        + $"(scene={HighLogic.LoadedScene}, Screen.width={Screen.width})");
            }
            if (!_showDiagnosticGui) return;
            // Show in any scene — we want to be able to toggle anywhere.
            _guiRect = GUILayout.Window(0x4C3B7F, _guiRect, DrawDiagnosticWindow,
                "AP Career Limits — Diag");
        }

        private void DrawDiagnosticWindow(int id)
        {
            GUILayout.BeginVertical();
            GUILayout.Label($"Override installed: {_installed}");
            GUILayout.Label($"User suppressed: {_userSuppressed}");
            GUILayout.Label($"GameVariables.Instance type:");
            string typeName = "<null>";
            try { typeName = GameVariables.Instance?.GetType().FullName ?? "<null>"; } catch { }
            GUILayout.Label($"  {typeName}");

            string toggleLabel = _userSuppressed
                ? "RE-INSTALL APCareerGameVariables"
                : "UNINSTALL APCareerGameVariables (diag)";
            if (GUILayout.Button(toggleLabel))
            {
                _userSuppressed = !_userSuppressed;
                Debug.Log($"[KSP-AP] DIAGNOSTIC: user toggled override suppression "
                        + $"-> userSuppressed={_userSuppressed}. Re-syncing.");
                Sync();
            }
            if (GUILayout.Button("Dump LaunchPad component tree"))
            {
                DumpFacilityComponents("LaunchPad");
            }
            if (GUILayout.Button("Dump Runway component tree"))
            {
                DumpFacilityComponents("Runway");
            }
            if (GUILayout.Button("TOGGLE LaunchPad colliders enabled"))
            {
                ToggleFacilityColliders("LaunchPad");
            }
            if (GUILayout.Button("TOGGLE LaunchPad LaunchSiteFacility enabled"))
            {
                ToggleLaunchSiteFacility("LaunchPad");
            }
            if (GUILayout.Button("Dump launch site state NOW"))
            {
                string snap = LaunchSiteStateLogger.DumpNow();
                Debug.Log($"[KSP-AP] DIAG one-shot launch-site state: {snap}");
            }
            if (GUILayout.Button("Hide window")) _showDiagnosticGui = false;
            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        /// <summary>
        /// Replace public bodyFactors. Called from KSPArchipelagoMod once
        /// slot_data has been parsed. Re-applies factors on the live
        /// override instance if installed.
        /// </summary>
        public void SetBodyFactors(IDictionary<string, float> factors)
        {
            if (factors == null) return;
            // Update the testFactors snapshot so subsequent installs use
            // the latest values, AND apply to the live instance if any.
            foreach (var kv in factors) _testFactors[kv.Key] = kv.Value;
            if (_override != null)
            {
                _override.bodyFactors.Clear();
                foreach (var kv in _testFactors) _override.bodyFactors[kv.Key] = kv.Value;
                Debug.Log($"[KSP-AP] Career limits factors updated: {Stringify(_testFactors)}");
            }
        }

        private void Install()
        {
            if (_installed) return;
            try
            {
                _originalGameVariables = GameVariables.Instance;
                _overrideHost = new GameObject("APCareerGameVariablesHost");
                DontDestroyOnLoad(_overrideHost);
                _override = _overrideHost.AddComponent<APCareerGameVariables>();

                // APCareerGameVariables overrides only a few getters but inherits
                // every GameVariables DATA field — and a freshly AddComponent'd
                // instance has them all null. Reputation math, for one, evaluates
                // GameVariables.Instance.reputationAddition/Subtraction (curves);
                // a null curve NREs in ModifyReputationDelta on every rep change
                // (ours and KSP's own milestone awards). Copy all base fields from
                // the original so the override is a faithful stand-in.
                if (_originalGameVariables != null)
                {
                    foreach (var f in typeof(GameVariables).GetFields(
                        System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance))
                    {
                        try { f.SetValue(_override, f.GetValue(_originalGameVariables)); }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[KSP-AP] GameVariables field copy "
                                           + $"'{f.Name}' failed: {ex.Message}");
                        }
                    }
                }

                foreach (var kv in _testFactors) _override.bodyFactors[kv.Key] = kv.Value;
                GameVariables.Instance = _override;
                _installed = true;
                Debug.Log($"[KSP-AP] APCareerGameVariables installed (factors: {Stringify(_testFactors)})");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KSP-AP] APCareerGameVariables install failed: {ex}");
                _installed = false;
            }
        }

        private void Uninstall()
        {
            if (!_installed) return;
            try
            {
                GameVariables.Instance = _originalGameVariables;
                if (_overrideHost != null) Destroy(_overrideHost);
                _overrideHost = null;
                _override = null;
                _originalGameVariables = null;
                _installed = false;
                Debug.Log("[KSP-AP] APCareerGameVariables uninstalled");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KSP-AP] APCareerGameVariables uninstall failed: {ex}");
            }
        }

        // Dump every Component on the named facility's GameObject hierarchy.
        // We want to find what handles left-click "Enter" on the LaunchPad
        // so we can identify the click-launch trigger.
        private void DumpFacilityComponents(string facilityName)
        {
            Upgradeables.UpgradeableFacility target = null;
            foreach (var f in Resources.FindObjectsOfTypeAll<Upgradeables.UpgradeableFacility>())
            {
                if (f != null && f.name == facilityName) { target = f; break; }
            }
            if (target == null || target.gameObject == null)
            {
                Debug.LogError($"[KSP-AP] DIAG: facility '{facilityName}' not findable");
                return;
            }
            Debug.Log($"[KSP-AP] === DIAG Component dump for facility '{facilityName}' ===");
            Debug.Log($"[KSP-AP]   root GameObject: '{target.gameObject.name}' "
                    + $"active={target.gameObject.activeInHierarchy} "
                    + $"layer={target.gameObject.layer}");

            // Walk hierarchy, log every GameObject + its components.
            DumpGameObjectAndChildren(target.gameObject, 0);

            Debug.Log($"[KSP-AP] === END dump for '{facilityName}' ===");
        }

        // Toggle every Collider in the facility's hierarchy. Used to test
        // whether disabling click reception kills the Kerbin click-launch.
        private void ToggleFacilityColliders(string facilityName)
        {
            Upgradeables.UpgradeableFacility target = null;
            foreach (var f in Resources.FindObjectsOfTypeAll<Upgradeables.UpgradeableFacility>())
            {
                if (f != null && f.name == facilityName) { target = f; break; }
            }
            if (target == null) { Debug.LogError($"[KSP-AP] DIAG: '{facilityName}' not found"); return; }

            Collider[] colliders = target.gameObject.GetComponentsInChildren<Collider>(true);
            // Use the first collider's current state to flip them all uniformly.
            bool newEnabled = colliders.Length > 0 ? !colliders[0].enabled : false;
            int count = 0;
            foreach (var c in colliders)
            {
                if (c == null) continue;
                c.enabled = newEnabled;
                count++;
            }
            Debug.Log($"[KSP-AP] DIAG: toggled {count} Collider(s) on '{facilityName}' -> enabled={newEnabled}");
        }

        // Toggle the LaunchSiteFacility component (Squad's launch driver).
        private void ToggleLaunchSiteFacility(string facilityName)
        {
            Upgradeables.UpgradeableFacility target = null;
            foreach (var f in Resources.FindObjectsOfTypeAll<Upgradeables.UpgradeableFacility>())
            {
                if (f != null && f.name == facilityName) { target = f; break; }
            }
            if (target == null) { Debug.LogError($"[KSP-AP] DIAG: '{facilityName}' not found"); return; }

            int count = 0;
            bool? newEnabled = null;
            foreach (var mb in target.gameObject.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                if (mb.GetType().Name != "LaunchSiteFacility") continue;
                if (newEnabled == null) newEnabled = !mb.enabled;
                mb.enabled = newEnabled.Value;
                count++;
            }
            Debug.Log($"[KSP-AP] DIAG: toggled {count} LaunchSiteFacility component(s) "
                    + $"on '{facilityName}' -> enabled={newEnabled}");
        }

        private void DumpGameObjectAndChildren(GameObject go, int depth)
        {
            if (go == null) return;
            string indent = new string(' ', depth * 2);
            var comps = go.GetComponents<Component>();
            string compList = "";
            foreach (var c in comps)
            {
                if (c == null) { compList += "[null] "; continue; }
                compList += c.GetType().Name + " ";
            }
            Debug.Log($"[KSP-AP]   {indent}'{go.name}' "
                    + $"active={go.activeInHierarchy} layer={go.layer} "
                    + $"comps=[{compList.Trim()}]");
            // Log collider state specifically
            foreach (var col in go.GetComponents<Collider>())
            {
                if (col == null) continue;
                Debug.Log($"[KSP-AP]   {indent}  Collider: type={col.GetType().Name} "
                        + $"enabled={col.enabled} isTrigger={col.isTrigger}");
            }
            for (int i = 0; i < go.transform.childCount; i++)
            {
                DumpGameObjectAndChildren(go.transform.GetChild(i).gameObject, depth + 1);
            }
        }

        private static string Stringify(Dictionary<string, float> d)
        {
            var parts = new List<string>(d.Count);
            foreach (var kv in d) parts.Add($"{kv.Key}={kv.Value}");
            return string.Join(", ", parts.ToArray());
        }
    }
}
