using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Archipelago.MultiClient.Net;

namespace KSPArchipelago
{
    /// <summary>
    /// Detects KSP mission events and reports them as Archipelago location checks.
    /// Call OnConnect() after a successful AP connection (registers events on first call).
    /// Call Update() from the MonoBehaviour Update loop for altitude polling.
    /// Call OnDisconnect() on disconnect (events stay registered, offline checks are queued).
    /// Call Destroy() on mod teardown to unregister events.
    /// </summary>
    internal class MissionTracker
    {
        /// Number of locations already checked (from AP server).
        public int CheckedCount => checkedLocationIds?.Count ?? 0;

        // Populated from slot_data at connect time.
        private int[] kerbinAltThresholds = new int[0];
        private Dictionary<string, int> eventScale = new Dictionary<string, int>();
        private int startingInvCount = 0;

        // KSC biome strings (KSP game-internal) → AP location name.
        // Keys come from the game, so they must stay here.  Values are validated
        // against slot_data at connect time.
        private static readonly Dictionary<string, string> KscBiomeToLocation = new Dictionary<string, string>
        {
            {"LaunchPad",        "KSC LaunchPad"},
            {"Runway",           "KSC Runway"},
            {"Administration",   "KSC Administration"},
            {"AstronautComplex", "KSC Astronaut Complex"},
            {"FlagPole",         "KSC Flag Pole (Astronaut Complex)"},
            {"SPH",              "KSC SPH"},
            {"VAB",              "KSC VAB"},
            {"TrackingStation",  "KSC Tracking Station"},
            {"MissionControl",   "KSC Mission Control"},
            {"Crawlerway",       "KSC Crawlerway"},
            {"R&D",              "KSC R&D"},
            {"KSC",              "KSC Grounds"},
        };

        // Tech tree node_id → display_name.  Populated from slot_data at connect time.
        // Static because TechTreeScout and PlaceholderManager access it.
        internal static Dictionary<string, string> TechDisplayNames = new Dictionary<string, string>();

        private const float MissionScienceBonus = 5f;

        /// True for player-created vessels (ships, probes, EVA kerbals, etc.).
        /// False for asteroids, debris, and flags.
        private static bool IsMissionVessel(Vessel v)
        {
            if (v == null) return false;
            return v.vesselType != VesselType.SpaceObject &&
                   v.vesselType != VesselType.Flag &&
                   v.vesselType != VesselType.Debris;
        }

        private ArchipelagoSession session;
        private HashSet<long> checkedLocationIds;
        private bool initialized = false;
        private bool eventsRegistered = false;
        private int techSlotsPerNode = 4;
        private Action onLocationReported;

        // Locations detected while offline, queued for sending on reconnect.
        // Shared reference with ApScenarioModule for save/load persistence.
        private HashSet<string> pendingLocationNames = new HashSet<string>();

        // Cached location IDs for hot-path guards (looked up once at init).
        private long kerbinFirstLaunchId, kerbinFirstStagingId,
                     kerbinFirstLandingId, kerbinFirstCrashId;
        private Dictionary<int, long> altitudeIds;

        // Runtime-only: vessel persistentIds that have achieved Kerbin orbit.
        // Used to gate "Kerbin Return" — only vessels that orbited Kerbin qualify.
        // Not persisted; onOrbit re-fires on scene load for orbiting vessels.
        private readonly HashSet<uint> _vesselsOrbitedKerbin = new HashSet<uint>();

