using System;
using System.Collections.Generic;
using System.IO;

using UnityEngine;
using Archipelago.MultiClient.Net;

namespace KSPArchipelago
{
    // Persistent tracking state — serialized as JSON alongside the career save file.
    [Serializable]
    internal class KspApState
    {
        public HashSet<long> CheckedLocationIds = new HashSet<long>();
        // "BodyName|EventName" → number of slots already reported (0..check_scale)
        public Dictionary<string, int> BodyEventCounts = new Dictionary<string, int>();
        public bool StartingInventoryReported = false;
        public bool KerbinStagingDone = false;
        public bool KerbinFirstLaunchDone = false;
        public bool KerbinFirstLandingDone = false;
        // Altitude thresholds (in metres) already reported
        public HashSet<int> AltitudesReachedMeters = new HashSet<int>();
        // KSC biome names already reported
        public HashSet<string> KscBiomesVisited = new HashSet<string>();
    }

    /// <summary>
    /// Detects KSP mission events and reports them as Archipelago location checks.
    /// Call Initialize() after a successful AP connection.
    /// Call Update() from the MonoBehaviour Update loop for altitude polling.
    /// Call Shutdown() on disconnect.
    /// </summary>
    internal class MissionTracker
    {
        // Kerbin altitude check thresholds in metres (location names use km).
        private static readonly int[] KerbinAltThresholds = { 5000, 15000, 25000, 35000, 45000, 55000, 70000 };

        // Number of AP location slots per mission event, per body.
        // Kerbin is not in this table — it uses fixed individual location names.
        private static readonly Dictionary<string, int> BodyCheckScale = new Dictionary<string, int>
        {
            { "Mun",    1 }, { "Minmus", 1 }, { "Gilly", 1 }, { "Ike",  1 }, { "Kerbol", 1 },
            { "Moho",   2 }, { "Eve",    2 }, { "Duna",  2 }, { "Dres", 2 }, { "Jool",   2 },
            { "Bop",    2 }, { "Pol",    2 },
            { "Laythe", 3 }, { "Vall",   3 }, { "Tylo",  3 }, { "Eeloo", 3 },
        };

        // Number of Starting Inventory locations by difficulty value.
        private static readonly Dictionary<int, int> DifficultyStartingCount = new Dictionary<int, int>
        {
            { 0, 20 }, // casual
            { 1, 15 }, // normal
            { 2, 10 }, // expert
            { 3,  5 }, // insane
        };

        // KSC biome strings as they appear after "KerbinSrfLanded" in ScienceSubject.id
        // → AP location name to report.
        private static readonly Dictionary<string, string> KscBiomeToLocation = new Dictionary<string, string>
        {
            {"LaunchPad",        "KSC LaunchPad"},
            {"Runway",           "KSC Runway"},
            {"Administration",   "KSC Administration"},
            {"AstronautComplex", "KSC Astronaut Complex"},
            {"FlagPole",         "KSC Flag Pole"},
            {"SPH",              "KSC SPH"},
            {"VAB",              "KSC VAB"},
            {"TrackingStation",  "KSC Tracking Station"},
            {"MissionControl",   "KSC Mission Control"},
            {"Crawlerway",       "KSC Crawlerway"},
            {"R&D",              "KSC R&D"},
        };

