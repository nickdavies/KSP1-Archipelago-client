using KSP.UI.Screens;
using UnityEngine;

namespace KSPArchipelago
{
    /// <summary>
    /// Enforces the Progressive Launch Pad mass cap.
    ///
    /// Two layers:
    ///   1) Editor warning — polls ship mass every frame (throttled) and posts
    ///      a screen message when over cap. Polling (rather than only
    ///      onEditorShipModified) catches PAW fuel-slider changes, which
    ///      onEditorShipModified does not fire on.
    ///   2) Pad RUD — when an over-cap vessel arrives in the flight scene
    ///      in PRELAUNCH, the structure "fails": every part is exploded
    ///      via Part.explode() after a short delay.
    ///
    /// The 0.01 t grace absorbs float drift without leaving exploitable room.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class LaunchPadMassCap : MonoBehaviour
    {
        private float _lastWarnTime = -10f;
        private const float WarnIntervalSec = 1.5f;

        private void Update()
        {
            if (EditorLogic.fetch == null) return;
            var ship = EditorLogic.fetch.ship;
            if (ship == null || ship.parts == null || ship.parts.Count == 0) return;

            float cap = LaunchPadGate.ServerMassCap();
            if (float.IsPositiveInfinity(cap)) return;

            float mass = ship.GetTotalMass();
            if (mass <= cap + LaunchPadGate.GraceTonnes) return;

            if (Time.unscaledTime - _lastWarnTime < WarnIntervalSec) return;
            _lastWarnTime = Time.unscaledTime;

            ScreenMessages.PostScreenMessage(
                $"<color=orange>Vessel mass {mass:F2} t exceeds launch pad cap {cap:F0} t</color>\n" +
                $"<color=yellow>Upgrade the launch pad to raise the cap</color>",
                3f, ScreenMessageStyle.UPPER_CENTER);
        }
    }

    /// <summary>
    /// On flight-scene entry: if the active vessel is in PRELAUNCH and over
    /// the current pad cap, post a structural-failure message and explode it.
    /// Re-checks on every flight-scene load, so revert-to-launch can't bypass.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class LaunchPadOverCapDetonator : MonoBehaviour
    {
        private void Start()
        {
            GameEvents.onFlightReady.Add(OnFlightReady);
        }

        private void OnDestroy()
        {
            GameEvents.onFlightReady.Remove(OnFlightReady);
        }

        private void OnFlightReady()
        {
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) return;
            if (vessel.situation != Vessel.Situations.PRELAUNCH) return;

            float cap = LaunchPadGate.ServerMassCap();
            if (float.IsPositiveInfinity(cap)) return;

            float mass = vessel.GetTotalMass();
            if (mass <= cap + LaunchPadGate.GraceTonnes) return;

            VesselDestruction.Destroy(
                this, vessel,
                screenMessage:
                    $"<color=red>STRUCTURAL FAILURE:</color> vessel {mass:F2} t exceeds " +
                    $"launch pad rating {cap:F0} t.\n" +
                    "<color=yellow>Damn it Jeb, I told you it was too heavy for the pad!</color>",
                messageTitle: "Pad Structural Failure",
                messageBody:
                    "Damn it Jeb, I told you it was too heavy for the pad!\n\n" +
                    $"Vessel mass: {mass:F2} t\n" +
                    $"Pad rating: {cap:F0} t\n\n" +
                    "Upgrade the launch pad to raise the cap.",
                delay: 3f);
        }
    }

    internal static class LaunchPadGate
    {
        // Float-drift tolerance; tight enough that players can't game an
        // extra part of meaningful mass out of it.
        public const float GraceTonnes = 0.01f;

        /// <summary>
        /// The authoritative launch-pad mass cap: the Progressive Launch Pad
        /// server-provided thresholds indexed by the count of collected items
        /// (KSPArchipelagoMod.CurrentLaunchPadMassCap) — the same value written
        /// into the building via APCareerGameVariables.GetCraftMassLimit.
        /// PositiveInfinity when the option is off or the top tier is reached.
        ///
        /// This READS the source of truth directly rather than deriving a cap
        /// from the live LaunchPad facility level: the career hack maxes that
        /// facility, so a level-derived cap would always read "unlimited". The
        /// editor warning and the pre-launch detonator both gate on this, so
        /// the cap binds regardless of the maxed pad.
        /// </summary>
        public static float ServerMassCap()
        {
            var mod = UnityEngine.Object.FindObjectOfType<KSPArchipelagoMod>();
            return mod != null ? mod.CurrentLaunchPadMassCap : float.PositiveInfinity;
        }
    }
}