        // Goal detection: cached location IDs whose checks indicate victory.
        // Set via SetGoalLocations() from slot_data; polled by IsGoalMet().
        private List<long> _goalLocationIds;
        // Per-goal-location display names, parallel to _goalLocationIds.
        private List<string> _goalDisplayNames;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        /// <summary>
        /// Call after a successful AP connection. Populates checked-location state
        /// from the server, registers events on first call, flushes any offline
        /// queued checks, and reports Starting Inventory.
        /// Returns null on success, or an error message if slot_data is invalid.
        /// </summary>
        public string OnConnect(ArchipelagoSession newSession, int difficulty, int techSlots = 4,
                                Action onLocationReported = null,
                                Dictionary<string, object> slotData = null)
        {
            session = newSession;
            this.onLocationReported = onLocationReported;
            techSlotsPerNode = techSlots;

            string error = ParseSlotData(slotData);
            if (error != null) return error;

            checkedLocationIds = new HashSet<long>(session.Locations.AllLocationsChecked);
            Debug.Log($"[KSP-AP] Loaded {checkedLocationIds.Count} checked locations from server.");

            // Cache IDs for hot-path guards.
            kerbinFirstLaunchId = LookupId("Kerbin First Launch");
            kerbinFirstStagingId = LookupId("Kerbin First Staging");
            kerbinFirstLandingId = LookupId("Kerbin First Landing");
            kerbinFirstCrashId = LookupId("Kerbin First Crash");
            altitudeIds = new Dictionary<int, long>();
            foreach (int t in kerbinAltThresholds)
                altitudeIds[t] = LookupId($"Kerbin {t / 1000}km Altitude");

            if (!eventsRegistered)
            {
                RegisterEvents();
                eventsRegistered = true;
            }

            FlushPending();
            ReportStartingInventory();
            initialized = true;
            return null;
        }

        /// <summary>
        /// Parse and validate all required slot_data keys.
        /// Returns null on success, or an error message describing what's missing.
        /// </summary>
        private string ParseSlotData(Dictionary<string, object> slotData)
        {
            if (slotData == null)
                return "Server sent no slot_data";

            var missing = new List<string>();

            // Event scales
            if (slotData.TryGetValue("event_scales", out object esObj)
                && esObj is JObject esDict)
            {
                eventScale = new Dictionary<string, int>();
                foreach (var kvp in esDict)
                    eventScale[kvp.Key] = (int)kvp.Value;
            }
            else missing.Add("event_scales");

            // Tech display names
            if (slotData.TryGetValue("tech_display_names", out object tdObj)
                && tdObj is JObject tdDict)
            {
                TechDisplayNames = new Dictionary<string, string>();
                foreach (var kvp in tdDict)
                    TechDisplayNames[kvp.Key] = (string)kvp.Value;
            }
            else missing.Add("tech_display_names");

            // Kerbin altitude thresholds
            if (slotData.TryGetValue("kerbin_altitude_thresholds", out object katObj)
                && katObj is JArray katArr)
            {
                kerbinAltThresholds = katArr.ToObject<int[]>();
            }
            else missing.Add("kerbin_altitude_thresholds");

            // Starting inventory count
            if (slotData.TryGetValue("starting_inv_count", out object sicObj))
                startingInvCount = Convert.ToInt32(sicObj);
            else
                missing.Add("starting_inv_count");

            // KSC biome names: validate that our hardcoded mapping covers the server's list.
            if (slotData.TryGetValue("ksc_biome_names", out object kbnObj)
                && kbnObj is JArray kbnArr)
            {
                var serverNames = new HashSet<string>();
                foreach (var tok in kbnArr)
                    serverNames.Add((string)tok);
                foreach (var kvp in KscBiomeToLocation)
                {
                    if (!serverNames.Contains(kvp.Value))
                        return $"KSC biome mismatch: client has '{kvp.Value}' but server doesn't";
                }
            }
            else missing.Add("ksc_biome_names");

            if (missing.Count > 0)
                return "Server slot_data missing required keys: " + string.Join(", ", missing);

            Debug.Log($"[KSP-AP] slot_data validated: {eventScale.Count} events, " +
                      $"{TechDisplayNames.Count} tech nodes, {kerbinAltThresholds.Length} alt thresholds, " +
                      $"{startingInvCount} starting inv");
            return null;
        }

        private long LookupId(string name) =>
            session.Locations.GetLocationIdFromName(session.ConnectionInfo.Game, name);