        // Tech tree node_id → display_name  (from tech_tree.py — must stay in sync).
        internal static readonly Dictionary<string, string> TechDisplayNames = new Dictionary<string, string>
        {
            { "basicRocketry",             "Basic Rocketry" },
            { "generalRocketry",           "General Rocketry" },
            { "survivability",             "Survivability" },
            { "stability",                 "Stability" },
            { "advRocketry",               "Advanced Rocketry" },
            { "spaceExploration",          "Space Exploration" },
            { "advConstruction",           "Advanced Construction" },
            { "fieldScience",              "Field Science" },
            { "basicScience",              "Basic Science" },
            { "propulsionSystems",         "Propulsion Systems" },
            { "advExploration",            "Advanced Exploration" },
            { "landing",                   "Landing" },
            { "advAerodynamics",           "Advanced Aerodynamics" },
            { "scienceTech",               "Science Tech" },
            { "generalConstruction",       "General Construction" },
            { "heavyRocketry",             "Heavy Rocketry" },
            { "highAltitudeFlight",        "High Altitude Flight" },
            { "advLanding",                "Advanced Landing" },
            { "actuators",                 "Actuators" },
            { "electronics",               "Electronics" },
            { "ionPropulsion",             "Ion Propulsion" },
            { "precisionEngineering",      "Precision Engineering" },
            { "heavierRocketry",           "Heavier Rocketry" },
            { "nuclearPropulsion",         "Nuclear Propulsion" },
            { "specializedControl",        "Specialized Control" },
            { "unmannedTech",              "Unmanned Tech" },
            { "advScienceTech",            "Advanced Science Tech" },
            { "specializedConstruction",   "Specialized Construction" },
            { "largeElectrics",            "Large Electrics" },
            { "composites",               "Composites" },
            { "veryHeavyRocketry",         "Very Heavy Rocketry" },
            { "experimentalElectrics",     "Experimental Electrics" },
            { "highPerformanceFuelSystems","High Performance Fuel Systems" },
            { "advUnmannedTech",           "Advanced Unmanned Tech" },
            { "robotics",                  "Robotics" },
            { "experimentalMotors",        "Experimental Motors" },
            { "aerospaceComposites",       "Aerospace Composites" },
            { "fieldResearch",             "Field Research" },
            { "nanolathing",               "Nanolathing" },
            { "advancedMotors",            "Advanced Motors" },
            { "highPerformanceSystems",    "High Performance Systems" },
            { "metaMaterials",             "Meta-Materials" },
            { "ultimateRocketry",          "Ultimate Rocketry" },
        };

        private ArchipelagoSession session;
        private KspApState state;
        private string statePath;
        private bool initialized = false;
        private Action onLocationReported;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        /// <summary>
        /// Call after a successful AP connection. Loads persisted state, registers
        /// all KSP events, and immediately reports Starting Inventory locations.
        /// </summary>
        public void Initialize(ArchipelagoSession newSession, int difficulty, Action onLocationReported = null)
        {
            session = newSession;
            this.onLocationReported = onLocationReported;
            LoadState();
            RegisterEvents();
            ReportStartingInventory(difficulty);
            initialized = true;
        }

        /// <summary>Call on disconnect or mod destroy.</summary>
        public void Shutdown()
        {
            UnregisterEvents();
            session = null;
            initialized = false;
        }

        /// <summary>Call from MonoBehaviour.Update() for altitude polling.</summary>
        public void Update()
        {
            if (!initialized) return;
            PollKerbinAltitude();
        }

        // ------------------------------------------------------------------
        // Persistent state
        // ------------------------------------------------------------------

        private string BuildStatePath()
        {
            string folder = HighLogic.SaveFolder ?? "default";
            return Path.Combine(KSPUtil.ApplicationRootPath, "saves", folder, "ksp_ap_state.json");
        }

        private void LoadState()
        {
            statePath = BuildStatePath();
            if (File.Exists(statePath))
            {
                try
                {
                    string json = File.ReadAllText(statePath);
                    state = Newtonsoft.Json.JsonConvert.DeserializeObject<KspApState>(json) ?? new KspApState();
                    Debug.Log($"[KSP-AP] Loaded state: {state.CheckedLocationIds.Count} locations checked.");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[KSP-AP] State load failed: {ex.Message}. Starting fresh.");
                    state = new KspApState();
                }
            }
            else
            {
                state = new KspApState();
            }
        }

