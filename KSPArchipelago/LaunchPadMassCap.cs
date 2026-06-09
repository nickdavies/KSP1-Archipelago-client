using System.Collections;
using System.Collections.Generic;
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

        private static KSPArchipelagoMod GetMod() =>
            FindObjectOfType<KSPArchipelagoMod>();

        private void Update()
        {
            if (EditorLogic.fetch == null) return;
            var ship = EditorLogic.fetch.ship;
            if (ship == null || ship.parts == null || ship.parts.Count == 0) return;

            float cap = LaunchPadGate.ComputeMassCap();
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

            float cap = LaunchPadGate.ComputeMassCap();
            if (float.IsPositiveInfinity(cap)) return;

            float mass = vessel.GetTotalMass();
            if (mass <= cap + LaunchPadGate.GraceTonnes) return;

            ScreenMessages.PostScreenMessage(
                $"<color=red>STRUCTURAL FAILURE:</color> vessel {mass:F2} t exceeds " +
                $"launch pad rating {cap:F0} t.\n" +
                "<color=yellow>Damn it Jeb, I told you it was too heavy for the pad!</color>",
                8f, ScreenMessageStyle.UPPER_CENTER);

            if (MessageSystem.Instance != null)
            {
                var msg = new MessageSystem.Message(
                    "Pad Structural Failure",
                    $"Damn it Jeb, I told you it was too heavy for the pad!\n\n" +
                    $"Vessel mass: {mass:F2} t\n" +
                    $"Pad rating: {cap:F0} t\n\n" +
                    "Upgrade the launch pad to raise the cap.",
                    MessageSystemButton.MessageButtonColor.ORANGE,
                    MessageSystemButton.ButtonIcons.FAIL);
                MessageSystem.Instance.AddMessage(msg);
            }

            StartCoroutine(DetonateAfterDelay(3f));
        }

        private IEnumerator DetonateAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);

            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || vessel.parts == null) yield break;

            // Copy first — Part.explode() mutates vessel.parts.
            var parts = new List<Part>(vessel.parts);
            foreach (var p in parts)
            {
                if (p != null) p.explode();
            }
        }
    }

    internal static class LaunchPadGate
    {
        // Float-drift tolerance; tight enough that players can't game an
        // extra part of meaningful mass out of it.
        public const float GraceTonnes = 0.01f;

        /// <summary>
        /// Single source of truth for the launch-pad mass cap. Reads the
        /// LaunchPad facility's current level from KSP and looks up the
        /// matching mass limit via GameVariables — which dispatches through
        /// APCareerGameVariables (installed by CareerLimitsManager on Career
        /// saves) so per-body factors are respected.
        ///
        /// Returns float.PositiveInfinity when no enforcement applies
        /// (LaunchPad at max level, or no facility findable).
        ///
        /// This unifies the legacy LaunchPadMassCap editor warning and the
        /// LaunchPadOverCapDetonator flight check with KK's MaxCraftMass on
        /// the alien KK pad — both now derive from the same per-facility-level
        /// formula, so the player never sees "VAB says X but pad blows up
        /// at Y" again.
        /// </summary>
        public static float ComputeMassCap()
        {
            // FindObjectsOfType doesn't return UpgradeableFacility in editor
            // scenes — but the LaunchPad's FacilityLevel reads correctly via
            // ScenarioUpgradeableFacilities even there (KK CareerState manages
            // it). Try the live-instance path first, fall back to the
            // ProtoUpgradeable on miss.
            float normLevel = ReadLaunchPadNormLevel();
            if (normLevel < 0f) return float.PositiveInfinity; // nothing to read
            // Second arg is isVAB (probe-confirmed) — positional because
            // Squad's parameter name differs across KSP versions.
            float cap = GameVariables.Instance.GetCraftMassLimit(normLevel, true);
            if (cap >= float.MaxValue / 2f) return float.PositiveInfinity;
            return cap;
        }

        private static float ReadLaunchPadNormLevel()
        {
            // Live UpgradeableFacility (SpaceCenter scene + KSC scene).
            foreach (var f in Resources.FindObjectsOfTypeAll<Upgradeables.UpgradeableFacility>())
            {
                if (f != null && f.name == "LaunchPad")
                {
                    try
                    {
                        // FacilityLevel is 0-indexed; KSP stock has 3 levels
                        // (0/1/2). normLevel = level / (maxLevel) = level / 2.
                        // MaxLevel reads from the upgradeLevels array length.
                        int maxLevel = f.MaxLevel; // 2 for stock 3-level facilities
                        if (maxLevel <= 0) maxLevel = 2;
                        return (float)f.FacilityLevel / (float)maxLevel;
                    }
                    catch { /* fall through */ }
                }
            }
            // Fallback: read from the persistent scenario node (works in any scene).
            try
            {
                var proto = ScenarioUpgradeableFacilities.protoUpgradeables;
                if (proto != null && proto.TryGetValue("SpaceCenter/LaunchPad", out var pu))
                {
                    if (pu?.facilityRefs != null && pu.facilityRefs.Count > 0)
                    {
                        var f = pu.facilityRefs[0];
                        int maxLevel = f.MaxLevel;
                        if (maxLevel <= 0) maxLevel = 2;
                        return (float)f.FacilityLevel / (float)maxLevel;
                    }
                }
            }
            catch { }
            return -1f;
        }
    }
}