        /// <summary>
        /// Call on server disconnect. Nulls session but keeps events registered
        /// so checks detected while offline are queued for later.
        /// </summary>
        public void OnDisconnect()
        {
            session = null;
        }

        /// <summary>Call on mod teardown to unregister KSP events.</summary>
        public void Destroy()
        {
            if (eventsRegistered)
            {
                UnregisterEvents();
                eventsRegistered = false;
            }
            session = null;
            initialized = false;
        }

        public HashSet<string> GetPendingNames() => pendingLocationNames;

        public void SetPendingNames(HashSet<string> names)
        {
            pendingLocationNames = names ?? new HashSet<string>();
        }

        /// <summary>
        /// Cache goal-relevant location IDs from server-provided names.
        /// Call after session is available (needs LookupId).
        /// </summary>
        public void SetGoalLocations(List<string> locationNames)
        {
            _goalLocationIds = new List<long>(locationNames.Count);
            _goalDisplayNames = new List<string>(locationNames.Count);
            foreach (string name in locationNames)
            {
                long id = LookupId(name);
                if (id >= 0)
                {
                    _goalLocationIds.Add(id);
                    // Strip trailing " 1" — all sentinels are slot-1 locations.
                    string display = name.EndsWith(" 1") ? name.Substring(0, name.Length - 2) : name;
                    _goalDisplayNames.Add(display);
                }
                else
                    Debug.LogWarning($"[KSP-AP] Goal location not found: '{name}'");
            }
            Debug.Log($"[KSP-AP] Cached {_goalLocationIds.Count} goal location IDs");
        }

        /// <summary>
        /// Returns per-location goal progress: key = display name, value = is checked.
        /// </summary>
        public List<KeyValuePair<string, bool>> GetGoalStatus()
        {
            var result = new List<KeyValuePair<string, bool>>();
            if (_goalLocationIds == null || checkedLocationIds == null) return result;
            for (int i = 0; i < _goalLocationIds.Count; i++)
            {
                bool done = checkedLocationIds.Contains(_goalLocationIds[i]);
                result.Add(new KeyValuePair<string, bool>(_goalDisplayNames[i], done));
            }
            return result;
        }

        public int GoalLocationCount => _goalLocationIds?.Count ?? 0;

        public int GoalLocationsChecked
        {
            get
            {
                if (_goalLocationIds == null || checkedLocationIds == null) return 0;
                int count = 0;
                foreach (long id in _goalLocationIds)
                    if (checkedLocationIds.Contains(id)) count++;
                return count;
            }
        }

        /// <summary>
        /// Returns true if all goal-sentinel locations have been checked.
        /// </summary>
        public bool IsGoalMet()
        {
            if (_goalLocationIds == null || _goalLocationIds.Count == 0
                || checkedLocationIds == null)
                return false;
            foreach (long id in _goalLocationIds)
            {
                if (!checkedLocationIds.Contains(id)) return false;
            }
            return true;
        }

        /// <summary>Call from MonoBehaviour.Update() for altitude polling.</summary>
        public void Update()
        {
            if (!initialized) return;
            PollKerbinAltitude();
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
            GameEvents.onVesselSOIChanged.Add(
                new EventData<GameEvents.HostedFromToAction<Vessel, CelestialBody>>.OnEvent(OnVesselSOIChanged));
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
            GameEvents.onCrash.Add(
                new EventData<EventReport>.OnEvent(OnCrash));
            GameEvents.onCrashSplashdown.Add(
                new EventData<EventReport>.OnEvent(OnCrash));
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
            GameEvents.onVesselSOIChanged.Remove(
                new EventData<GameEvents.HostedFromToAction<Vessel, CelestialBody>>.OnEvent(OnVesselSOIChanged));
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
            GameEvents.onCrash.Remove(
                new EventData<EventReport>.OnEvent(OnCrash));
            GameEvents.onCrashSplashdown.Remove(
                new EventData<EventReport>.OnEvent(OnCrash));
        }

        // ------------------------------------------------------------------
        // Location reporting
        // ------------------------------------------------------------------

