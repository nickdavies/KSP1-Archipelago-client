using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
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

        /// True for player-created vessels (ships, probes, EVA kerbals, etc.).
        /// False for asteroids, debris, and flags.
        private static bool IsMissionVessel(Vessel v)
        {
            if (v == null) return false;
            return v.vesselType != VesselType.SpaceObject &&
                   v.vesselType != VesselType.Flag &&
                   v.vesselType != VesselType.Debris;
        }

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

        // Runtime-only: vessel persistentIds that have achieved home-body orbit.
        // Used to gate the home-body "Return" event — only vessels that
        // previously orbited home qualify. Not persisted; onOrbit re-fires
        // on scene load for orbiting vessels.
        private readonly HashSet<uint> _vesselsOrbitedHome = new HashSet<uint>();

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
                                Dictionary<string, object> slotData = null)
        {
            session = newSession;
            this.onLocationReported = onLocationReported;
            this.onSendFailed = onSendFailed;
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
        // from the stored ScienceData config nodes.
        private void OnVesselRecovered(ProtoVessel vessel, bool quick)
        {
            if (vessel == null) return;

            // Award Return + Sample Return for every body whose surface sample is on
            // board. A surface sample at recovery is strictly stronger evidence than the
            // stock onReturnFromSurface/onReturnFromOrbit flight-log signal, which misses
            // the EVA-bailout case: a kerbal carries the sample down under personal chute
            // from Kerbin orbit on a "vessel" whose flight log never recorded the body.
            // Both locations are idempotent, so re-firing in the common path is a no-op.
            foreach (string body in CollectSurfaceSampleBodies(vessel))
            {
                ReportBodyEvent(body, "Return");
                ReportBodyEvent(body, "Sample Return");
            }

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
            if (!IsMissionVessel(vessel)) return;
            ReportBodyEvent(body.name, "Flyby");
        }

        private void OnVesselSOIChanged(GameEvents.HostedFromToAction<Vessel, CelestialBody> data)
        {
            // Report flyby when entering a body's SOI from its parent SOI.
            // onFlyBy only fires for "new" SOIs, so it misses the home body.
            // This catches that case and acts as belt-and-suspenders for all bodies.
            // Leaving a moon back to its planet is filtered out because
            // data.from (the moon) != data.to.referenceBody (the planet's parent).
            if (!IsMissionVessel(data.host)) return;
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
            if (!IsMissionVessel(vessel)) return;
            ReportBodyEvent(body.name, "Orbit");
            if (body.name == KSPArchipelagoMod.StartingBody)
                _vesselsOrbitedHome.Add(vessel.persistentId);
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
            HandleTouchdown(vessel, body);
        }

        // Shared by OnLand and the SPLASHED branch of OnSituationChange —
        // onLand does not fire for SPLASHED, and ocean touchdowns count as
        // landings in the generator's model.
        private void HandleTouchdown(Vessel vessel, CelestialBody body)
        {
            if (body.name == KSPArchipelagoMod.StartingBody)
            {
                // Home-body Landing/Return require the vessel to have achieved
                // home orbit first — matches the generator's home LAND/RETURN
                // profile (ascent + deorbit). Sub-orbital hops don't qualify.
                if (!_vesselsOrbitedHome.Contains(vessel.persistentId))
                    return;
                ReportBodyEvent(body.name, "Return");
                foreach (string sampleBody in CollectSurfaceSampleBodies(vessel))
                    ReportBodyEvent(sampleBody, "Sample Return");
            }
            ReportBodyEvent(body.name, "Landing");
            if (vessel.GetCrewCount() > 0)
                ReportBodyEvent(body.name, "Crewed Landing");
        }

        private void OnSituationChange(GameEvents.HostedFromToAction<Vessel, Vessel.Situations> data)
        {
            Vessel v = data.host;
            if (!IsMissionVessel(v)) return;
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

        // onReturnFromOrbit fires when a vessel returns to the home body after
        // having orbited another body. The body parameter is the remote body
        // (e.g. Mun), not the home.
        private void OnReturnFromOrbit(Vessel vessel, CelestialBody body)
        {
            if (!IsMissionVessel(vessel)) return;
            string home = KSPArchipelagoMod.StartingBody;
            ReportBodyEvent(body.name, "Return");
            // Also count as a home-body landing (deorbit + recovery)
            ReportBodyEvent(home, "Landing");
            if (vessel.GetCrewCount() > 0)
                ReportBodyEvent(home, "Crewed Landing");
        }

        // onReturnFromSurface fires when a vessel returns to the home body
        // after having landed on another body.
        private void OnReturnFromSurface(Vessel vessel, CelestialBody body)
        {
            if (!IsMissionVessel(vessel)) return;
            string home = KSPArchipelagoMod.StartingBody;
            ReportBodyEvent(body.name, "Return");
            foreach (string sampleBody in CollectSurfaceSampleBodies(vessel))
                ReportBodyEvent(sampleBody, "Sample Return");
            // Also count as a home-body landing (deorbit + recovery)
            ReportBodyEvent(home, "Landing");
            if (vessel.GetCrewCount() > 0)
                ReportBodyEvent(home, "Crewed Landing");
        }

        private void OnStageSeparation(EventReport report)
        {
            if (checkedLocationIds.Contains(homeFirstStagingId)) return;
            Vessel v = FlightGlobals.ActiveVessel;
            string home = KSPArchipelagoMod.StartingBody;
            if (v == null || v.mainBody?.name != home) return;
            ReportLocation($"{home} First Staging", grantScience: true);
        }

        // onCrash / onCrashSplashdown fire per-part on impact destruction.
        // We only care about the first crash ever on the home body.
        // Use ActiveVessel (like OnStageSeparation) because KSP reclassifies
        // parts as Debris before firing crash events, breaking IsMissionVessel.
        // ActiveVessel is the craft the player is flying, so detached boosters
        // that crash separately won't trigger this.
        private void OnCrash(EventReport report)
        {
            if (checkedLocationIds.Contains(homeFirstCrashId)) return;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || !IsMissionVessel(v)) return;
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
