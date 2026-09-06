using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Archipelago.MultiClient.Net;
using KSPArchipelago.Missions;

namespace KSPArchipelago
{
    /// <summary>
    /// Detects KSP mission events and reports them as Archipelago location checks.
    /// Call OnConnect() after a successful AP connection (registers events on first call).
    /// Call Update() from the MonoBehaviour Update loop for altitude polling.
    /// Call OnDisconnect() on disconnect (events stay registered, offline checks are queued).
    /// Call Destroy() on mod teardown to unregister events.
    /// </summary>
    public class MissionTracker
    {
        /// Number of locations already checked (from AP server).
        public int CheckedCount => checkedLocationIds?.Count ?? 0;

        /// True iff the location with this AP name has been checked.
        /// Resolves name → id via the AP session; returns false on
        /// unknown names, missing session, or pre-init state. Used by
        /// the contracts subsystem (ApContractManager) to skip contracts
        /// whose AP location has already been checked.
        public bool IsLocationChecked(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (session == null || checkedLocationIds == null) return false;
            long id;
            try
            {
                id = session.Locations.GetLocationIdFromName(
                    session.ConnectionInfo.Game, name);
            }
            catch
            {
                return false;
            }
            if (id < 0) return false;
            return checkedLocationIds.Contains(id);
        }

        /// True iff the AP server recognises this location name (regardless
        /// of whether it's been checked yet). Used by UX surfaces that
        /// want to tell the player "this upgrade is a real AP check, wait
        /// for the matching item" vs "the generator hasn't shipped this
        /// check yet."
        public bool IsLocationKnown(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (session == null) return false;
            try
            {
                return session.Locations.GetLocationIdFromName(
                    session.ConnectionInfo.Game, name) >= 0;
            }
            catch
            {
                return false;
            }
        }

        // Populated from slot_data at connect time.
        private int[] homeAltThresholds = new int[0];
        private Dictionary<string, int> eventScale = new Dictionary<string, int>();
        private int startingInvCount = 0;

        // KSP game-internal biome key → AP location name.  Populated from
        // slot_data at connect time; the server is the single source of
        // truth for both the biome keys and the location names.  Iteration
        // order is preserved from the JSON, which the server orders so
        // that the catch-all "KSC" key falls last (sub-biome fallback uses
        // StartsWith on the key).
        private Dictionary<string, string> kscBiomeToLocation = new Dictionary<string, string>();

        // Tech tree node_id → display_name.  Populated from slot_data at connect time.
        // Static because TechTreeScout and PlaceholderManager access it.
        internal static Dictionary<string, string> TechDisplayNames = new Dictionary<string, string>();

        private const float MissionScienceBonus = 5f;

        // volatile: assigned on the console thread (OnConnect/OnDisconnect),
        // read on the main thread and the send worker.
        private volatile ArchipelagoSession session;
        private HashSet<long> checkedLocationIds;
        private bool initialized = false;
        private bool eventsRegistered = false;
        private int techSlotsPerNode = 4;
        private Action onLocationReported;
        // Invoked when a location send throws (likely a closed socket the
        // library hasn't surfaced yet). Lets APConsole start its reconnect cycle.
        private Action<string> onSendFailed;
        // DeathLink send callback (cause -> AP Bounce). Non-null only when the seed
        // enabled DeathLink; null disables all outgoing deaths. Invoked from
        // OnRootPartWillDie / OnCrewKilled via SendDeath.
        private Action<string> onDeath;
        // Per-vessel dedup for outgoing deaths, scoped to ONE flight (cleared on
        // onFlightReady). MurderCrew kills each crew member individually, so a
        // three-seat pod fires onCrewKilled three times; a craft that loses its
        // root part with crew aboard fires both paths. All of that is ONE death.
        // Also pre-marked by OnModDestroyedVessel so a mod-initiated kill never
        // rebroadcasts (anti-loop). Must NOT persist across flights:
        // Revert-to-Launch reuses a craft's persistentId, so a session-lifetime
        // set would permanently bar a reflown craft from ever broadcasting again.
        private readonly HashSet<uint> _deathSent = new HashSet<uint>();
        // True once ANY death has been broadcast for the current flight, whatever
        // vessel it was charged to. The revert path needs this rather than
        // _deathSent because by revert time the craft that died may be gone or
        // no longer the active vessel, so its persistentId is unrecoverable.
        // Cleared with _deathSent on onFlightReady.
        private bool _deathSentThisFlight;
        // slot_data death_link_on_revert. Reverting a flight that actually
        // launched broadcasts a death. Only consulted when onDeath is non-null,
        // so it is inert unless the seed also enabled DeathLink.
        private bool deathLinkOnRevert;

