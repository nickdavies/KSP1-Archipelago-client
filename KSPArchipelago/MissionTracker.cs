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

        // Number of AP location slots per event type (must match EVENT_SCALE in locations.py).
        private static readonly Dictionary<string, int> EventScale = new Dictionary<string, int>
        {
            { "Flyby", 1 }, { "SOI Leave", 1 }, { "Orbit", 1 },
            { "Landing", 2 }, { "Crewed Landing", 2 }, { "Flag Plant", 2 },
            { "Return", 3 }, { "Sample Return", 3 },
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

        // Tech tree node_id → display_name  (from data/tech_tree.json — must stay in sync).
        // 62 purchasable stock nodes across tiers 1-8.
        internal static readonly Dictionary<string, string> TechDisplayNames = new Dictionary<string, string>
        {
            // Tier 1
            { "basicRocketry",                   "Basic Rocketry" },
            { "engineering101",                  "Engineering 101" },
            // Tier 2
            { "generalRocketry",                 "General Rocketry" },
            { "stability",                       "Stability" },
            { "survivability",                   "Survivability" },
            // Tier 3
            { "advRocketry",                     "Advanced Rocketry" },
            { "aviation",                        "Aviation" },
            { "basicScience",                    "Basic Science" },
            { "flightControl",                   "Flight Control" },
            { "generalConstruction",             "General Construction" },
            // Tier 4
            { "advConstruction",                 "Advanced Construction" },
            { "advFlightControl",                "Advanced Flight Control" },
            { "aerodynamicSystems",              "Aerodynamics" },
            { "electrics",                       "Electrics" },
            { "fuelSystems",                     "Fuel Systems" },
            { "heavyRocketry",                   "Heavy Rocketry" },
            { "landing",                         "Landing" },
            { "miniaturization",                 "Miniaturization" },
            { "propulsionSystems",               "Propulsion Systems" },
            { "spaceExploration",                "Space Exploration" },
            // Tier 5
            { "actuators",                       "Actuators" },
            { "advAerodynamics",                 "Advanced Aerodynamics" },
            { "advElectrics",                    "Advanced Electrics" },
            { "advExploration",                  "Advanced Exploration" },
            { "advFuelSystems",                  "Adv. Fuel Systems" },
            { "advLanding",                      "Advanced Landing" },
            { "commandModules",                  "Command Modules" },
            { "heavierRocketry",                 "Heavier Rocketry" },
            { "precisionEngineering",            "Precision Engineering" },
            { "precisionPropulsion",             "Precision Propulsion" },
            { "specializedConstruction",         "Specialized Construction" },
            { "specializedControl",              "Specialized Control" },
            { "supersonicFlight",                "Supersonic Flight" },
            // Tier 6
            { "advMetalworks",                   "Advanced MetalWorks" },
            { "composites",                      "Composites" },
            { "electronics",                     "Electronics" },
            { "fieldScience",                    "Field Science" },
            { "heavyAerodynamics",               "Heavy Aerodynamics" },
            { "heavyLanding",                    "Heavy Landing" },
            { "highAltitudeFlight",              "High Altitude Flight" },
            { "largeElectrics",                  "High-Power Electrics" },
            { "largeVolumeContainment",          "Large Volume Containment" },
            { "nuclearPropulsion",               "Nuclear Propulsion" },
            { "scienceTech",                     "Scanning Tech" },
            { "unmannedTech",                    "Unmanned Tech" },
            // Tier 7
            { "advScienceTech",                  "Advanced Science Tech" },
            { "advUnmanned",                     "Advanced Unmanned Tech" },
            { "advancedMotors",                  "Advanced Motors" },
            { "automation",                      "Automation" },
            { "experimentalAerodynamics",        "Experimental Aerodynamics" },
            { "highPerformanceFuelSystems",      "High-Performance Fuel Systems" },
            { "hypersonicFlight",                "Hypersonic Flight" },
            { "ionPropulsion",                   "Ion Propulsion" },
            { "metaMaterials",                   "Meta-Materials" },
            { "nanolathing",                     "Nanolathing" },
            { "specializedElectrics",            "Specialized Electrics" },
            { "veryHeavyRocketry",               "Very Heavy Rocketry" },
            // Tier 8
            { "aerospaceTech",                   "Aerospace Tech" },
            { "experimentalElectrics",           "Experimental Electrics" },
            { "experimentalMotors",              "Experimental Motors" },
            { "experimentalScience",             "Experimental Science" },
            { "largeUnmanned",                   "Large Probes" },
        };

        private const float MissionScienceBonus = 5f;

        private ArchipelagoSession session;
        private KspApState state;
        private string statePath;
        private bool initialized = false;
        private int techSlotsPerNode = 4;
        private Action onLocationReported;

        // Runtime-only: vessel persistentIds that have achieved Kerbin orbit.
        // Used to gate "Kerbin Return" — only vessels that orbited Kerbin qualify.
        // Not persisted; onOrbit re-fires on scene load for orbiting vessels.
        private readonly HashSet<uint> _vesselsOrbitedKerbin = new HashSet<uint>();

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        /// <summary>
        /// Call after a successful AP connection. Loads persisted state, registers
        /// all KSP events, and immediately reports Starting Inventory locations.
        /// </summary>
        public void Initialize(ArchipelagoSession newSession, int difficulty, int techSlots = 4, Action onLocationReported = null)
        {
            session = newSession;
            this.onLocationReported = onLocationReported;
            techSlotsPerNode = techSlots;
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
            GameEvents.onVesselRecovered.Add(
                new EventData<ProtoVessel, bool>.OnEvent(OnVesselRecovered));

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
            GameEvents.onVesselRecovered.Remove(
                new EventData<ProtoVessel, bool>.OnEvent(OnVesselRecovered));

            GameEvents.onGameStateLoad.Remove(
                new EventData<ConfigNode>.OnEvent(OnGameStateLoad));
        }

        // ------------------------------------------------------------------
        // Location reporting
        // ------------------------------------------------------------------

        /// Reports a location by name to the AP server, idempotent.
        /// When grantScience is true, awards a small science bonus on first report.
        private void ReportLocation(string name, bool grantScience = false)
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
                if (grantScience && ResearchAndDevelopment.Instance != null)
                    ResearchAndDevelopment.Instance.AddScience(MissionScienceBonus, TransactionReasons.ScienceTransmission);
                Debug.Log($"[KSP-AP] Checked: {name}");
                SaveState();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[KSP-AP] ReportLocation failed for '{name}': {ex.Message}");
            }
        }

        // Reports the next unchecked slot for a body/event pair (up to event scale).
        private void ReportBodyEvent(string bodyName, string eventName)
        {
            if (!EventScale.TryGetValue(eventName, out int scale))
            {
                Debug.LogWarning($"[KSP-AP] Unknown event type: '{eventName}'");
                return;
            }
            string key = $"{bodyName}|{eventName}";
            state.BodyEventCounts.TryGetValue(key, out int count);
            if (count >= scale)
                return; // all slots already reported
            count++;
            state.BodyEventCounts[key] = count;
            ReportLocation($"{bodyName} {eventName} {count}", grantScience: true);
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

        // Checks a ScienceSubject.id for KSC biome science and reports the
        // matching AP location. Called from both OnScienceReceived and
        // OnVesselRecovered so that we catch science regardless of how it
        // reaches R&D (transmission, recovery dialog, or direct recovery).
        private void TryMatchKscBiome(string subjectId)
        {
            const string kerbinLanded = "KerbinSrfLanded";
            int idx = subjectId.IndexOf(kerbinLanded, StringComparison.Ordinal);
            if (idx < 0) return;

            string biome = subjectId.Substring(idx + kerbinLanded.Length);
            if (string.IsNullOrEmpty(biome)) return;

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

            if (locationName == null)
            {
                Debug.Log($"[KSP-AP] KSC biome '{biome}' from subject '{subjectId}' did not match any location");
                return;
            }
            if (!state.KscBiomesVisited.Add(locationName)) return; // already reported
            Debug.Log($"[KSP-AP] KSC biome matched: '{biome}' → '{locationName}'");
            ReportLocation(locationName, grantScience: true);
        }

        private void OnScienceReceived(float amount, ScienceSubject subject, ProtoVessel vessel, bool reverseEngineered)
        {
            if (subject == null) return;
            Debug.Log($"[KSP-AP] OnScienceReceived: id='{subject.id}', amount={amount}");
            TryMatchKscBiome(subject.id);
        }

        // Backup hook: scan a recovered vessel's experiment modules for KSC
        // biome science data. OnScienceRecieved does not fire reliably on
        // recovery in all cases, so we also extract subject IDs directly
        // from the stored ScienceData config nodes.
        private void OnVesselRecovered(ProtoVessel vessel, bool quick)
        {
            if (vessel == null) return;

            // Kerbin Sample Return: recovering any crewed vessel on Kerbin
            // (includes EVA kerbals, which don't trigger OnLand).
            if (vessel.GetVesselCrew().Count > 0)
                ReportBodyEvent("Kerbin", "Sample Return");

            foreach (ProtoPartSnapshot part in vessel.protoPartSnapshots)
            {
                foreach (ProtoPartModuleSnapshot module in part.modules)
                {
                    if (module.moduleName != "ModuleScienceExperiment" &&
                        module.moduleName != "ModuleScienceContainer")
                        continue;

                    foreach (ConfigNode dataNode in module.moduleValues.GetNodes("ScienceData"))
                    {
                        string subjectId = dataNode.GetValue("subjectID");
                        if (string.IsNullOrEmpty(subjectId)) continue;
                        TryMatchKscBiome(subjectId);
                    }
                }
            }
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
                    ReportLocation($"Kerbin {threshold / 1000}km Altitude", grantScience: true);
            }
        }

        // ------------------------------------------------------------------
        // KSP event handlers
        // ------------------------------------------------------------------

        private void OnFlyBy(Vessel vessel, CelestialBody body)
        {
            ReportBodyEvent(body.name, "Flyby");
        }

        private void OnOrbit(Vessel vessel, CelestialBody body)
        {
            ReportBodyEvent(body.name, "Orbit");
            if (body.name == "Kerbin")
                _vesselsOrbitedKerbin.Add(vessel.persistentId);
        }

        private void OnEscape(Vessel vessel, CelestialBody body)
        {
            // Entering a moon's SOI (e.g. Duna→Ike) fires onEscape for the parent.
            // Only report SOI Leave for a true system escape, not moon encounters.
            CelestialBody newBody = vessel.mainBody;
            if (newBody != null && newBody.referenceBody == body)
                return;

            ReportBodyEvent(body.name, "SOI Leave");
        }

        private void OnLand(Vessel vessel, CelestialBody body)
        {
            if (body.name == "Kerbin")
            {
                // Skip non-craft vessels (flags, debris, EVA kerbals)
                if (vessel.vesselType == VesselType.Flag ||
                    vessel.vesselType == VesselType.Debris ||
                    vessel.isEVA)
                    return;

                // Kerbin Return requires the vessel to have achieved Kerbin orbit.
                // Sub-orbital hops don't qualify.
                if (_vesselsOrbitedKerbin.Contains(vessel.persistentId))
                {
                    ReportBodyEvent("Kerbin", "Return");
                    if (vessel.GetCrewCount() > 0)
                        ReportBodyEvent("Kerbin", "Sample Return");
                }
                return;
            }
            ReportBodyEvent(body.name, "Landing");
            if (vessel.GetCrewCount() > 0)
                ReportBodyEvent(body.name, "Crewed Landing");
        }

        private void OnSituationChange(GameEvents.HostedFromToAction<Vessel, Vessel.Situations> data)
        {
            Vessel v = data.host;
            if (v == null) return;
            string body = v?.mainBody?.name;

            // Kerbin Splashdown — skip EVA vessels (onLand does not fire for SPLASHED)
            if (data.to == Vessel.Situations.SPLASHED && body == "Kerbin" && !v.isEVA)
                ReportLocation("Kerbin Splashdown", grantScience: true);

            // First Launch: PRELAUNCH → FLYING or SUB_ORBITAL on Kerbin
            if (body == "Kerbin"
                && data.from == Vessel.Situations.PRELAUNCH
                && (data.to == Vessel.Situations.FLYING || data.to == Vessel.Situations.SUB_ORBITAL)
                && !state.KerbinFirstLaunchDone)
            {
                state.KerbinFirstLaunchDone = true;
                ReportLocation("Kerbin First Launch", grantScience: true);
            }

            // First Landing: transition to LANDED on Kerbin from FLYING or SUB_ORBITAL
            // (not from PRELAUNCH — that's sitting on the pad)
            if (body == "Kerbin"
                && data.to == Vessel.Situations.LANDED
                && (data.from == Vessel.Situations.FLYING || data.from == Vessel.Situations.SUB_ORBITAL)
                && !state.KerbinFirstLandingDone)
            {
                state.KerbinFirstLandingDone = true;
                ReportLocation("Kerbin First Landing", grantScience: true);
            }
        }

        private void OnFlagPlant(Vessel flagVessel)
        {
            string body = flagVessel.mainBody?.name;
            if (body == null) return;
            ReportBodyEvent(body, "Flag Plant");
        }

        // onReturnFromOrbit fires when a vessel lands on Kerbin after having orbited another body.
        // The body parameter is the remote body (e.g. Mun), not Kerbin.
        private void OnReturnFromOrbit(Vessel vessel, CelestialBody body)
        {
            ReportBodyEvent(body.name, "Return");
            // Also count as a Kerbin landing (deorbit + recovery)
            ReportBodyEvent("Kerbin", "Landing");
            if (vessel.GetCrewCount() > 0)
                ReportBodyEvent("Kerbin", "Crewed Landing");
        }

        // onReturnFromSurface fires when a vessel lands on Kerbin after having landed on another body.
        private void OnReturnFromSurface(Vessel vessel, CelestialBody body)
        {
            ReportBodyEvent(body.name, "Return");
            ReportBodyEvent(body.name, "Sample Return");
            // Also count as a Kerbin landing (deorbit + recovery)
            ReportBodyEvent("Kerbin", "Landing");
            if (vessel.GetCrewCount() > 0)
                ReportBodyEvent("Kerbin", "Crewed Landing");
        }

        private void OnStageSeparation(EventReport report)
        {
            if (state.KerbinStagingDone) return;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.mainBody?.name != "Kerbin") return;
            state.KerbinStagingDone = true;
            ReportLocation("Kerbin First Staging", grantScience: true);
        }

        private void OnCrewOnEva(GameEvents.FromToAction<Part, Part> action)
        {
            Vessel v = action.to?.vessel;
            if (v == null) return;
            if (v.mainBody?.name == "Kerbin" && v.situation == Vessel.Situations.ORBITING)
                ReportLocation("Kerbin EVA in Orbit", grantScience: true);
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
            for (int slot = 1; slot <= techSlotsPerNode; slot++)
                ReportLocation($"{displayName} {slot}");

            // Clear placeholders before scouting re-evaluates newly purchasable nodes.
            UnityEngine.Object.FindObjectOfType<TechTreeScout>()?.OnNodeChecked(nodeId);
        }
    }
}