        private void SaveState()
        {
            if (state == null || statePath == null) return;
            try
            {
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(state, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(statePath, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[KSP-AP] State save failed: {ex.Message}");
            }
        }

        private void OnGameStateLoad(ConfigNode config)
        {
            // Save folder may have changed; reload state for the new campaign.
            if (initialized)
            {
                SaveState();     // flush current before switching
                LoadState();
            }
        }

        // ------------------------------------------------------------------
        // Event registration
        // ------------------------------------------------------------------

        private void RegisterEvents()
        {
            GameEvents.VesselSituation.onFlyBy.Add(
                new EventData<Vessel, CelestialBody>.OnEvent(OnFlyBy));
            GameEvents.VesselSituation.onOrbit.Add(
                new EventData<Vessel, CelestialBody>.OnEvent(OnOrbit));
            GameEvents.VesselSituation.onEscape.Add(
                new EventData<Vessel, CelestialBody>.OnEvent(OnEscape));
            GameEvents.VesselSituation.onLand.Add(
                new EventData<Vessel, CelestialBody>.OnEvent(OnLand));
            GameEvents.VesselSituation.onReturnFromOrbit.Add(
                new EventData<Vessel, CelestialBody>.OnEvent(OnReturnFromOrbit));
            GameEvents.VesselSituation.onReturnFromSurface.Add(
                new EventData<Vessel, CelestialBody>.OnEvent(OnReturnFromSurface));

            GameEvents.onVesselSituationChange.Add(
                new EventData<GameEvents.HostedFromToAction<Vessel, Vessel.Situations>>.OnEvent(OnSituationChange));
            GameEvents.onFlagPlant.Add(
                new EventData<Vessel>.OnEvent(OnFlagPlant));
            GameEvents.onStageSeparation.Add(
                new EventData<EventReport>.OnEvent(OnStageSeparation));
            GameEvents.onCrewOnEva.Add(
                new EventData<GameEvents.FromToAction<Part, Part>>.OnEvent(OnCrewOnEva));
            GameEvents.OnTechnologyResearched.Add(
                new EventData<GameEvents.HostTargetAction<RDTech, RDTech.OperationResult>>.OnEvent(OnTechResearched));

            // KSP misspells "Received" as "Recieved"
            GameEvents.OnScienceRecieved.Add(
                new EventData<float, ScienceSubject, ProtoVessel, bool>.OnEvent(OnScienceReceived));

            GameEvents.onGameStateLoad.Add(
                new EventData<ConfigNode>.OnEvent(OnGameStateLoad));
        }

        private void UnregisterEvents()
        {
            GameEvents.VesselSituation.onFlyBy.Remove(
                new EventData<Vessel, CelestialBody>.OnEvent(OnFlyBy));
            GameEvents.VesselSituation.onOrbit.Remove(
                new EventData<Vessel, CelestialBody>.OnEvent(OnOrbit));
            GameEvents.VesselSituation.onEscape.Remove(
                new EventData<Vessel, CelestialBody>.OnEvent(OnEscape));
            GameEvents.VesselSituation.onLand.Remove(
                new EventData<Vessel, CelestialBody>.OnEvent(OnLand));
            GameEvents.VesselSituation.onReturnFromOrbit.Remove(
                new EventData<Vessel, CelestialBody>.OnEvent(OnReturnFromOrbit));
            GameEvents.VesselSituation.onReturnFromSurface.Remove(
                new EventData<Vessel, CelestialBody>.OnEvent(OnReturnFromSurface));

            GameEvents.onVesselSituationChange.Remove(
                new EventData<GameEvents.HostedFromToAction<Vessel, Vessel.Situations>>.OnEvent(OnSituationChange));
            GameEvents.onFlagPlant.Remove(
                new EventData<Vessel>.OnEvent(OnFlagPlant));
            GameEvents.onStageSeparation.Remove(
                new EventData<EventReport>.OnEvent(OnStageSeparation));
            GameEvents.onCrewOnEva.Remove(
                new EventData<GameEvents.FromToAction<Part, Part>>.OnEvent(OnCrewOnEva));
            GameEvents.OnTechnologyResearched.Remove(
                new EventData<GameEvents.HostTargetAction<RDTech, RDTech.OperationResult>>.OnEvent(OnTechResearched));

            GameEvents.OnScienceRecieved.Remove(
                new EventData<float, ScienceSubject, ProtoVessel, bool>.OnEvent(OnScienceReceived));

            GameEvents.onGameStateLoad.Remove(
                new EventData<ConfigNode>.OnEvent(OnGameStateLoad));
        }

        // ------------------------------------------------------------------
        // Location reporting
        // ------------------------------------------------------------------

        /// Reports a location by name to the AP server, idempotent.
        private void ReportLocation(string name)
        {
            if (session == null) return;
            try
            {
                long id = session.Locations.GetLocationIdFromName(session.ConnectionInfo.Game, name);
                if (id < 0)
                {
                    Debug.LogWarning($"[KSP-AP] Unknown location: '{name}'");
                    return;
                }
                if (!state.CheckedLocationIds.Add(id))
                    return; // already reported
                session.Locations.CompleteLocationChecks(id);
                onLocationReported?.Invoke();
                Debug.Log($"[KSP-AP] Checked: {name}");
                SaveState();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[KSP-AP] ReportLocation failed for '{name}': {ex.Message}");
            }
        }

        // Reports the next unchecked slot for a body/event pair (up to check_scale).
        private void ReportBodyEvent(string bodyName, string eventName)
        {
            if (!BodyCheckScale.TryGetValue(bodyName, out int scale))
            {
                Debug.LogWarning($"[KSP-AP] No check_scale for body '{bodyName}'");
                return;
            }
            string key = $"{bodyName}|{eventName}";
            state.BodyEventCounts.TryGetValue(key, out int count);
            if (count >= scale)
                return; // all slots already reported
            count++;
            state.BodyEventCounts[key] = count;
            ReportLocation($"{bodyName} {eventName} {count}");
        }

        // ------------------------------------------------------------------
        // Starting inventory (zero-requirement locations reported on connect)
        // ------------------------------------------------------------------

        public void ReportStartingInventory(int difficulty)
        {
            if (state.StartingInventoryReported) return;
            int n = DifficultyStartingCount.TryGetValue(difficulty, out int c) ? c : 15;
            for (int i = 1; i <= n; i++)
                ReportLocation($"Starting Inventory {i}");
            state.StartingInventoryReported = true;
            SaveState();
        }

        // ------------------------------------------------------------------
        // KSC biome science detection
        // ------------------------------------------------------------------

        private void OnScienceReceived(float amount, ScienceSubject subject, ProtoVessel vessel, bool reverseEngineered)
        {
            if (subject == null) return;
            string id = subject.id;

            // Format: {experiment}@{body}{situation}{biome}
            // We want science done on Kerbin's surface at KSC biomes.
            const string kerbinLanded = "KerbinSrfLanded";
            int idx = id.IndexOf(kerbinLanded, StringComparison.Ordinal);
            if (idx < 0) return;

            string biome = id.Substring(idx + kerbinLanded.Length);
            if (string.IsNullOrEmpty(biome)) return;

            Debug.Log($"[KSP-AP] KSC science: subject='{id}', biome='{biome}'");

            // Try exact match first, then StartsWith for sub-biomes
            string locationName = null;
            if (KscBiomeToLocation.TryGetValue(biome, out string exact))
            {
                locationName = exact;
            }
            else
            {
                // Sub-biome fallback: "VABMainBuilding" → starts with "VAB"
                foreach (var kvp in KscBiomeToLocation)
                {
                    if (biome.StartsWith(kvp.Key, StringComparison.Ordinal))
                    {
                        locationName = kvp.Value;
                        break;
                    }
                }
            }

            if (locationName == null) return;
            if (!state.KscBiomesVisited.Add(locationName)) return; // already reported
            ReportLocation(locationName);
        }

        // ------------------------------------------------------------------
        // Kerbin altitude polling
        // ------------------------------------------------------------------

        private void PollKerbinAltitude()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.mainBody?.name != "Kerbin") return;
            if (v.Landed || v.Splashed) return;

            double alt = v.altitude;
            foreach (int threshold in KerbinAltThresholds)
            {
                if (alt >= threshold && state.AltitudesReachedMeters.Add(threshold))
                    ReportLocation($"Kerbin {threshold / 1000}km Altitude");
            }
        }

