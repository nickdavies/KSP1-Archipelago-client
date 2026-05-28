// Retarget the PlanetariumCamera to the alien home when the
// TrackingStation scene loads.  KSP's PlanetariumCamera.OnLevelLoaded
// unconditionally calls SetTarget(initialTarget), and initialTarget
// is a Unity-prefab-set MapObject reference pointing at Kerbin —
// it's never updated based on the current isHomeWorld flag.
//
// We could swap initialTarget directly, but Unity's Awake ordering
// is non-deterministic relative to PlanetariumCamera's OnSceneLoaded
// callback, so we'd race.  Instead, this addon polls for the first
// frame after scene load: if the camera target is Kerbin (the stomp
// destination), re-assert our home target.  Bounded to ~1s so it
// stops fighting before the user can legitimately click a vessel.

using System.Collections;
using UnityEngine;

namespace KSPArchipelago.KSC
{
    [KSPAddon(KSPAddon.Startup.TrackingStation, false)]
    public class TrackingStationGate : MonoBehaviour
    {
        // ~1 s at 60 fps.  Empirically: KSP's stomp lands within the
        // first few frames after scene load; nothing observed past
        // ~10 frames.  60 is generous safety margin.
        private const int EnforceFrames = 60;

        IEnumerator Start()
        {
            // Two frames of headroom for fetch/MapObject to be alive.
            yield return null;
            yield return null;

            string homeName = SelectorScenarioModule.Instance?.BodyName;
            if (string.IsNullOrEmpty(homeName) || homeName == "Kerbin")
            {
                yield break;
            }

            CelestialBody home = FlightGlobals.GetBodyByName(homeName);
            if (home == null)
            {
                Debug.LogWarning($"[KSPArchipelago.KSC] TrackingStationGate: body '{homeName}' not found");
                yield break;
            }

            int retargetCount = 0;
            for (int i = 0; i < EnforceFrames; i++)
            {
                if (PlanetariumCamera.fetch == null || home.MapObject == null)
                {
                    yield return null;
                    continue;
                }

                MapObject curTarget = PlanetariumCamera.fetch.target;
                CelestialBody curBody = curTarget?.GetReferenceBody();
                // Only re-assert when target is Kerbin (the stomp destination).
                // If user has clicked a vessel or a third body, leave them alone.
                if (curBody != null && curBody != home && curBody.name == "Kerbin")
                {
                    PlanetariumCamera.fetch.SetTarget(home.MapObject);
                    retargetCount++;
                }
                yield return null;
            }

            Debug.Log($"[KSPArchipelago.KSC] TrackingStationGate: enforced {homeName} target " +
                      $"({retargetCount} re-assertions over {EnforceFrames} frames)");
        }
    }
}