        /// Reports a location by name to the AP server, idempotent.
        /// When offline, queues the name for sending on reconnect.
        /// When grantScience is true, awards a small science bonus on first report.
        private void ReportLocation(string name, bool grantScience = false)
        {
            if (session == null)
            {
                if (!initialized) return; // never connected — ignore pre-connection events
                if (!pendingLocationNames.Add(name)) return; // already queued
                onLocationReported?.Invoke();
                if (grantScience && ResearchAndDevelopment.Instance != null)
                    ResearchAndDevelopment.Instance.AddScience(MissionScienceBonus, TransactionReasons.ScienceTransmission);
                Debug.Log($"[KSP-AP] Queued (offline): {name}");
                return;
            }
            try
            {
                long id = session.Locations.GetLocationIdFromName(session.ConnectionInfo.Game, name);
                if (id < 0)
                {
                    Debug.LogWarning($"[KSP-AP] Unknown location: '{name}'");
                    return;
                }
                if (!checkedLocationIds.Add(id))
                    return; // already reported
                session.Locations.CompleteLocationChecks(id);
                onLocationReported?.Invoke();
                if (grantScience && ResearchAndDevelopment.Instance != null)
                    ResearchAndDevelopment.Instance.AddScience(MissionScienceBonus, TransactionReasons.ScienceTransmission);
                Debug.Log($"[KSP-AP] Checked: {name}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[KSP-AP] ReportLocation failed for '{name}': {ex.Message}");
            }
        }

        // Reports all unchecked slots for a body/event pair (up to event scale).
        private void ReportBodyEvent(string bodyName, string eventName)
        {
            if (!eventScale.TryGetValue(eventName, out int scale))
            {
                Debug.LogWarning($"[KSP-AP] Unknown event type: '{eventName}'");
                return;
            }
            for (int slot = 1; slot <= scale; slot++)
                ReportLocation($"{bodyName} {eventName} {slot}", grantScience: true);
        }

        /// <summary>
        /// Sends any locations queued while offline to the now-connected server.
        /// </summary>
        private void FlushPending()
        {
            if (pendingLocationNames.Count == 0) return;
            Debug.Log($"[KSP-AP] Flushing {pendingLocationNames.Count} pending offline locations");
            foreach (string name in new List<string>(pendingLocationNames))
            {
                long id = session.Locations.GetLocationIdFromName(session.ConnectionInfo.Game, name);
                if (id < 0)
                {
                    Debug.LogWarning($"[KSP-AP] Flush: unknown location '{name}', skipping");
                    continue;
                }
                if (!checkedLocationIds.Add(id)) continue; // server already has it
                session.Locations.CompleteLocationChecks(id);
                Debug.Log($"[KSP-AP] Flushed: {name}");
            }
            pendingLocationNames.Clear();
        }

        // ------------------------------------------------------------------
        // Starting inventory (zero-requirement locations reported on connect)
        // ------------------------------------------------------------------

        public void ReportStartingInventory()
        {
            if (checkedLocationIds.Contains(LookupId("Starting Inventory 1"))) return;
            for (int i = 1; i <= startingInvCount; i++)
                ReportLocation($"Starting Inventory {i}");
        }

        // ------------------------------------------------------------------
        // Surface sample detection
        // ------------------------------------------------------------------

        private const string SurfaceSamplePrefix = "surfaceSample@";

        // Extracts the body name from a surfaceSample subject ID.
        // Format: surfaceSample@{Body}Srf{Landed|Splashed}{Biome}
        private static string ExtractSampleBody(string subjectId)
        {
            if (!subjectId.StartsWith(SurfaceSamplePrefix, StringComparison.Ordinal))
                return null;
            int srfIdx = subjectId.IndexOf("Srf", SurfaceSamplePrefix.Length, StringComparison.Ordinal);
            if (srfIdx <= SurfaceSamplePrefix.Length) return null;
            return subjectId.Substring(SurfaceSamplePrefix.Length, srfIdx - SurfaceSamplePrefix.Length);
        }