        // ------------------------------------------------------------------
        // KSP event handlers
        // ------------------------------------------------------------------

        private void OnFlyBy(Vessel vessel, CelestialBody body)
        {
            if (body.name == "Kerbin") return;
            ReportBodyEvent(body.name, "Flyby");
        }

        private void OnOrbit(Vessel vessel, CelestialBody body)
        {
            if (body.name == "Kerbin")
            {
                ReportLocation("Kerbin Orbit");
                return;
            }
            ReportBodyEvent(body.name, "Orbit");
        }

        private void OnEscape(Vessel vessel, CelestialBody body)
        {
            // Leaving Kerbin SOI is not a location; leaving any other body's SOI is.
            if (body.name == "Kerbin") return;
            ReportBodyEvent(body.name, "SOI Leave");
        }

        private void OnLand(Vessel vessel, CelestialBody body)
        {
            if (body.name == "Kerbin") return; // No "Kerbin Landing" location
            ReportBodyEvent(body.name, "Landing");
            if (vessel.GetCrewCount() > 0)
                ReportBodyEvent(body.name, "Crewed Landing");
        }

        private void OnSituationChange(GameEvents.HostedFromToAction<Vessel, Vessel.Situations> data)
        {
            Vessel v = data.host;
            string body = v?.mainBody?.name;

            // Kerbin Splashdown (onLand does not fire for SPLASHED)
            if (data.to == Vessel.Situations.SPLASHED && body == "Kerbin")
                ReportLocation("Kerbin Splashdown");

            // First Launch: PRELAUNCH → FLYING or SUB_ORBITAL on Kerbin
            if (body == "Kerbin"
                && data.from == Vessel.Situations.PRELAUNCH
                && (data.to == Vessel.Situations.FLYING || data.to == Vessel.Situations.SUB_ORBITAL)
                && !state.KerbinFirstLaunchDone)
            {
                state.KerbinFirstLaunchDone = true;
                ReportLocation("Kerbin First Launch");
            }

            // First Landing: transition to LANDED on Kerbin from FLYING or SUB_ORBITAL
            // (not from PRELAUNCH — that's sitting on the pad)
            if (body == "Kerbin"
                && data.to == Vessel.Situations.LANDED
                && (data.from == Vessel.Situations.FLYING || data.from == Vessel.Situations.SUB_ORBITAL)
                && !state.KerbinFirstLandingDone)
            {
                state.KerbinFirstLandingDone = true;
                ReportLocation("Kerbin First Landing");
            }
        }

