// Per-save persistence of the chosen body spec.
//
// First load (no spec in node): default Kerbin. The starting body
// arrives later via AP slot_data — the main mod's HandleConnect calls
// StartingBodyHandler.OnStartingBodyResolved, which calls
// ApplyServerBody on this module. If AP connected before this module
// loaded, PendingBodyName carries the value across.
//
// Subsequent loads: stored spec drives materialisation. No AP needed
// to replay an already-bootstrapped save.

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
        public static string PendingBodyName = null;

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
                return;
            }

            // Fresh save with no spec. If AP already resolved a body in
            // this session, consume it now; otherwise default to Kerbin
            // and wait for the next HandleConnect to call ApplyServerBody.
            if (!string.IsNullOrEmpty(PendingBodyName))
            {
                string pending = PendingBodyName;
                PendingBodyName = null;
                Debug.Log($"[KSPArchipelago.KSC] Fresh save consuming pending body: {pending}");
                ApplyServerBody(pending);
                return;
            }

            BodyName = "Kerbin";
            Debug.Log("[KSPArchipelago.KSC] Fresh save — defaulting to Kerbin until AP connect.");
        }

        // Called by StartingBodyHandler after AP slot_data resolves the
        // starting body. Looks up the BodySpec, writes it into this
        // save's scenario data, and (if a scene is loaded) triggers
        // materialisation directly. Idempotent for the same body within
        // a session — Materialiser handles its own already-materialised
        // short-circuit, and mid-session switches are caught by the
        // existing CurrentBody guard.
        public void ApplyServerBody(string bodyName)
        {
            if (bodyName == "Kerbin")
            {
                BodyName = "Kerbin";
                Lat = Lon = TerrainAltM = 0;
                SkipMapDecal = false;
                return;
            }

            if (!BodyData.TryFindBody(bodyName, out BodySpec spec))
            {
                Debug.LogError($"[KSPArchipelago.KSC] AP requested unknown body '{bodyName}' — staying on Kerbin.");
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