        // Returns all body names that have surface-sample data on a ProtoVessel.
        private HashSet<string> CollectSurfaceSampleBodies(ProtoVessel vessel)
        {
            var bodies = new HashSet<string>();
            foreach (ProtoPartSnapshot part in vessel.protoPartSnapshots)
            {
                foreach (ProtoPartModuleSnapshot module in part.modules)
                {
                    if (module.moduleName != "ModuleScienceExperiment" &&
                        module.moduleName != "ModuleScienceContainer")
                        continue;
                    foreach (ConfigNode dataNode in module.moduleValues.GetNodes("ScienceData"))
                    {
                        string body = ExtractSampleBody(dataNode.GetValue("subjectID") ?? "");
                        if (body != null) bodies.Add(body);
                    }
                }
            }
            return bodies;
        }

        // Returns all body names that have surface-sample data on a live Vessel.
        private HashSet<string> CollectSurfaceSampleBodies(Vessel vessel)
        {
            var bodies = new HashSet<string>();
            foreach (Part part in vessel.Parts)
            {
                foreach (PartModule module in part.Modules)
                {
                    IScienceDataContainer container = module as IScienceDataContainer;
                    if (container == null) continue;
                    ScienceData[] data = container.GetData();
                    if (data == null) continue;
                    foreach (ScienceData d in data)
                    {
                        string body = ExtractSampleBody(d.subjectID ?? "");
                        if (body != null) bodies.Add(body);
                    }
                }
            }
            return bodies;
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

            // Award Sample Return for every body whose surface sample is on board.
            foreach (string body in CollectSurfaceSampleBodies(vessel))
                ReportBodyEvent(body, "Sample Return");

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
            foreach (int threshold in kerbinAltThresholds)
            {
                if (alt >= threshold && !checkedLocationIds.Contains(altitudeIds[threshold]))
                    ReportLocation($"Kerbin {threshold / 1000}km Altitude", grantScience: true);
            }
        }

        // ------------------------------------------------------------------
        // KSP event handlers
        // ------------------------------------------------------------------

        private void OnFlyBy(Vessel vessel, CelestialBody body)
        {
            if (!IsMissionVessel(vessel)) return;
            ReportBodyEvent(body.name, "Flyby");
        }

        private void OnVesselSOIChanged(GameEvents.HostedFromToAction<Vessel, CelestialBody> data)
        {
            // Report flyby when entering a body's SOI from its parent SOI.
            // onFlyBy only fires for "new" SOIs, so it misses Kerbin (home body).
            // This catches that case and acts as belt-and-suspenders for all bodies.
            // Leaving a moon back to its planet is filtered out because
            // data.from (the moon) != data.to.referenceBody (the planet's parent).
            if (!IsMissionVessel(data.host)) return;
            if (data.to != null && data.from == data.to.referenceBody)
                ReportBodyEvent(data.to.name, "Flyby");
        }

        private void OnOrbit(Vessel vessel, CelestialBody body)
        {
            if (!IsMissionVessel(vessel)) return;
            ReportBodyEvent(body.name, "Orbit");
            if (body.name == "Kerbin")
                _vesselsOrbitedKerbin.Add(vessel.persistentId);
        }

        private void OnEscape(Vessel vessel, CelestialBody body)
        {
            if (!IsMissionVessel(vessel)) return;
            // Entering a moon's SOI (e.g. Duna→Ike) fires onEscape for the parent.
            // Only report SOI Leave for a true system escape, not moon encounters.
            CelestialBody newBody = vessel.mainBody;
            if (newBody != null && newBody.referenceBody == body)
                return;

            ReportBodyEvent(body.name, "SOI Leave");
        }

