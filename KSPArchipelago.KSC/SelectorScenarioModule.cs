// Per-save persistence of the chosen body spec.
//
// First load (no spec in node): default Kerbin. The starting body
// arrives later via AP slot_data — the main mod's HandleConnect calls
// StartingBodyHandler.OnStartingBodyResolved, which calls
// ApplyServerBody on this module. If AP connected before this module
// loaded, PendingSpec carries the resolved spec across.
//
// Subsequent loads: stored spec drives materialisation. No AP needed
// to replay an already-bootstrapped save.

using System;
using UnityEngine;

namespace KSPArchipelago.KSC
{
    [KSPScenario(ScenarioCreationOptions.AddToAllGames,
        GameScenes.SPACECENTER, GameScenes.FLIGHT, GameScenes.TRACKSTATION,
        GameScenes.EDITOR)]
    public class SelectorScenarioModule : ScenarioModule
    {
        public static SelectorScenarioModule Instance { get; private set; }

        // Set by StartingBodyHandler when slot_data is parsed before any
        // SelectorScenarioModule exists in the scene. Consumed by the
        // next OnLoad. Static lifetime spans scene changes.
        public static BodySpec? PendingSpec = null;

        public string BodyName = "Kerbin";
        public double Lat;
        public double Lon;
        public double TerrainAltM;
        public bool   SkipMapDecal;

        // True after Materialiser ran for this spec in this session.
        // Not persisted — runtime statics don't survive a KSP restart,
        // so we always re-materialise on a fresh KSP launch.
        public bool MaterialisedThisSession;

        public BodySpec Spec => new BodySpec
        {
            Name         = BodyName,
            Lat          = Lat,
            Lon          = Lon,
            TerrainAltM  = TerrainAltM,
            SkipMapDecal = SkipMapDecal,
        };

        public override void OnAwake()
        {
            Instance = this;
        }

        public override void OnLoad(ConfigNode node)
        {
            if (node.HasValue("BodyName"))
            {
                BodyName     = node.GetValue("BodyName");
                double.TryParse(node.GetValue("Lat"),         out Lat);
                double.TryParse(node.GetValue("Lon"),         out Lon);
                double.TryParse(node.GetValue("TerrainAltM"), out TerrainAltM);
                bool.TryParse  (node.GetValue("SkipMapDecal"), out SkipMapDecal);
                Debug.Log($"[KSPArchipelago.KSC] Loaded spec from save: {BodyName}");
                ApplyHomeFlagFromOnLoad();
                // Spec found in save => AP previously resolved this body
                // for this save. Skip the preemptive hide on next SC entry.
                Materialiser.ApHomeConfirmed = true;
                // Make sure click-on-Kerbin-LaunchPad routes to the saved
                // body's alien pad from the first SC entry, before
                // Materialiser has had a chance to run.
                Materialiser.SetDefaultLaunchSitesForBody(BodyName);
                return;
            }

            // Fresh save with no spec. If AP already resolved a body in
            // this session, consume it now; otherwise default to Kerbin
            // and wait for the next HandleConnect to call ApplyServerBody.
            if (PendingSpec != null)
            {
                BodySpec pending = PendingSpec.Value;
                PendingSpec = null;
                Debug.Log($"[KSPArchipelago.KSC] Fresh save consuming pending body: {pending.Name}");
                ApplyServerBody(pending);
                return;
            }

            BodyName = "Kerbin";
            // Note: do NOT set ApHomeConfirmed here. BodyName=Kerbin is a
            // placeholder until AP actually confirms; leaving the flag
            // false keeps PreflightHideStockKerbinPads active so a
            // pre-AP-connect click can't launch from the stock Kerbin pad.
            Debug.Log("[KSPArchipelago.KSC] Fresh save — defaulting to Kerbin until AP connect.");
        }

