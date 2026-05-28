// KSPAddon orchestration.
//
// Two addons:
//   SelectorBootstrap — fires when SpaceCenter scene loads.  Waits
//     for SelectorScenarioModule.Instance to be loaded, invokes the
//     materialiser, then yields ~2 s before placing decorations
//     (matches AP_KSC_Probe's wait — Squad scene's decoration meshes
//     and renderer bounds aren't stable immediately at scene init).
//   EditorLaunchGate — fires on each editor entry, re-applies the
//     stock-launchpad hide and sets the alien LaunchPad as the
//     default launch site (LaunchSiteManager.setLaunchSite NREs in
//     the SpaceCenter scene because it writes to EditorLogic.fetch,
//     so we defer it to here where EditorLogic exists).

using System.Collections;
using KerbalKonstructs.Core;
using UnityEngine;

namespace KSPArchipelago.KSC
{
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public class SelectorBootstrap : MonoBehaviour
    {
        // Frames to wait for the scenario module to load before giving up.
        // OnLoad usually fires within the first frame of scene load, but
        // build/load order on slow machines can stretch this.
        private const int MaxWaitFrames = 120;

        void Start()
        {
            StartCoroutine(WaitForScenarioAndMaterialise());
        }

        IEnumerator WaitForScenarioAndMaterialise()
        {
            int waited = 0;
            while (SelectorScenarioModule.Instance == null && waited < MaxWaitFrames)
            {
                yield return null;
                waited++;
            }

            SelectorScenarioModule sm = SelectorScenarioModule.Instance;
            if (sm == null)
            {
                Debug.LogError($"[KSPArchipelago.KSC] Scenario module never loaded after {MaxWaitFrames} frames — aborting.");
                yield break;
            }

            if (sm.MaterialisedThisSession && Materialiser.CurrentBody == sm.BodyName)
            {
                yield break;
            }

            Materialiser.Materialise(sm.Spec);
            sm.MaterialisedThisSession = true;

            if (sm.BodyName == "Kerbin" || Materialiser.LastGroupCenter == null) yield break;

            yield return StartCoroutine(Decorations.WaitAndPlace(
                Materialiser.LastSpec,
                FlightGlobals.GetBodyByName(sm.BodyName),
                Materialiser.LastGroupCenter));
        }
    }

    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class EditorLaunchGate : MonoBehaviour
    {
        void Start()
        {
            if (Materialiser.CurrentBody == null || Materialiser.CurrentBody == "Kerbin") return;

            // Re-apply the picker hide in case anything between SpaceCenter
            // and the editor scene reset PSystemSetup.SpaceCenterFacilities
            // or KK reopened a Making History site.
            Materialiser.HideKerbinLaunchSites();

            // Default the launch button to the alien LaunchPad.  Must run
            // in the editor scene — LaunchSiteManager.setLaunchSite writes
            // EditorLogic.fetch.launchSiteName which is null in SpaceCenter.
            KKLaunchSite pad = LaunchSiteManager.GetLaunchSiteByName($"{Materialiser.CurrentBody} LaunchPad");
            if (pad != null)
            {
                LaunchSiteManager.setLaunchSite(pad);
            }
        }
    }
}
