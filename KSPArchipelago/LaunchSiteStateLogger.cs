using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace KSPArchipelago
{
    /// <summary>
    /// Periodic snapshot of launch-site-relevant KSP state for root-causing
    /// the alien-start Kerbin-launch regression. Logs ONLY on state change
    /// (deduplicated by snapshot string), so we get a tight timeline without
    /// flooding output.
    ///
    /// Tracked state per tick:
    ///   - Each PSystemSetup.SpaceCenterFacility's editorFacility (LaunchPad,
    ///     Runway, and any "* LaunchPad"/"* Runway" alien entries)
    ///   - PSystemSetup.LaunchSites count and names
    ///   - EditorDriver.validLaunchSites contents (private static field via reflection)
    ///   - The Kerbin LaunchPad's LaunchSiteFacility component enabled state
    ///
    /// Also logs full snapshots on:
    ///   - Scene transitions (onLevelWasLoadedGUIReady)
    ///   - Launch events (GameEvents.onLaunch) — with the chosen launchSiteName
    ///
    /// Read-only — never mutates the state it inspects.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class LaunchSiteStateLogger : MonoBehaviour
    {
        private const float TickIntervalSec = 0.5f;
        private float _nextTick = 0f;
        private string _lastSnapshot = null;

        private static FieldInfo _validSitesField;

        void Awake()
        {
            DontDestroyOnLoad(this);
            try { GameEvents.onLaunch.Add(OnLaunch); } catch { }
            try { GameEvents.onLevelWasLoadedGUIReady.Add(OnSceneReady); } catch { }
            _validSitesField = typeof(EditorDriver).GetField(
                "validLaunchSites",
                BindingFlags.NonPublic | BindingFlags.Static
                | BindingFlags.Public | BindingFlags.Instance);
            Debug.Log("[KSP-AP] LaunchSiteStateLogger initialised "
                    + $"(validLaunchSites field found: {_validSitesField != null})");
        }

        void OnDestroy()
        {
            try { GameEvents.onLaunch.Remove(OnLaunch); } catch { }
            try { GameEvents.onLevelWasLoadedGUIReady.Remove(OnSceneReady); } catch { }
        }

        void Update()
        {
            if (Time.unscaledTime < _nextTick) return;
            _nextTick = Time.unscaledTime + TickIntervalSec;

            string snap = BuildSnapshot();
            if (snap != _lastSnapshot)
            {
                Debug.Log($"[KSP-AP] LSSL DELTA t={Time.unscaledTime:F2} "
                        + $"scene={HighLogic.LoadedScene}");
                Debug.Log($"[KSP-AP]   prev = {_lastSnapshot ?? "<initial>"}");
                Debug.Log($"[KSP-AP]   curr = {snap}");
                // Stack trace tells us which subsystem is mutating state.
                // System.Environment.StackTrace is heavy but only fires on change.
                try
                {
                    string st = System.Environment.StackTrace;
                    // Trim to the top ~20 frames to keep log readable.
                    var lines = st.Split('\n');
                    int max = Math.Min(20, lines.Length);
                    var sb = new StringBuilder();
                    for (int i = 0; i < max; i++) sb.AppendLine(lines[i]);
                    Debug.Log($"[KSP-AP]   stack:\n{sb}");
                }
                catch { }
                _lastSnapshot = snap;
            }
        }

        private void OnSceneReady(GameScenes scene)
        {
            Debug.Log($"[KSP-AP] LSSL SCENE-READY scene={scene} "
                    + $"state={BuildSnapshot()}");
        }

        private void OnLaunch(EventReport report)
        {
            string siteName = "<unknown>";
            try
            {
                if (EditorLogic.fetch != null) siteName = EditorLogic.fetch.launchSiteName;
            }
            catch { }
            string activeBody = "<none>";
            try
            {
                if (FlightGlobals.ActiveVessel != null
                    && FlightGlobals.ActiveVessel.mainBody != null)
                    activeBody = FlightGlobals.ActiveVessel.mainBody.name;
            }
            catch { }
            Debug.Log($"[KSP-AP] LSSL LAUNCH siteName='{siteName}' "
                    + $"activeBody='{activeBody}' state={BuildSnapshot()}");
        }

        public static string DumpNow()
        {
            // Public entry point for on-demand dumps from the diagnostic GUI.
            var l = FindObjectOfType<LaunchSiteStateLogger>();
            return l != null ? l.BuildSnapshot() : "<logger not active>";
        }

        private string BuildSnapshot()
        {
            var sb = new StringBuilder();

            // PSystemSetup.SpaceCenterFacilities — editorFacility per entry
            try
            {
                var ps = PSystemSetup.Instance;
                if (ps != null && ps.SpaceCenterFacilities != null)
                {
                    sb.Append("SCFs[");
                    foreach (var e in ps.SpaceCenterFacilities)
                    {
                        if (e == null) continue;
                        string n = e.facilityName ?? "<null>";
                        // Filter to LaunchPad/Runway-like entries
                        if (!(n == "LaunchPad" || n == "Runway"
                            || n.EndsWith("LaunchPad") || n.EndsWith("Runway")))
                            continue;
                        sb.Append($"{n}={e.editorFacility};");
                    }
                    sb.Append("] ");
                }
            }
            catch (Exception ex) { sb.Append($"SCFs<ex:{ex.Message}> "); }

            // PSystemSetup.LaunchSites — count + names
            try
            {
                var ps = PSystemSetup.Instance;
                if (ps != null)
                {
                    var ls = ps.LaunchSites;
                    sb.Append($"LS[{ls?.Count ?? -1}:");
                    if (ls != null)
                    {
                        foreach (var s in ls)
                        {
                            if (s != null) sb.Append($"{s.launchSiteName},");
                        }
                    }
                    sb.Append("] ");
                }
            }
            catch (Exception ex) { sb.Append($"LS<ex:{ex.Message}> "); }

            // EditorDriver.validLaunchSites
            try
            {
                if (_validSitesField != null)
                {
                    object v = _validSitesField.GetValue(null);
                    sb.Append("VLS[");
                    if (v is System.Collections.IEnumerable e)
                    {
                        int n = 0;
                        foreach (var x in e)
                        {
                            sb.Append($"{x},");
                            n++;
                        }
                        sb.Append($"]({n}) ");
                    }
                    else
                    {
                        sb.Append($"<not-enumerable:{v?.GetType().Name ?? "null"}>] ");
                    }
                }
                else
                {
                    sb.Append("VLS<field-missing> ");
                }
            }
            catch (Exception ex) { sb.Append($"VLS<ex:{ex.Message}> "); }

            // Kerbin LaunchPad's LaunchSiteFacility component enabled state
            try
            {
                var pads = Resources.FindObjectsOfTypeAll<Upgradeables.UpgradeableFacility>();
                bool seen = false;
                foreach (var f in pads)
                {
                    if (f == null || f.name != "LaunchPad") continue;
                    foreach (var mb in f.gameObject
                             .GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (mb == null) continue;
                        if (mb.GetType().Name != "LaunchSiteFacility") continue;
                        sb.Append($"LSF.enabled={mb.enabled} ");
                        seen = true;
                        break;
                    }
                    if (seen) break;
                }
                if (!seen) sb.Append("LSF<not-found> ");
            }
            catch (Exception ex) { sb.Append($"LSF<ex:{ex.Message}> "); }

            return sb.ToString();
        }
    }
}