        private void OnLand(Vessel vessel, CelestialBody body)
        {
            if (!IsMissionVessel(vessel)) return;
            if (body.name == "Kerbin")
            {
                // Kerbin Return requires the vessel to have achieved Kerbin orbit.
                // Sub-orbital hops don't qualify.
                if (_vesselsOrbitedKerbin.Contains(vessel.persistentId))
                {
                    ReportBodyEvent("Kerbin", "Return");
                    foreach (string sampleBody in CollectSurfaceSampleBodies(vessel))
                        ReportBodyEvent(sampleBody, "Sample Return");
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
            if (!IsMissionVessel(v)) return;
            string body = v.mainBody?.name;

            // Kerbin Splashdown (onLand does not fire for SPLASHED)
            if (data.to == Vessel.Situations.SPLASHED && body == "Kerbin")
                ReportLocation("Kerbin Splashdown", grantScience: true);

            // First Launch: any transition to FLYING or SUB_ORBITAL on Kerbin.
            // Don't check data.from — KSP can insert PRELAUNCH→LANDED→FLYING
            // when physics settles the vessel on the pad before launch.
            if (body == "Kerbin"
                && (data.to == Vessel.Situations.FLYING || data.to == Vessel.Situations.SUB_ORBITAL)
                && !checkedLocationIds.Contains(kerbinFirstLaunchId))
            {
                ReportLocation("Kerbin First Launch", grantScience: true);
            }

            // First Landing: transition to LANDED on Kerbin from FLYING or SUB_ORBITAL
            // (not from PRELAUNCH — that's sitting on the pad)
            if (body == "Kerbin"
                && data.to == Vessel.Situations.LANDED
                && (data.from == Vessel.Situations.FLYING || data.from == Vessel.Situations.SUB_ORBITAL)
                && !checkedLocationIds.Contains(kerbinFirstLandingId))
            {
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
            if (!IsMissionVessel(vessel)) return;
            ReportBodyEvent(body.name, "Return");
            // Also count as a Kerbin landing (deorbit + recovery)
            ReportBodyEvent("Kerbin", "Landing");
            if (vessel.GetCrewCount() > 0)
                ReportBodyEvent("Kerbin", "Crewed Landing");
        }

        // onReturnFromSurface fires when a vessel lands on Kerbin after having landed on another body.
        private void OnReturnFromSurface(Vessel vessel, CelestialBody body)
        {
            if (!IsMissionVessel(vessel)) return;
            ReportBodyEvent(body.name, "Return");
            foreach (string sampleBody in CollectSurfaceSampleBodies(vessel))
                ReportBodyEvent(sampleBody, "Sample Return");
            // Also count as a Kerbin landing (deorbit + recovery)
            ReportBodyEvent("Kerbin", "Landing");
            if (vessel.GetCrewCount() > 0)
                ReportBodyEvent("Kerbin", "Crewed Landing");
        }

        private void OnStageSeparation(EventReport report)
        {
            if (checkedLocationIds.Contains(kerbinFirstStagingId)) return;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.mainBody?.name != "Kerbin") return;
            ReportLocation("Kerbin First Staging", grantScience: true);
        }

        // onCrash / onCrashSplashdown fire per-part on impact destruction.
        // We only care about the first crash ever on Kerbin.
        // Use ActiveVessel (like OnStageSeparation) because KSP reclassifies
        // parts as Debris before firing crash events, breaking IsMissionVessel.
        // ActiveVessel is the craft the player is flying, so detached boosters
        // that crash separately won't trigger this.
        private void OnCrash(EventReport report)
        {
            if (checkedLocationIds.Contains(kerbinFirstCrashId)) return;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || !IsMissionVessel(v)) return;
            if (v.mainBody?.name != "Kerbin") return;
            ReportLocation("Kerbin First Crash", grantScience: true);
        }

        private void OnCrewOnEva(GameEvents.FromToAction<Part, Part> action)
        {
            // Use the source vessel's situation — the EVA vessel may not have its
            // orbital state initialized yet when this event fires.
            Vessel v = action.from?.vessel ?? action.to?.vessel;
            if (v == null) return;
            if (v.situation == Vessel.Situations.ORBITING && v.mainBody != null)
                ReportBodyEvent(v.mainBody.name, "EVA in Orbit");
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