        private void OnFlagPlant(Vessel flagVessel)
        {
            string body = flagVessel.mainBody?.name;
            if (body == null || body == "Kerbin") return;
            ReportBodyEvent(body, "Flag Plant");
        }

        // onReturnFromOrbit fires when a vessel lands on Kerbin after having orbited another body.
        private void OnReturnFromOrbit(Vessel vessel, CelestialBody body)
        {
            ReportBodyEvent(body.name, "Return");
        }

        // onReturnFromSurface fires when a vessel lands on Kerbin after having landed on another body.
        private void OnReturnFromSurface(Vessel vessel, CelestialBody body)
        {
            ReportBodyEvent(body.name, "Return");
            ReportBodyEvent(body.name, "Sample Return");
        }

        private void OnStageSeparation(EventReport report)
        {
            if (state.KerbinStagingDone) return;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.mainBody?.name != "Kerbin") return;
            state.KerbinStagingDone = true;
            ReportLocation("Kerbin First Staging");
        }

        private void OnCrewOnEva(GameEvents.FromToAction<Part, Part> action)
        {
            Vessel v = action.to?.vessel;
            if (v == null) return;
            if (v.mainBody?.name == "Kerbin" && v.situation == Vessel.Situations.ORBITING)
                ReportLocation("Kerbin EVA in Orbit");
        }

        private void OnTechResearched(GameEvents.HostTargetAction<RDTech, RDTech.OperationResult> action)
        {
            if (action.target != RDTech.OperationResult.Successful) return;
            string nodeId = action.host.techID;
            if (!TechDisplayNames.TryGetValue(nodeId, out string displayName))
            {
                Debug.LogWarning($"[KSP-AP] Researched unknown tech node: '{nodeId}'");
                return;
            }
            for (int slot = 1; slot <= 5; slot++)
                ReportLocation($"{displayName} {slot}");

            UnityEngine.Object.FindObjectOfType<TechTreeScout>()?.OnNodeChecked(nodeId);
        }
    }
}