        // Called by StartingBodyHandler after AP slot_data resolves the
        // starting body. The spec (name + landing coordinate) comes
        // straight from the server's `ksc_site` slot_data row — the client
        // no longer carries a per-body table. Writes it into this save's
        // scenario data and (if a scene is loaded) triggers materialisation
        // directly. Idempotent for the same body within a session —
        // Materialiser handles its own already-materialised short-circuit,
        // and mid-session switches are caught by the existing CurrentBody
        // guard.
        public void ApplyServerBody(BodySpec spec)
        {
            // AP has now told us the starting body for this save. From here
            // on, LiveLimitsSync uses CurrentBody (not the preemptive hide)
            // to decide whether stock Kerbin pads are launchable.
            Materialiser.ApHomeConfirmed = true;

            if (spec.Name == "Kerbin")
            {
                BodyName = "Kerbin";
                Lat = Lon = TerrainAltM = 0;
                SkipMapDecal = false;
                // PreflightHideStockKerbinPads set editorFacility=None on
                // the stock Kerbin pads while we waited for AP. Now that
                // we know it's a Kerbin start, restore them so the player
                // can actually launch.
                try { Materialiser.RestoreStockKerbinLaunchSites(); }
                catch (Exception ex)
                {
                    Debug.LogError("[KSPArchipelago.KSC] RestoreStockKerbinLaunchSites failed: " + ex);
                }
                return;
            }

            BodyName     = spec.Name;
            Lat          = spec.Lat;
            Lon          = spec.Lon;
            TerrainAltM  = spec.TerrainAltM;
            SkipMapDecal = spec.SkipMapDecal;
            ApplyHomeFlagFromOnLoad();

            // SelectorBootstrap may have already materialised the
            // default (Kerbin) for this scene before AP connected.
            // Force a re-materialise to the AP-chosen body. Materialiser
            // will hit its mid-session-switch toast if the previous
            // body was non-Kerbin; the Kerbin → alien transition is the
            // common case and is handled inline.
            MaterialisedThisSession = false;
            Materialiser.CurrentBody = null;
            Materialiser.Materialise(Spec);
            MaterialisedThisSession = true;

            // SelectorBootstrap already yielded break at the Kerbin
            // guard before AP connected, so the decoration coroutine
            // never ran.  Kick it now: StartingBodyHandler is
            // DontDestroyOnLoad, so the coroutine survives scene
            // transitions if AP-connect arrived outside SpaceCenter.
            if (Materialiser.LastGroupCenter != null && StartingBodyHandler.Instance != null)
            {
                CelestialBody body = FlightGlobals.GetBodyByName(BodyName);
                if (body != null)
                {
                    StartingBodyHandler.Instance.SchedulePlaceDecorations(
                        Materialiser.LastSpec, body, Materialiser.LastGroupCenter);
                }
            }
        }

        // Flip KSP's home-body designation as early as possible.
        // OnLoad fires before scene-specific init (tracking station,
        // map view), so doing the swap here lets those scenes pick up
        // the correct home flag.  Materialiser.FlipHomeFlag is
        // idempotent — also runs from the SpaceCenter bootstrap, the
        // second call no-ops via its built-in detection log line.
        // CommNet relocation stays in Materialiser because it needs
        // FindObjectsOfType<CommNetHome>, which has nothing to find
        // until the scene's body objects are alive.
        private void ApplyHomeFlagFromOnLoad()
        {
            if (BodyName == "Kerbin") return;
            if (FlightGlobals.Bodies == null) return;
            CelestialBody newHome = FlightGlobals.GetBodyByName(BodyName);
            if (newHome == null)
            {
                Debug.LogWarning($"[KSPArchipelago.KSC] OnLoad: body '{BodyName}' not found in " +
                                 $"FlightGlobals — deferring home flip to Materialiser.");
                return;
            }
            Materialiser.FlipHomeFlag(newHome);
        }

        public override void OnSave(ConfigNode node)
        {
            node.AddValue("BodyName",     BodyName);
            node.AddValue("Lat",          Lat);
            node.AddValue("Lon",          Lon);
            node.AddValue("TerrainAltM",  TerrainAltM);
            node.AddValue("SkipMapDecal", SkipMapDecal);
        }
    }
}