        // Simulation / practice mode. When true, ReportLocation short-circuits
        // to a "would check" toast and does NOT send, queue, dedup, or reward —
        // letting a player fly a cheat-menu practice mission and Revert without
        // burning real checks. In-memory only (intentionally not persisted): a
        // fresh KSP launch is always live. Toggled from ArchipelagoUI; all
        // access is on the main thread.
        public bool SimulationMode;
        // Base location name + frame of the last simulation-mode toast, used to
        // collapse the multi-slot loops (ReportBodyEvent / OnTechResearched) —
        // which fire synchronously within one frame — into a single toast.
        // Frame-scoped so a later mission re-toasts an event even if it matches
        // the previous session's last toast.
        private string _lastSimBase;
        private int _lastSimFrame = -1;

        // Locations detected while offline, queued for sending on reconnect.
        // Shared reference with ApScenarioModule for save/load persistence.
        private HashSet<string> pendingLocationNames = new HashSet<string>();

        // Off-main-thread location sending. ReportLocation/FlushPending do the
        // cheap bookkeeping on the calling thread and enqueue (id, name);
        // SendWorker performs the actual websocket send so GameEvents handlers
        // never block a frame on I/O. Failures flow back through _sendFailures
        // and are applied in Update() — the worker never touches
        // checkedLocationIds or pendingLocationNames (ApScenarioModule iterates
        // the pending set raw during saves, so those sets must stay off the
        // worker thread).
        private BlockingCollection<SendRequest> _sendQueue =
            new BlockingCollection<SendRequest>();
        private readonly ConcurrentQueue<SendResult> _sendFailures =
            new ConcurrentQueue<SendResult>();
        private Thread _sendThread;

        private sealed class SendRequest
        {
            public readonly long Id;
            public readonly string Name;
            public SendRequest(long id, string name) { Id = id; Name = name; }
        }

        // A failed send: carries the session the send was attempted on (null if
        // disconnected) so the main thread can distinguish "this session is
        // dead" from "a reconnect already replaced the session — just retry".
        private sealed class SendResult
        {
            public readonly long Id;
            public readonly string Name;
            public readonly ArchipelagoSession Session;
            public readonly string Error;
            public SendResult(SendRequest req, ArchipelagoSession session, string error)
            {
                Id = req.Id; Name = req.Name; Session = session; Error = error;
            }
        }

        // Cached location IDs for hot-path guards (looked up once at init).
        private long homeFirstLaunchId, homeFirstStagingId,
                     homeFirstLandingId, homeFirstCrashId;
        private Dictionary<int, long> altitudeIds;

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
                                Action<string> onSendFailed = null,
                                Action<string> onDeath = null,
                                Dictionary<string, object> slotData = null)
        {
            session = newSession;
            this.onLocationReported = onLocationReported;
            this.onSendFailed = onSendFailed;
            this.onDeath = onDeath;
            techSlotsPerNode = techSlots;

            string error = ParseSlotData(slotData);
            if (error != null) return error;

            checkedLocationIds = new HashSet<long>(session.Locations.AllLocationsChecked);
            Debug.Log($"[KSP-AP] Loaded {checkedLocationIds.Count} checked locations from server.");

            // Cache IDs for hot-path guards.
            string home = KSPArchipelagoMod.StartingBody;
            homeFirstLaunchId = LookupId($"{home} First Launch");
            homeFirstStagingId = LookupId($"{home} First Staging");
            homeFirstLandingId = LookupId($"{home} First Landing");
            homeFirstCrashId = LookupId($"{home} First Crash");
            altitudeIds = new Dictionary<int, long>();
            foreach (int t in homeAltThresholds)
                altitudeIds[t] = LookupId($"{home} {t / 1000}km Altitude");

            if (!eventsRegistered)
            {
                RegisterEvents();
                eventsRegistered = true;
            }

            EnsureSendWorker();
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

            // Altitude milestone thresholds (home-body suborbital altitudes,
            // in metres). Slot-data key kept as `kerbin_altitude_thresholds`
            // for compatibility with the current server-side schema.
            if (slotData.TryGetValue("kerbin_altitude_thresholds", out object katObj)
                && katObj is JArray katArr)
            {
                homeAltThresholds = katArr.ToObject<int[]>();
            }
            else missing.Add("kerbin_altitude_thresholds");

            // Starting inventory count
            if (slotData.TryGetValue("starting_inv_count", out object sicObj))
                startingInvCount = Convert.ToInt32(sicObj);
            else
                missing.Add("starting_inv_count");

            // KSC biome map (biome_key → AP location name).  Server is
            // authoritative — no hardcoded copy on the client.
            if (slotData.TryGetValue("ksc_biome_locations", out object kblObj)
                && kblObj is JObject kblDict)
            {
                kscBiomeToLocation = new Dictionary<string, string>();
                foreach (var kvp in kblDict)
                    kscBiomeToLocation[kvp.Key] = (string)kvp.Value;
            }
            else missing.Add("ksc_biome_locations");

            // Optional: charge a death for reverting a launched flight. Absent on
            // seeds generated before the option existed -> off. Not required.
            deathLinkOnRevert = slotData.TryGetValue("death_link_on_revert", out object dlrObj)
                                && Convert.ToInt32(dlrObj) != 0;

            if (missing.Count > 0)
                return "Server slot_data missing required keys: " + string.Join(", ", missing);

            Debug.Log($"[KSP-AP] slot_data validated: {eventScale.Count} events, " +
                      $"{TechDisplayNames.Count} tech nodes, {homeAltThresholds.Length} alt thresholds, " +
                      $"{startingInvCount} starting inv, {kscBiomeToLocation.Count} KSC biomes");
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
            // Stop sending deaths on a dead session; OnConnect re-wires onDeath.
            onDeath = null;
            _deathSent.Clear();
        }

