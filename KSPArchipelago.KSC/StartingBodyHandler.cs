// Bridge between KSPArchipelago.dll (main, KK-free) and the
// materialiser in this assembly. Registers itself with
// StartingBodyBridge on game launch; the main mod calls back into it
// from HandleConnect once slot_data is parsed.
//
// Also doubles as the cross-scene MonoBehaviour host for the
// decoration coroutine kicked off by ApplyServerBody — SelectorBootstrap
// only lives during SpaceCenter, but DontDestroyOnLoad keeps this
// instance available regardless of the active scene.

using KerbalKonstructs.Core;
using UnityEngine;

namespace KSPArchipelago.KSC
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class StartingBodyHandler : MonoBehaviour, IStartingBodyHandler
    {
        public static StartingBodyHandler Instance { get; private set; }

        void Awake()
        {
            if (!KKLoaded())
            {
                Debug.LogWarning("[KSPArchipelago.KSC] KerbalKonstructs not detected — " +
                                 "leaving StartingBodyBridge unregistered. Non-Kerbin " +
                                 "starting bodies will be rejected at AP connect.");
                Destroy(this);
                return;
            }
            DontDestroyOnLoad(this);
            Instance = this;
            StartingBodyBridge.SetHandler(this);
            GameEvents.onLaunch.Add(OnLaunch);
            Debug.Log("[KSPArchipelago.KSC] Registered starting-body handler.");
        }

        void OnDestroy()
        {
            GameEvents.onLaunch.Remove(OnLaunch);
        }

        // Belt-and-braces behind the launch-site picker gate: if anything
        // bypasses it (a mod, a console command), a launch from the stock
        // Kerbin pads on an alien run gets a clear on-screen warning instead
        // of dumping the player on Kerbin with no way back to AP logic.
        // Instance method on purpose: GameEvents.Add reads the delegate's
        // Target and NREs on a static handler.
        private void OnLaunch(EventReport report)
        {
            string body = Materialiser.CurrentBody;
            if (body == null || body == "Kerbin") return;
            string site = EditorLogic.fetch != null ? EditorLogic.fetch.launchSiteName : null;
            if (site == "LaunchPad" || site == "Runway")
            {
                ScreenMessages.PostScreenMessage(
                    $"Stock KSC pads are disabled when starting on {body}. " +
                    $"Use the \"{body} LaunchPad\" or \"{body} Runway\" instead.",
                    8f, ScreenMessageStyle.UPPER_CENTER);
            }
        }

        // Detect KK via the loaded-assembly list rather than typeof(KK.Type)
        // — the latter would itself fail to JIT when KK is missing, so the
        // probe would never run.  AssemblyLoader.loadedAssemblies is KSP's
        // own assembly list and is safe to enumerate without KK present.
        private static bool KKLoaded()
        {
            foreach (var la in AssemblyLoader.loadedAssemblies)
            {
                if (la?.assembly?.GetName().Name == "KerbalKonstructs") return true;
            }
            return false;
        }

        public void OnStartingBodyResolved(string bodyName, double lat, double lon,
                                           double terrainAlt, bool skipMapDecal)
        {
            BodySpec spec = new BodySpec
            {
                Name         = bodyName,
                Lat          = lat,
                Lon          = lon,
                TerrainAltM  = terrainAlt,
                SkipMapDecal = skipMapDecal,
            };

            SelectorScenarioModule sm = SelectorScenarioModule.Instance;
            if (sm == null)
            {
                // No scenario module yet — typically because AP-connect
                // happened before any save loaded. Stash the choice;
                // the next SelectorScenarioModule.OnLoad consumes it.
                Debug.Log($"[KSPArchipelago.KSC] OnStartingBodyResolved({bodyName}) " +
                          "before any scenario module — stashing for next OnLoad.");
                SelectorScenarioModule.PendingSpec = spec;
                return;
            }
            sm.ApplyServerBody(spec);
        }

        // Recompute the alien LaunchPad's KKLaunchSite.MaxCraftMass after the
        // Progressive Launch Pad cap changed. Materialiser.ComputeMaxCraftMass
        // reads through GameVariables.GetCraftMassLimit, which the main mod has
        // just updated, so this picks up the new absolute cap. Safe no-op on
        // Kerbin starts (no alien launch site exists).
        public void RefreshPadMassCap()
        {
            try { Materialiser.UpdateAlienPadMaxCraftMass("SpaceCenter/LaunchPad"); }
            catch (System.Exception ex)
            {
                Debug.LogError("[KSPArchipelago.KSC] RefreshPadMassCap failed: " + ex);
            }
        }

        // Kick the decoration-placement coroutine.  Hosted here (not on
        // SelectorScenarioModule or SelectorBootstrap) so it survives
        // scene transitions — relevant when AP connects from a non-
        // SpaceCenter scene and we want the placement to wait out the
        // scene change rather than immediately time out.
        public void SchedulePlaceDecorations(BodySpec spec, CelestialBody body, GroupCenter gc)
        {
            StartCoroutine(Decorations.WaitAndPlace(spec, body, gc));
        }
    }
}