        /// <summary>Call on mod teardown to unregister KSP events.</summary>
        public void Destroy()
        {
            // CompleteAdding before nulling session so the worker can drain
            // already-queued sends on the live connection before exiting.
            _sendQueue.CompleteAdding();
            _sendThread = null;
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

        /// <summary>
        /// Call from MonoBehaviour.Update(). Applies send-worker failures on
        /// the main thread and polls altitude milestones.
        /// </summary>
        public void Update()
        {
            DrainSendFailures();
            if (!initialized) return;
            PollHomeAltitude();
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

            // The return/sample family. FlightMilestoneSource owns every
            // GameEvents hook for it (onVesselRecovered, onReturnFrom*, the
            // home landing/splashdown) and publishes one FlightMilestone per
            // "the craft is home" signal; OnMilestone turns that evidence into
            // AP checks. Contract parameters subscribe to the SAME event, which
            // is what stops a location and its contract disagreeing about what
            // a flight proved.
            FlightMilestoneSource.Register();
            MissionEvidence.Observed += OnMilestone;

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
            GameEvents.onCrewKilled.Add(
                new EventData<EventReport>.OnEvent(OnCrewKilled));
            GameEvents.onPartWillDie.Add(
                new EventData<Part>.OnEvent(OnRootPartWillDie));
            GameEvents.OnRevertToLaunchFlightState.Add(
                new EventData<FlightState>.OnEvent(OnRevert));
            GameEvents.OnRevertToPrelaunchFlightState.Add(
                new EventData<FlightState>.OnEvent(OnRevert));
            GameEvents.onFlightReady.Add(OnFlightReadyResetDeaths);
            VesselDestruction.Destroyed += OnModDestroyedVessel;
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

            MissionEvidence.Observed -= OnMilestone;
            FlightMilestoneSource.Unregister();

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
            GameEvents.onCrewKilled.Remove(
                new EventData<EventReport>.OnEvent(OnCrewKilled));
            GameEvents.onPartWillDie.Remove(
                new EventData<Part>.OnEvent(OnRootPartWillDie));
            GameEvents.OnRevertToLaunchFlightState.Remove(
                new EventData<FlightState>.OnEvent(OnRevert));
            GameEvents.OnRevertToPrelaunchFlightState.Remove(
                new EventData<FlightState>.OnEvent(OnRevert));
            GameEvents.onFlightReady.Remove(OnFlightReadyResetDeaths);
            VesselDestruction.Destroyed -= OnModDestroyedVessel;
        }

        // ------------------------------------------------------------------
        // Location reporting
        // ------------------------------------------------------------------

        /// Reports a location by name to the AP server, idempotent.
        /// When offline, queues the name for sending on reconnect.
        /// When grantScience is true, awards a small science bonus on first report.
        public void ReportLocation(string name, bool grantScience = false)
        {
            // Simulation mode: practice flights report nothing to the server.
            // This sits ABOVE the offline-queue branch below, so an offline +
            // simulation player buffers nothing for reconnect either — just a
            // toast. No send, no dedup, no science: a true no-op the player
            // can Revert away.
            if (SimulationMode) { SimNotify(name); return; }

            var s = session;
            if (s == null)
            {
                if (!initialized) return; // never connected — ignore pre-connection events
                if (!pendingLocationNames.Add(name)) return; // already queued
                GrantLocalReward(grantScience);
                Debug.Log($"[KSP-AP] Queued (offline): {name}");
                return;
            }

            long id;
            try
            {
                id = s.Locations.GetLocationIdFromName(s.ConnectionInfo.Game, name);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[KSP-AP] Location lookup failed for '{name}': {ex.Message}");
                return;
            }
            if (id < 0)
            {
                Debug.LogWarning($"[KSP-AP] Unknown location: '{name}'");
                return;
            }
            if (!checkedLocationIds.Add(id))
                return; // already reported

            // The websocket send happens on the send worker — doing it here
            // would stall the frame (GameEvents handlers and the Update poll
            // run on the main thread). Grant the local reward immediately —
            // the player did the thing in game regardless of send outcome.
            // If the send fails, DrainSendFailures rolls back the dedup add
            // and queues the name for the reconnect flush.
            GrantLocalReward(grantScience);
            _sendQueue.Add(new SendRequest(id, name));
        }

        // Posts a single "would check" toast for a suppressed simulation-mode
        // report. Strips a trailing " <n>" slot index and skips consecutive
        // duplicate base names, so a body event firing "Mun Orbit 1/2/3" or a
        // tech node firing "<node> 1..4" collapse to one toast instead of N.
        private void SimNotify(string name)
        {
            string baseName = StripSlotIndex(name);
            int frame = Time.frameCount;
            if (baseName == _lastSimBase && frame == _lastSimFrame) return;
            _lastSimBase = baseName;
            _lastSimFrame = frame;
            ScreenMessages.PostScreenMessage(
                $"SIMULATION: {baseName} — would check (not sent)",
                4f, ScreenMessageStyle.UPPER_CENTER);
            Debug.Log($"[KSP-AP] SIMULATION (suppressed): {name}");
        }

        // "Mun Orbit 3" -> "Mun Orbit"; "Splashdown" -> "Splashdown".
        private static string StripSlotIndex(string name)
        {
            int sp = name.LastIndexOf(' ');
            if (sp <= 0 || sp == name.Length - 1) return name;
            for (int i = sp + 1; i < name.Length; i++)
                if (!char.IsDigit(name[i])) return name;
            return name.Substring(0, sp);
        }

        // Starts the send worker if it isn't running. Recreates the queue if a
        // previous Destroy() completed it.
        private void EnsureSendWorker()
        {
            if (_sendThread != null && _sendThread.IsAlive) return;
            if (_sendQueue.IsAddingCompleted)
                _sendQueue = new BlockingCollection<SendRequest>();
            _sendThread = new Thread(SendWorker)
            {
                IsBackground = true,
                Name = "AP-LocationSender",
            };
            _sendThread.Start();
        }

        // Drains the send queue off the main thread. Batches whatever is
        // immediately available into a single CompleteLocationChecks call
        // (multi-slot body events and tech nodes queue several ids at once,
        // which would otherwise be one websocket send each). Failures are
        // reported back through _sendFailures and applied on the main thread.
        private void SendWorker()
        {
            var batch = new List<SendRequest>();
            while (true)
            {
                SendRequest first;
                try { first = _sendQueue.Take(); }
                catch (InvalidOperationException) { return; } // CompleteAdding + drained

                batch.Clear();
                batch.Add(first);
                SendRequest extra;
                while (_sendQueue.TryTake(out extra)) batch.Add(extra);

                ArchipelagoSession s = session;
                if (s == null)
                {
                    foreach (SendRequest req in batch)
                        _sendFailures.Enqueue(new SendResult(req, null, "not connected"));
                    continue;
                }

                var ids = new long[batch.Count];
                for (int i = 0; i < batch.Count; i++) ids[i] = batch[i].Id;
                try
                {
                    s.Locations.CompleteLocationChecks(ids);
                    foreach (SendRequest req in batch)
                        Debug.Log($"[KSP-AP] Checked: {req.Name}");
                }
                catch (Exception ex)
                {
                    foreach (SendRequest req in batch)
                        _sendFailures.Enqueue(new SendResult(req, s, ex.Message));
                }
            }
        }

        // Applies send failures on the main thread (the worker must not touch
        // checkedLocationIds / pendingLocationNames). A failure means the
        // socket died before the library noticed: roll back the dedup add so
        // the reconnect flush re-sends, queue the name, and notify the console
        // once so it starts its reconnect cycle. A failure from a session that
        // a reconnect has since replaced is retried on the new session instead
        // — notifying for it would tear down the healthy new connection.
        private void DrainSendFailures()
        {
            string failure = null;
            SendResult r;
            while (_sendFailures.TryDequeue(out r))
            {
                ArchipelagoSession current = session;
                if (current != null && !ReferenceEquals(current, r.Session))
                {
                    _sendQueue.Add(new SendRequest(r.Id, r.Name));
                    continue;
                }

                Debug.LogWarning($"[KSP-AP] Location send failed for '{r.Name}': {r.Error}");
                checkedLocationIds?.Remove(r.Id);
                pendingLocationNames.Add(r.Name);
                failure = r.Error;
            }
            if (failure != null)
                onSendFailed?.Invoke(failure);
        }

        private void GrantLocalReward(bool grantScience)
        {
            onLocationReported?.Invoke();
            if (grantScience && ResearchAndDevelopment.Instance != null)
                ResearchAndDevelopment.Instance.AddScience(
                    MissionScienceBonus, TransactionReasons.ScienceTransmission);
        }

        // Reports all unchecked slots for a body/event pair (up to event scale).
        private void ReportBodyEvent(string bodyName, string eventName)
        {
            // A hidden (undiscovered) body reports nothing — its checks are gated
            // behind its Discover item in AP logic. Once revealed (by item or, in
            // allow-undiscovered mode, by flying there), events fire normally.
            if (BodyUnlockManager.IsHidden(bodyName)) return;

            if (!eventScale.TryGetValue(eventName, out int scale))
            {
                Debug.LogWarning($"[KSP-AP] Unknown event type: '{eventName}'");
                return;
            }
            for (int slot = 1; slot <= scale; slot++)
                ReportLocation($"{bodyName} {eventName} {slot}", grantScience: true);
        }

        /// <summary>
        /// Queues locations checked while offline for sending to the
        /// now-connected server. The actual sends happen on the send worker;
        /// names whose send fails return to the pending set via
        /// DrainSendFailures for the next reconnect.
        /// </summary>
        private void FlushPending()
        {
            if (pendingLocationNames.Count == 0) return;
            Debug.Log($"[KSP-AP] Flushing {pendingLocationNames.Count} pending offline locations");
            var done = new List<string>();
            foreach (string name in new List<string>(pendingLocationNames))
            {
                long id = session.Locations.GetLocationIdFromName(session.ConnectionInfo.Game, name);
                if (id < 0)
                {
                    Debug.LogWarning($"[KSP-AP] Flush: unknown location '{name}', skipping");
                    done.Add(name);
                    continue;
                }
                if (!checkedLocationIds.Add(id))
                {
                    done.Add(name); // server already has it
                    continue;
                }
                _sendQueue.Add(new SendRequest(id, name));
                done.Add(name);
            }
            foreach (var name in done)
                pendingLocationNames.Remove(name);
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
        // KSC biome science detection
        // ------------------------------------------------------------------

        // Checks a ScienceSubject.id for KSC biome science and reports the
        // matching AP location. Called from both OnScienceReceived and
        // OnVesselRecovered so that we catch science regardless of how it
        // reaches R&D (transmission, recovery dialog, or direct recovery).
        private void TryMatchKscBiome(string subjectId)
        {
            string homeSrfLanded = $"{KSPArchipelagoMod.StartingBody}SrfLanded";
            int idx = subjectId.IndexOf(homeSrfLanded, StringComparison.Ordinal);
            if (idx < 0) return;

            string biome = subjectId.Substring(idx + homeSrfLanded.Length);
            if (string.IsNullOrEmpty(biome)) return;

            // Try exact match first, then StartsWith for sub-biomes
            string locationName = null;
            if (kscBiomeToLocation.TryGetValue(biome, out string exact))
            {
                locationName = exact;
            }
            else
            {
                // Sub-biome fallback: "VABMainBuilding" → starts with "VAB"
                foreach (var kvp in kscBiomeToLocation)
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
        // from the stored ScienceData config nodes. The return/sample checks a
        // recovery earns arrive separately, via OnMilestone.
        private void OnVesselRecovered(ProtoVessel vessel, bool quick)
        {
            if (vessel == null) return;

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
        // Home-body altitude polling
        // ------------------------------------------------------------------

        private void PollHomeAltitude()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            string home = KSPArchipelagoMod.StartingBody;
            if (v == null || v.mainBody?.name != home) return;
            if (v.Landed || v.Splashed) return;

            double alt = v.altitude;
            foreach (int threshold in homeAltThresholds)
            {
                if (alt >= threshold && !checkedLocationIds.Contains(altitudeIds[threshold]))
                    ReportLocation($"{home} {threshold / 1000}km Altitude", grantScience: true);
            }
        }

        // ------------------------------------------------------------------
        // KSP event handlers
        // ------------------------------------------------------------------

        private void OnFlyBy(Vessel vessel, CelestialBody body)
        {
            if (!FlightMilestoneSource.IsMissionVessel(vessel)) return;
            ReportBodyEvent(body.name, "Flyby");
        }

        private void OnVesselSOIChanged(GameEvents.HostedFromToAction<Vessel, CelestialBody> data)
        {
            // Report flyby when entering a body's SOI from its parent SOI.
            // onFlyBy only fires for "new" SOIs, so it misses the home body.
            // This catches that case and acts as belt-and-suspenders for all bodies.
            // Leaving a moon back to its planet is filtered out because
            // data.from (the moon) != data.to.referenceBody (the planet's parent).
            if (!FlightMilestoneSource.IsMissionVessel(data.host)) return;
            CelestialBody to = data.to;
            if (to == null) return;
            bool arrivingFromParent = data.from == to.referenceBody;

            // Arrival at a still-hidden body (from its parent SOI). In
            // allow-undiscovered mode this reveals the body locally so the flyby
            // check below fires; otherwise it's a fatal collision with a body the
            // player couldn't see, and no check fires.
            if (arrivingFromParent && BodyUnlockManager.IsHidden(to.bodyName))
            {
                if (BodyUnlockManager.AllowUndiscovered)
                {
                    Debug.Log($"[KSP-AP] Fly-to-reveal: entered SOI of hidden body {to.bodyName}");
                    BodyUnlockManager.RevealByName(to.bodyName);
                    BodyUnlockManager.MarkFlownReveal(to.bodyName);
                    // Persist server-side so the reveal survives a restart.
                    BodyDiscoveryStore.Save(session, BodyUnlockManager.FlownReveals);
                }
                else
                {
                    Debug.Log($"[KSP-AP] Undiscovered-body collision: {to.bodyName} — destroying {data.host?.vesselName}");
                    var host = UnityEngine.Object.FindObjectOfType<KSPArchipelagoMod>();
                    if (host != null)
                        VesselDestruction.Destroy(host, data.host,
                            "<color=red>STRUCTURAL FAILURE:</color> collided with undiscovered body.");
                    return;
                }
            }

            if (arrivingFromParent)
                ReportBodyEvent(to.name, "Flyby");
        }

        private void OnOrbit(Vessel vessel, CelestialBody body)
        {
            if (!FlightMilestoneSource.IsMissionVessel(vessel)) return;
            ReportBodyEvent(body.name, "Orbit");
        }

        private void OnEscape(Vessel vessel, CelestialBody body)
        {
            if (!FlightMilestoneSource.IsMissionVessel(vessel)) return;
            // Entering a moon's SOI (e.g. Duna→Ike) fires onEscape for the parent.
            // Only report SOI Leave for a true system escape, not moon encounters.
            CelestialBody newBody = vessel.mainBody;
            if (newBody != null && newBody.referenceBody == body)
                return;

            ReportBodyEvent(body.name, "SOI Leave");
        }

        private void OnLand(Vessel vessel, CelestialBody body)
        {
            if (!FlightMilestoneSource.IsMissionVessel(vessel)) return;
            HandleTouchdown(vessel, body);
        }

        // Landing credit AWAY from home. Shared by OnLand and the SPLASHED
        // branch of OnSituationChange — onLand does not fire for every
        // splashdown path, and ocean touchdowns count as landings in the
        // generator's model.
        //
        // Home touchdowns are deliberately not handled here: they are a
        // FlightMilestone (FlightMilestoneSource hooks the same two signals for
        // the home body) and OnMilestone applies the "must have orbited home
        // first" gate that a sub-orbital hop must not pass.
        private void HandleTouchdown(Vessel vessel, CelestialBody body)
        {
            if (body.name == KSPArchipelagoMod.StartingBody) return;
            ReportBodyEvent(body.name, "Landing");
            if (vessel.GetCrewCount() > 0)
                ReportBodyEvent(body.name, "Crewed Landing");
        }

        /// <summary>
        /// The one place a "the craft came home" milestone becomes AP checks.
        /// Replaces four separately-hooked handlers (recovery, both stock
        /// onReturnFrom* events, and the home touchdown), all of which used to
        /// answer "what did this flight prove" differently.
        ///
        /// Every award reads the flight-log SET for a body and asks for exactly
        /// the entry that tier needs — no tier implies another (see
        /// MissionAchievement). That is what stops a Duna flyby from claiming
        /// the land-and-return check, and what lets a direct-entry landing take
        /// Return without ever having orbited.
        ///
        /// ReportLocation is idempotent, so overlapping milestones (a landing
        /// at home followed by recovering the same craft) cost nothing.
        /// </summary>
        private void OnMilestone(FlightMilestone milestone)
        {
            string home = KSPArchipelagoMod.StartingBody;

            // Home tiers, realigned: at home the surface tier is the trivial one
            // — the craft is by definition home, so walking out at the pad and
            // recovering satisfies it. Orbit Return is the one that costs a
            // launch. ({home} SOI Return is banned server-side and never
            // reported: no such location exists.)
            ReportBodyEvent(home, "Return");

            bool orbitedHome = milestone.HasAchievement(home, MissionAchievement.Orbit);
            if (orbitedHome)
                ReportBodyEvent(home, "Orbit Return");

            // Home Landing/Crewed Landing keep their "must have orbited first"
            // gate (a sub-orbital hop is not a landing mission), except on a
            // ReturnedHome milestone: arriving from another body is a home
            // landing by construction, which is how the stock onReturnFrom*
            // handlers credited it.
            if (orbitedHome || milestone.Kind == FlightMilestoneKind.ReturnedHome)
            {
                ReportBodyEvent(home, "Landing");
                if (milestone.CrewCount > 0)
                    ReportBodyEvent(home, "Crewed Landing");
            }

            // One tier per entry the log actually holds, independently.
            foreach (var entry in milestone.AchievementsByBody)
            {
                if (entry.Key == home) continue;
                HashSet<MissionAchievement> proved = entry.Value;
                if (proved.Contains(MissionAchievement.Flyby))
                    ReportBodyEvent(entry.Key, "SOI Return");
                if (proved.Contains(MissionAchievement.Orbit))
                    ReportBodyEvent(entry.Key, "Orbit Return");
                if (proved.Contains(MissionAchievement.Surface))
                    ReportBodyEvent(entry.Key, "Return");
            }

            // A recovered surface sample proves both halves of the surface tier
            // — the craft reached that surface AND it came home — so it awards
            // Return as well as Sample Return. This is the backstop for a craft
            // with no ModuleTripLogger and for the EVA bailout, where the sample
            // rides home on a kerbal whose "vessel" has no flight log. It says
            // nothing about flybys or orbits, so those are never inferred.
            foreach (string sampleBody in milestone.SurfaceSampleBodies)
            {
                ReportBodyEvent(sampleBody, "Sample Return");
                ReportBodyEvent(sampleBody, "Return");
            }
        }

        private void OnSituationChange(GameEvents.HostedFromToAction<Vessel, Vessel.Situations> data)
        {
            Vessel v = data.host;
            if (!FlightMilestoneSource.IsMissionVessel(v)) return;
            CelestialBody mainBody = v.mainBody;
            string body = mainBody?.name;
            string home = KSPArchipelagoMod.StartingBody;

            // Splashdown on any body with oceans. onLand does not fire for
            // SPLASHED, so we catch the situation transition here. This is
            // a single body-agnostic AP location: splashing on Kerbin, Eve,
            // or Laythe all check the same "Splashdown" location.
            if (data.to == Vessel.Situations.SPLASHED
                && mainBody != null && mainBody.ocean)
            {
                ReportLocation("Splashdown", grantScience: true);
                HandleTouchdown(v, mainBody);
            }

            // First Launch: any transition to FLYING or SUB_ORBITAL on the home body.
            // Don't check data.from — KSP can insert PRELAUNCH→LANDED→FLYING
            // when physics settles the vessel on the pad before launch.
            if (body == home
                && (data.to == Vessel.Situations.FLYING || data.to == Vessel.Situations.SUB_ORBITAL)
                && !checkedLocationIds.Contains(homeFirstLaunchId))
            {
                ReportLocation($"{home} First Launch", grantScience: true);
            }

            // First Landing: transition to LANDED on the home body from FLYING
            // or SUB_ORBITAL (not from PRELAUNCH — that's sitting on the pad).
            if (body == home
                && data.to == Vessel.Situations.LANDED
                && (data.from == Vessel.Situations.FLYING || data.from == Vessel.Situations.SUB_ORBITAL)
                && !checkedLocationIds.Contains(homeFirstLandingId))
            {
                ReportLocation($"{home} First Landing", grantScience: true);
            }
        }

        private void OnFlagPlant(Vessel flagVessel)
        {
            string body = flagVessel.mainBody?.name;
            if (body == null) return;
            ReportBodyEvent(body, "Flag Plant");
        }

        private void OnStageSeparation(EventReport report)
        {
            if (checkedLocationIds.Contains(homeFirstStagingId)) return;
            Vessel v = FlightGlobals.ActiveVessel;
            string home = KSPArchipelagoMod.StartingBody;
            if (v == null || v.mainBody?.name != home) return;
            ReportLocation($"{home} First Staging", grantScience: true);
        }

        // Broadcast a DeathLink for the player's OWN death. Guards ensure only a
        // real, player-caused death of the active mission vessel sends — exactly
        // once per vessel. A mod-initiated kill never rebroadcasts (anti-loop)
        // because OnModDestroyedVessel pre-marks the victim in _deathSent before
        // the first part explodes. No-op when DeathLink is off (onDeath == null)
        // or in practice mode.
        private void SendDeath(string cause)
        {
            if (onDeath == null || SimulationMode) return;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || !FlightMilestoneSource.IsMissionVessel(v)) return;   // ignore debris / detached boosters
            if (!_deathSent.Add(v.persistentId)) return;                          // already sent for this vessel
            _deathSentThisFlight = true;
            onDeath(cause);
        }

        // A DeathLink-worthy loss of the craft: the root part of the vessel the
        // player is flying is about to die, which is KSP's own definition of
        // "vessel destroyed" — Part.Die() calls vessel.Die() exactly when
        // rootPart == this. Deliberately NOT onCrash: that fires once per part
        // destroyed above its crash tolerance, so a snapped landing leg, a
        // sheared solar panel, or a spent booster hitting the ground inside
        // physics range all read as deaths while the mission flies on.
        //
        // Vessel.Die() destroys its parts with Object.Destroy rather than
        // Part.Die(), so recovery and on-rails cleanup never reach this hook.
        private void OnRootPartWillDie(Part p)
        {
            if (p == null) return;
            Vessel v = p.vessel;
            if (v == null || v.rootPart != p) return;
            // Only the craft the player is actually flying. A jettisoned stage
            // or a nearby wreck losing its root is not the player's death.
            if (FlightGlobals.ActiveVessel != v) return;
            SendDeath("was destroyed");
        }

        // The mod destroyed a craft — an incoming DeathLink, the launch-pad mass
        // gate, or a collision with an undiscovered body. Settle its DeathLink
        // bookkeeping up front: the explosion this is about to cause must not
        // ping-pong a death back, and the player must not be billed a revert
        // death for backing out of a wreck the mod created. Fires before the
        // kill is scheduled, so the mark is always in place first.
        //
        // Only the craft the player is flying settles the FLIGHT: an unattended
        // probe that drifts into a hidden body while you fly something else has
        // nothing to do with whether reverting your current flight is a scum.
        private void OnModDestroyedVessel(Vessel v)
        {
            if (v == null) return;
            _deathSent.Add(v.persistentId);
            if (FlightGlobals.ActiveVessel == v)
                _deathSentThisFlight = true;
        }

        // Revert-to-Launch / Revert-to-Editor with death_link_on_revert on: undoing
        // a flight that actually happened costs a death, so a failure can't be
        // save-scummed away for free.
        //
        // Both revert events fire while the flight scene is still live (FlightDriver
        // fires them immediately before LoadScene / StartEditor), so ActiveVessel is
        // still readable here.
        private void OnRevert(FlightState state)
        {
            if (onDeath == null || !deathLinkOnRevert || SimulationMode) return;
            // Already paid for this flight — a crash, a crewed loss, or an incoming
            // DeathLink that destroyed the craft. Reverting the wreck is free.
            if (_deathSentThisFlight) return;

            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;
            // Never launched: still clamped on the pad, or on the runway below the
            // 2.5 m/s surface speed at which KSP drops PRELAUNCH. Checking a design
            // or fixing a misclick is free. This is KSP's own "can revert to post-
            // init" test (FlightDriver: CanRevertToPostInit = situation == PRELAUNCH),
            // and PRELAUNCH never comes back once the craft has moved.
            if (v.situation == Vessel.Situations.PRELAUNCH) return;

            _deathSentThisFlight = true;
            onDeath("reverted a flight");
        }

        // A new flight (launch, Revert-to-Launch, or loading into flight) starts a
        // fresh death-dedup window. Without this, a persistentId pinned in
        // _deathSent by an earlier crash — or by a received-death kill via
        // OnModDestroyedVessel — would permanently bar a reflown craft (same
        // persistentId after a revert) from broadcasting a death again.
        private void OnFlightReadyResetDeaths()
        {
            _deathSent.Clear();
            _deathSentThisFlight = false;
        }

        // A Kerbal died — asphyxiation, EVA fall, decompression, or a crewed part
        // being destroyed. Always a DeathLink-worthy death even when the craft
        // itself survives. onCrewKilled carries no origin part (KSP fires it with
        // a null EventReport.origin), so SendDeath's ActiveVessel guards apply.
        private void OnCrewKilled(EventReport report)
        {
            SendDeath("lost their crew");
        }

        // onCrash / onCrashSplashdown fire per-part on impact destruction.
        // We only care about the first crash ever on the home body.
        // Use ActiveVessel (like OnStageSeparation) because KSP reclassifies
        // parts as Debris before firing crash events, breaking IsMissionVessel.
        // ActiveVessel is the craft the player is flying, so detached boosters
        // that crash separately won't trigger this.
        //
        // Location detection only — DeathLink lives on OnRootPartWillDie.
        private void OnCrash(EventReport report)
        {
            if (checkedLocationIds.Contains(homeFirstCrashId)) return;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || !FlightMilestoneSource.IsMissionVessel(v)) return;
            string home = KSPArchipelagoMod.StartingBody;
            if (v.mainBody?.name != home) return;
            ReportLocation($"{home} First Crash", grantScience: true);
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
