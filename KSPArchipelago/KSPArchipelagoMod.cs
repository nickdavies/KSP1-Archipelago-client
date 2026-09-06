using System;
using System.Collections.Generic;
using System.Threading;

using Newtonsoft.Json.Linq;
using UnityEngine;
using KSP.UI.Screens;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Models;
using Archipelago.MultiClient.Net.Helpers;
using Archipelago.MultiClient.Net.BounceFeatures.DeathLink;
using Archipelago.MultiClient.Net.Exceptions;


namespace KSPArchipelago
{
    public static class KSPArchipelagoPartsManager
    {
        // Populated from slot_data at connect time.
        private static Dictionary<string, float> SciencePackAmounts = new Dictionary<string, float>();

        public static bool TryGetScienceAmount(string itemName, out float amount)
            => SciencePackAmounts.TryGetValue(itemName, out amount);

        /// <summary>
        /// Parse science pack definitions from slot_data. Returns true on success.
        /// </summary>
        public static bool SetSciencePacksFromSlotData(Dictionary<string, object> slotData)
        {
            if (slotData == null) return false;
            if (slotData.TryGetValue("science_packs", out object spObj)
                && spObj is JObject spDict)
            {
                SciencePackAmounts = new Dictionary<string, float>();
                foreach (var kvp in spDict)
                    SciencePackAmounts[kvp.Key] = (float)(int)kvp.Value;
                Debug.Log($"[KSP-AP] Parsed {SciencePackAmounts.Count} science packs from slot data");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Single path for all item processing. State mutations (progressive counts,
        /// part tracking, experimental parts) always run. Toast/science only when flagged.
        /// </summary>
        public static void GiveItem(string itemName, string senderName,
                                    string locationName,
                                    bool showToast = true,
                                    ItemFlags flags = ItemFlags.None)
        {
            string toastText;

            // Trap items: recognized here ONLY to stay out of the part-lookup
            // fallthrough below. All trap state (queue, toasts, firing) runs
            // at the awarded-index sites via TrapManager.NoteReceived —
            // GiveItem replays in the EDITOR with a null scenario and must
            // stay side-effect free for traps.
            if (Traps.TrapManager.IsTrapItem(itemName) || (flags & ItemFlags.Trap) != 0)
                return;

            // Buff items: count here, exactly like Progressive Launch Pad below.
            // Unlike traps this IS safe in the EDITOR replay path — the count is
            // zeroed by ResetProgressiveState before any full re-walk, and the
            // effect is applied as an absolute (stock * total) rather than an
            // increment, so replaying can't compound. The actual field writes
            // happen on the main thread in Update() via BuffManager.Apply.
            // Consumable buffs: banked as a charge, spent later from the mod
            // menu. Recognised here so it stays out of the part-lookup
            // fallthrough; the index-keyed bookkeeping happens at the awarded
            // sites (ProcessAllItems / ProcessNewItems), which is where the
            // item index is actually known.
            if (Buffs.ChargeManager.IsConsumableItem(itemName))
            {
                if (showToast)
                {
                    toastText = $"AP: Received {itemName} — spend it from the AP menu";
                    ScreenMessages.PostScreenMessage(toastText, 6f, ScreenMessageStyle.UPPER_CENTER);
                    PostToMessageSystem(senderName, locationName, toastText);
                }
                return;
            }

            if (Buffs.BuffManager.IsBuffItem(itemName))
            {
                Buffs.BuffManager.NoteReceived(itemName);
                if (showToast)
                {
                    toastText = $"AP: Received {itemName} — permanent, stacks";
                    ScreenMessages.PostScreenMessage(toastText, 5f, ScreenMessageStyle.UPPER_CENTER);
                    PostToMessageSystem(senderName, locationName, toastText);
                }
                return;
            }

            // Science packs: toast only. R&D award and TotalApScienceAwarded are
            // written together by the caller (ProcessAllItems / ProcessNewItems) so
            // all three happen at once and can't desync
            if (SciencePackAmounts.TryGetValue(itemName, out float amount))
            {
                if (showToast)
                {
                    toastText = $"AP: Received {itemName} (+{amount} science)";
                    ScreenMessages.PostScreenMessage(toastText, 4f, ScreenMessageStyle.UPPER_CENTER);
                    PostToMessageSystem(senderName, locationName, toastText);
                }
                return;
            }

            // Discover-body items: reveal the celestial body. The item→body map
            // comes from slot_data (body_item_map); reveals replay via
            // ProcessAllItems on reconnect. No science, no part — just visibility.
            if (BodyUnlockManager.TryGetBodyForItem(itemName, out string discoveredBody))
            {
                BodyUnlockManager.RevealByName(discoveredBody);
                if (showToast)
                {
                    toastText = $"AP: Discovered {discoveredBody}";
                    ScreenMessages.PostScreenMessage(toastText, 5f, ScreenMessageStyle.UPPER_CENTER);
                    PostToMessageSystem(senderName, locationName, toastText);
                }
                return;
            }

            var mod = UnityEngine.Object.FindObjectOfType<KSPArchipelagoMod>();

            // Contract items: record receipt only. Offering/accepting happens
            // from a stable scene (Update → ReconcileOffers, and KSP's stock
            // poll), never here — GiveItem can run mid scene-load, where adding
            // a contract races ContractSystem's own load and gets clobbered.
            if (Contracts.ApContractManager.IsContractItem(itemName))
            {
                Contracts.ApContractManager.MarkReceived(itemName);
                if (showToast)
                {
                    toastText = $"AP: New contract available — {itemName}";
                    ScreenMessages.PostScreenMessage(toastText, 5f, ScreenMessageStyle.UPPER_CENTER);
                    PostToMessageSystem(senderName, locationName, toastText);
                }
                return;
            }

            // Progressive Launch Pad: raise the buildable launch-mass cap. The
            // server provides the cap thresholds; the collected count selects
            // the current one (CurrentLaunchPadMassCap). SyncLaunchPadMassCap
            // writes that into the building (stock VAB display + alien KK pad
            // MaxCraftMass via the GetCraftMassLimit override); the over-cap
            // detonator enforces it as a hard backstop on the pad. The cap is
            // NOT driven through the LaunchPad facility level — the career hack
            // maxes that facility, so a level-derived cap would never bind.
            if (itemName == "Progressive Launch Pad")
            {
                if (mod != null)
                {
                    mod.IncrementProgressiveCount(itemName);
                    mod.SyncLaunchPadMassCap();
                    if (showToast)
                    {
                        float newCap = mod.CurrentLaunchPadMassCap;
                        string capStr = float.IsPositiveInfinity(newCap) ? "unlimited" : $"{newCap:F0} t";
                        toastText = $"AP: Launch Pad Upgraded → {capStr} max launch mass";
                        ScreenMessages.PostScreenMessage(toastText, 4f, ScreenMessageStyle.UPPER_CENTER);
                        PostToMessageSystem(senderName, locationName, toastText);
                    }
                }
                return;
            }

            // Progressive R&D unlocks the artificial tech-tree band (RDLevel),
            // which is independent of the R&D *facility* level. The facility level
            // is driven by the facility_item_map below — the R&D building is now
            // gated (starts un-upgraded; 2nd copy → mid level, 4th → max), so its
            // science-cost cap and surface-sample gate ride the Progressive R&D count.
            bool isRD = itemName == "Progressive R&D";
            if (isRD && mod != null)
            {
                mod.IncrementRDLevel();
                if (showToast)
                {
                    toastText = $"AP: Research & Development Level {mod.RDLevel}";
                    ScreenMessages.PostScreenMessage(toastText, 4f, ScreenMessageStyle.UPPER_CENTER);
                    PostToMessageSystem(senderName, locationName, toastText);
                }
            }

            // Facility progressives: drive facility levels generically from the
            // server-provided slot_data map (item → facilities + count thresholds),
            // so the client hardcodes no item names and future buildings are a
            // server-only change.
            var facMap = CareerUpgradesManager.Instance?.FacilityItemMap;
            if (facMap != null && facMap.TryGetValue(itemName, out FacilityItemSpec facSpec))
            {
                int count = mod?.IncrementProgressiveCount(itemName) ?? 0;
                int level = CareerUpgradesManager.LevelForCount(facSpec.Thresholds, count);
                int applied = 0;
                foreach (string fid in facSpec.Facilities)
                    applied = CareerUpgradesManager.Instance?.RaiseApGrantedLevelTo(fid, level) ?? 0;
                if (showToast && !isRD)   // R&D already toasted its band above
                {
                    toastText = $"AP: {itemName} -> Lv{applied + 1}";
                    ScreenMessages.PostScreenMessage(toastText, 4f, ScreenMessageStyle.UPPER_CENTER);
                    PostToMessageSystem(senderName, locationName, toastText);
                }
                return;
            }
            if (isRD) return;   // R&D handled (band bumped); not a part

            // Individual part items: unlock the part as experimental.
            AvailablePart part = PartLoader.getPartInfoByName(itemName);
            if (part != null)
            {
                ResearchAndDevelopment.AddExperimentalPart(part);
                Debug.Log($"[KSP-AP] Unlocked part '{itemName}' ({part.title})");
                if (showToast)
                {
                    toastText = $"AP: Unlocked {part.title}";
                    ScreenMessages.PostScreenMessage(toastText, 4f, ScreenMessageStyle.UPPER_CENTER);
                    PostToMessageSystem(senderName, locationName, toastText);
                }
            }
            else
            {
                Debug.LogWarning($"[KSP-AP] GiveItem: part not found '{itemName}'");
            }
        }

        private static void PostToMessageSystem(string senderName, string locationName, string title)
        {
            if (MessageSystem.Instance == null) return;

            string body = title;
            if (senderName != null)
                body += $"\nFrom: {senderName}";
            if (locationName != null)
                body += $"\nLocation: {locationName}";

            var msg = new MessageSystem.Message(
                title, body,
                MessageSystemButton.MessageButtonColor.BLUE,
                MessageSystemButton.ButtonIcons.MESSAGE
            );
            MessageSystem.Instance.AddMessage(msg);
        }

        public static void ScrubTechTree()
        {
            foreach (AvailablePart part in PartLoader.LoadedPartsList)
            {
                if (part.TechRequired != "inaccessable")
                    part.TechRequired = "inaccessable";
            }
        }

        public static void ClearAllExperimentalParts()
        {
            foreach (AvailablePart part in PartLoader.LoadedPartsList)
                ResearchAndDevelopment.RemoveExperimentalPart(part);
        }

        /// <summary>
        /// Computes expected total science from all received science packs,
        /// compares against what the save file says was already awarded,
        /// and awards only the delta. This makes reconnects idempotent and
        /// save/load revert correctly (since TotalApScienceAwarded is save-tied).
        /// </summary>
        public static void ReconcileApScience(ArchipelagoSession session)
        {
            if (session == null || ResearchAndDevelopment.Instance == null) return;

            var scenario = ApScenarioModule.Instance;
            if (scenario == null) return;

            float expectedScience = 0f;
            foreach (var item in session.Items.AllItemsReceived)
            {
                if (item.ItemName != null && SciencePackAmounts.TryGetValue(item.ItemName, out float amount))
                    expectedScience += amount;
            }

            float alreadyAwarded = scenario.TotalApScienceAwarded;
            float delta = expectedScience - alreadyAwarded;

            if (delta > 0.01f)
            {
                /// ResearchAndDevelopment.Instance.AddScience(delta, TransactionReasons.Cheating);
                Debug.Log($"[KSP-AP] ReconcileApScience: expected={expectedScience}, awarded={alreadyAwarded}, delta={delta}");
            }
            else
            {
                Debug.Log($"[KSP-AP] ReconcileApScience: no delta (expected={expectedScience}, awarded={alreadyAwarded})");
            }
        }
    }

    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class KSPArchipelagoMod : MonoBehaviour
    {
        private readonly object sessionLock = new object();
        private ArchipelagoSession session;
        private MissionTracker missionTracker;

        // --- DeathLink ---
        // Library service, created per-connect only when the seed enabled DeathLink.
        // Manages the "DeathLink" tag and Bounce send/receive for us.
        private DeathLinkService _deathLink;
        // Incoming death, coalesced to at most one pending kill. Set on the socket
        // thread by OnDeathLinkReceived, drained on the main thread in Update().
        private readonly object _deathLock = new object();
        private bool _hasPendingDeath = false;
        private string _pendingDeathCause = null;
        // Main-thread-only: the vessel the pending kill has been "armed" against and
        // whether it's a fresh launch (wait 30s of mission time) or an existing
        // in-flight craft (kill on the next frame). uint.MaxValue == not yet armed.
        private uint _deathArmedVesselId = uint.MaxValue;
        private bool _deathArmedDelay = false;
        /// <summary>
        /// Public accessor so career-mode managers can fire location checks
        /// without re-implementing the AP-session plumbing. Returns null
        /// until Start() runs.
        /// </summary>
        public MissionTracker Tracker => missionTracker;
        private bool gameLoaded = false;
        public Archipelago.APConsole Console { get; private set; }

        // Thread-safe queue of user-visible messages drained on the main thread
        // by Update(). Lets background callbacks (socket close, reconnect) post
        // toasts without touching Unity APIs off-thread.
        private readonly Queue<string> _userToastQueue = new Queue<string>();
        private readonly object _toastLock = new object();

        /// <summary>Thread-safe: enqueue a toast to be shown on the next Update().</summary>
        public void EnqueueUserToast(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            lock (_toastLock) _userToastQueue.Enqueue(message);
        }

        // Pending starting-body hand-off to the KSC bridge. HandleConnect
        // runs on a ThreadPool worker (the AP library's connect path),
        // but the bridge ultimately drives Unity GameObject cloning via
        // KK — segfaults if called off the main thread. Stash the body +
        // its KSC site row here from HandleConnect; Update() drains and
        // invokes the bridge on the main thread. Bundled into one object
        // so the Interlocked.Exchange publishes name and coordinates
        // atomically across the two threads.
        private sealed class PendingStartingBody
        {
            public readonly string Name;
            public readonly double Lat, Lon, TerrainAlt;
            public readonly bool SkipMapDecal;
            public PendingStartingBody(string name, double lat, double lon,
                                       double terrainAlt, bool skipMapDecal)
            {
                Name = name; Lat = lat; Lon = lon;
                TerrainAlt = terrainAlt; SkipMapDecal = skipMapDecal;
            }
        }
        private PendingStartingBody _pendingBridge = null;

        // Pending science_values hand-off. Set by HandleConnect on a
        // worker thread; Update() drains, validates, and applies on the
        // main thread (touches FlightGlobals.Bodies). Null = "no scaling,
        // reset to stock"; non-null = absolute per-body, per-situation
        // values to write verbatim. Validation hard-fails on any missing
        // body or situation field — no silent defaults.
        private Dictionary<string, Dictionary<string, float>> _pendingScienceValues = null;
        private bool _scienceValuesPending = false;
        // Always take one stock-values snapshot per connect, even for
        // Kerbin-home seeds with no science_values payload, so Reset() can
        // restore stock values on disconnect.
        private bool _stockSnapshotPending = false;

        // Pending hidden-bodies config from slot_data (optional; null map = the
        // feature is off for this seed). Parsed by HandleConnect on the connect
        // worker; applied on the main thread in the _needsReset drain, before
        // ProcessAllItems (needs FlightGlobals.Bodies).
        private Dictionary<string, string> _pendingBodyItemMap = null;
        private bool _pendingAllowUndiscovered = true;
        private bool _pendingDeepHide = true;
        // Fly-there reveals retrieved asynchronously from AP DataStorage on
        // connect; the callback stashes them here and Update() applies them on the
        // main thread (SeedFlownReveals reveals any already hidden by then).
        private List<string> _pendingFlownReveals = null;
        private volatile bool _flownRevealsReady = false;
        // Set by HandleDisconnect (which may run on a worker thread) so the
        // main-thread Update drain reveals all bodies — BodyUnlockManager touches
        // Unity APIs and must not run off-thread.
        private volatile bool _bodyResetPending = false;

        // Pending career-hack directives parsed from slot_data on the connect
        // worker thread. Update() drains and applies them on the main thread
        // (CareerHackManager touches Funding/Reputation/facility singletons).
        private volatile CareerDirectives _pendingCareerDirectives = null;

        // Goal-mode threshold watcher re-evaluation request. Set on connect and
        // whenever a location is reported (a completed contract may push the
        // count past a threshold); drained in Update so the watcher's own
        // ReportLocation calls never re-enter the report callback.
        private volatile bool _thresholdsDirty = false;

        // Slot data from AP server.
        public int Difficulty { get; private set; }
        public int TechSlotsPerNode { get; private set; }

        // Name of the slot's home body. Sourced from slot_data; defaults
        // to "Kerbin" when the field is absent.
        public static string StartingBody { get; private set; } = "Kerbin";

        // Progressive Launch Pad tonnage caps from slot_data
        // (tier index → cap, -1 = unlimited). Null when the
        // progressive_launch_pad option is disabled; in that case no mass
        // gating applies.
        public List<float> LaunchPadMassCaps { get; private set; }

        /// <summary>
        /// Current launch-pad mass cap based on collected "Progressive Launch
        /// Pad" items. Returns float.PositiveInfinity when uncapped (option
        /// disabled, or top tier reached).
        /// </summary>
        public float CurrentLaunchPadMassCap
        {
            get
            {
                if (LaunchPadMassCaps == null || LaunchPadMassCaps.Count == 0)
                    return float.PositiveInfinity;
                _progressiveCounts.TryGetValue("Progressive Launch Pad", out int count);
                int idx = System.Math.Min(count, LaunchPadMassCaps.Count - 1);
                float cap = LaunchPadMassCaps[idx];
                return cap < 0 ? float.PositiveInfinity : cap;
            }
        }

        /// <summary>
        /// Write the current Progressive Launch Pad cap into the building so the
        /// in-game readouts match: the GetCraftMassLimit override (stock VAB
        /// mass display + stock pad check) and the alien KK pad's MaxCraftMass
        /// (refreshed through the same override via the KSC bridge). The
        /// LaunchPadOverCapDetonator reads CurrentLaunchPadMassCap directly as
        /// the hard backstop, independent of this. Call after the collected
        /// count changes (item receipt) and after a full item rebuild.
        /// </summary>
        public void SyncLaunchPadMassCap()
        {
            float cap = CurrentLaunchPadMassCap;
            APCareerGameVariables.LaunchPadMassCapTonnes =
                float.IsPositiveInfinity(cap) ? -1f : cap;
            // Alien KK pad (if installed) recomputes MaxCraftMass through the
            // same GetCraftMassLimit override; no-op on Kerbin / no-KK starts.
            StartingBodyBridge.Current?.RefreshPadMassCap();
        }

        // Progressive R&D: current band level (0 = base, up to 3).
        public int RDLevel { get; private set; }
        // Tech node ID → required R&D band (from slot data).
        public Dictionary<string, int> NodeBands { get; private set; }

        // Running count of progressive item copies received (reset on rebuild).
        // Drives the Progressive Launch Pad mass-cap index (CurrentLaunchPadMassCap).
        private Dictionary<string, int> _progressiveCounts = new Dictionary<string, int>();

        // Expose connection state for the UI.
        public bool IsConnected => session != null;
        internal ArchipelagoSession Session => session;
        public int ItemsReceivedCount { get; private set; }
        public int LocationsCheckedCount { get; private set; }
        public string ConnectedSlot { get; private set; }

        // Goal completion: set at connect, checked each Update().
        private bool _goalSent = false;
        private string _goalDisplayName;

        public string GoalDisplayName => _goalDisplayName;
        public int GoalLocationCount => missionTracker?.GoalLocationCount ?? 0;
        public int GoalLocationsChecked => missionTracker?.GoalLocationsChecked ?? 0;
        public List<KeyValuePair<string, bool>> GetGoalStatus()
            => missionTracker?.GetGoalStatus() ?? new List<KeyValuePair<string, bool>>();

        // Contract progress, for the UI panel. The goal contract item(s) are
        // released once ContractsCompleted reaches ContractsRequired; that gate is
        // rolled at generation time (contracts_required_for_goal supports AP's
        // random-range syntax), so the player has no other way to learn the number.
        private int _contractsRequired;
        public int ContractsRequired => _contractsRequired;
        public int ContractsTotal => Contracts.ApContractManager.NonGoalCount;
        public int ContractsCompleted
            => Contracts.ApContractManager.CountCompleted(missionTracker);

        public void IncrementRDLevel()
        {
            RDLevel++;
            Debug.Log($"[KSP-AP] R&D level incremented to {RDLevel}");
        }

        public int IncrementProgressiveCount(string itemName)
        {
            _progressiveCounts.TryGetValue(itemName, out int c);
            _progressiveCounts[itemName] = c + 1;
            return c + 1;
        }

        // Internal notification callback for UI.
        public event Action<string> OnItemReceived;

        // Deferred reset: HandleConnect runs on a background thread but
        // item processing calls Unity APIs that must run on the main thread.
        // This flag tells Update() to do a full rebuild before processing.
        private volatile bool _needsReset = false;

        // Track the last processed index for fast incremental polling.
        private int _lastProcessedIndex = 0;

        // Event-driven contract reconcile. Set dirty by the events that can
        // create owed/healable contracts; Update() runs ReconcileOffers once
        // per dirty cycle. Replaces the old every-30-frames poll, which ran
        // ReconcileOffers (a contract-list walk + repeated FindObjectOfType)
        // ~2x/second for the whole session and caused a periodic main-thread
        // hitch (stutter while connected, smooth the moment you disconnect).
        private bool _contractsDirty = false;
        private ReconcileTrigger _reconcileTrigger = ReconcileTrigger.SceneReady;
        // Coarse safety net + telemetry in case an event path is missed. 30s is
        // imperceptible vs the old 0.5s poll; the trigger is logged so a test
        // cycle shows whether the backstop ever does real work (= a missed event).
        private float _lastReconcileTime = 0f;

        private enum ReconcileTrigger
        { SceneReady, ContractsLoaded, GateFallback, ItemsReceived, Connect, Backstop, ContractCancelled }

        // Request a contract reconcile on the next Update tick where the scene
        // and ContractSystem are ready. Idempotent within a frame; last trigger
        // wins for the log line.
        private void MarkContractsDirty(ReconcileTrigger why)
        {
            _contractsDirty = true;
            _reconcileTrigger = why;
        }

        private void Start()
        {
            DontDestroyOnLoad(this);

            WinConsole.Initialize();
            Debug.Log($"[KSP-AP] KSPArchipelago version {ModVersion.Version}");
            Debug.Log("[KSP-AP] Mod started.");

            missionTracker = new MissionTracker();

            Console = new Archipelago.APConsole(this);
            ThreadStart work = new ThreadStart(Console.Run);
            Thread thread = new Thread(work) { IsBackground = true };
            thread.Start();

            GameEvents.onGameStateLoad.Add(new EventData<ConfigNode>.OnEvent(OnGameStateLoad));
            GameEvents.onGameSceneLoadRequested.Add(OnSceneChange);
            // Buffs: a vessel that was packed when Apply() last ran still has
            // stock values on its parts, so re-apply as it comes off rails.
            // Registered here rather than in MissionTracker because that block
            // is the location-check surface, not a general vessel-lifecycle
            // one, and because TrapManager's poll only ever sees ActiveVessel
            // (it would miss a station you just rendezvoused with).
            GameEvents.onVesselGoOffRails.Add(Buffs.BuffManager.OnVesselGoOffRails);
            GameEvents.onLevelWasLoadedGUIReady.Add(OnLevelLoadedGUIReady);
            GameEvents.Contract.onContractsLoaded.Add(OnContractsLoaded);
            // A cancelled/declined AP contract is still owed — re-offer it.
            GameEvents.Contract.onCancelled.Add(OnContractCancelledOrDeclined);
            GameEvents.Contract.onDeclined.Add(OnContractCancelledOrDeclined);
        }

        private void OnContractCancelledOrDeclined(global::Contracts.Contract c)
        {
            if (session == null) return;
            if (c is Contracts.ApGenericContract)
                MarkContractsDirty(ReconcileTrigger.ContractCancelled);
        }

        // The scene is fully up (GUI ready) — ContractSystem has finished
        // loading from the save, so it's now safe to add/accept contracts.
        // Update() reconciles while this stays true; it's cleared the instant a
        // scene transition is requested so we never touch ContractSystem
        // mid-load (that race clobbered force-offered contracts).
        private volatile bool _sceneReady = false;

        // True once KSP has finished (re)loading ContractSystem.Contracts for the
        // current scene. onLevelWasLoadedGUIReady fires BEFORE that — reconciling
        // then catches ContractSystem mid-reload with Contracts momentarily EMPTY,
        // and ReconcileOffers force-creates a duplicate of every owed contract
        // (re-spawning rescue Kerbals). Gate all reconciling on this instead.
        private volatile bool _contractsLoaded = false;

        // Frames the scene has been stable while still waiting for contracts to be
        // marked loaded. Drives the self-healing fallback below when KSP's
        // onContractsLoaded event doesn't fire. Reset on every scene change.
        private int _contractsReadyFrames = 0;

        private void OnLevelLoadedGUIReady(GameScenes scene)
        {
            _sceneReady = true;
            // Request a reconcile; Update() runs it once the contract system is
            // also loaded (it gates on _contractsLoaded there).
            if (session != null)
            {
                MarkContractsDirty(ReconcileTrigger.SceneReady);
                // CareerHackManager.OnLevelLoaded fires on this SAME event and
                // re-asserts building BASELINES via SetApGrantedLevel, which
                // overwrites the effective level and drops any "Progressive
                // <Facility>" increment already applied — a Tracking Station
                // silently falling back to no patched conics is the visible
                // symptom. Flag a reset so the next Update() re-walks items and
                // re-adds those increments on top of the freshly-set baselines:
                // the SAME ordered rebuild (ResetProgressiveState + ProcessAllItems)
                // that the connect / VAB-entry paths already rely on, which is why
                // a reconnect / re-award fixes the stuck facility today.
                _needsReset = true;
            }
        }

        private void OnContractsLoaded()
        {
            // KSP has repopulated ContractSystem.Contracts — now it's safe to
            // offer/accept without racing the empty reload window.
            _contractsLoaded = true;
            if (session != null)
                MarkContractsDirty(ReconcileTrigger.ContractsLoaded);
        }

        private void Update()
        {
            if (_slotDataError != null)
            {
                string msg = $"AP: Disconnected — {_slotDataError}";
                ScreenMessages.PostScreenMessage(msg, 10f, ScreenMessageStyle.UPPER_CENTER);
                Debug.LogError($"[KSP-AP] {msg}");
                _slotDataError = null;
            }

            // Drain background-queued toasts (disconnect, reconnect, etc.).
            lock (_toastLock)
            {
                while (_userToastQueue.Count > 0)
                {
                    string toast = _userToastQueue.Dequeue();
                    ScreenMessages.PostScreenMessage(toast, 6f, ScreenMessageStyle.UPPER_CENTER);
                }
            }

            // Apply a queued incoming DeathLink on the main thread.
            DrainPendingDeath();

            // Drain pending bridge invocation set by HandleConnect on
            // a worker thread. Materialiser ultimately clones Unity
            // GameObjects, which must happen on the main thread.
            PendingStartingBody bridge = System.Threading.Interlocked.Exchange(
                ref _pendingBridge, null);
            if (bridge != null && !string.IsNullOrEmpty(bridge.Name) && bridge.Name != "Kerbin")
            {
                IStartingBodyHandler bodyHandler = StartingBodyBridge.Current;
                if (bodyHandler != null)
                {
                    bodyHandler.OnStartingBodyResolved(
                        bridge.Name, bridge.Lat, bridge.Lon,
                        bridge.TerrainAlt, bridge.SkipMapDecal);
                }
                else
                {
                    EnqueueUserToast(
                        $"AP: starting_body={bridge.Name} but Kerbal Konstructs not " +
                        "installed — alien start disabled. Install KK and reconnect.");
                    Debug.LogError(
                        $"[KSP-AP] starting_body={bridge.Name} requires KK; " +
                        "no IStartingBodyHandler registered (KSPArchipelago.KSC.dll " +
                        "absent or failed to load — typically KerbalKonstructs.dll missing).");
                }
            }

            // Drain pending career-hack directives parsed by HandleConnect on
            // a worker thread. CareerHackManager touches Funding/Reputation/
            // facility singletons, which must run on the main thread.
            CareerDirectives careerDirectives = System.Threading.Interlocked.Exchange(
                ref _pendingCareerDirectives, null);
            if (careerDirectives != null)
                CareerHackManager.Instance?.Apply(careerDirectives);

            // One-shot stock-values snapshot. Always fires once per connect,
            // even for Kerbin-home seeds, so Reset() can restore stock
            // science values on disconnect.
            if (_stockSnapshotPending)
            {
                if (ScienceScaling.PrepareSnapshot())
                {
                    lock (sessionLock) { _stockSnapshotPending = false; }
                }
            }

            // Drain pending science values set by HandleConnect on a
            // worker thread. ScienceScaling touches FlightGlobals.Bodies
            // and must run on the main thread. ValidatePayload returns
            // "deferred" if bodies aren't loaded yet — flag stays set
            // and we retry next frame. Any other non-null return is a
            // hard error: disconnect with toast.
            if (_scienceValuesPending)
            {
                Dictionary<string, Dictionary<string, float>> values;
                lock (sessionLock) { values = _pendingScienceValues; }

                if (values == null)
                {
                    // No payload — Kerbin home, reset to stock (no-op
                    // unless a previous connect applied scaling).
                    ScienceScaling.Reset();
                    lock (sessionLock) { _scienceValuesPending = false; }
                }
                else
                {
                    string err = ScienceScaling.ValidatePayload(values);
                    if (err == "deferred")
                    {
                        // FlightGlobals not ready — retry next frame.
                    }
                    else if (err != null)
                    {
                        Debug.LogError($"[KSP-AP] science_values invalid: {err}");
                        _slotDataError = $"science_values invalid: {err}";
                        HandleDisconnect();
                        lock (sessionLock) { _scienceValuesPending = false; }
                    }
                    else
                    {
                        ScienceScaling.Apply(values);
                        lock (sessionLock) { _scienceValuesPending = false; }
                    }
                }
            }

            // Disconnect requested a body-visibility reset off-thread — do the
            // Unity work here on the main thread.
            if (_bodyResetPending)
            {
                _bodyResetPending = false;
                BodyUnlockManager.ResetAll();
            }

            // Apply persisted fly-there reveals once the async DataStorage read
            // has returned. SeedFlownReveals reveals any that were already hidden
            // by an earlier hide pass, so this is order-independent.
            if (_flownRevealsReady)
            {
                List<string> flown;
                lock (sessionLock) { flown = _pendingFlownReveals; _flownRevealsReady = false; }
                BodyUnlockManager.SeedFlownReveals(flown);
            }

            if (_needsReset)
            {
                _needsReset = false;
                // OnLoad creates a new HashSet; re-share the reference with the tracker.
                if (missionTracker != null && ApScenarioModule.Instance != null)
                    missionTracker.SetPendingNames(ApScenarioModule.Instance.PendingLocationNames);
                KSPArchipelagoPartsManager.ScrubTechTree();
                KSPArchipelagoPartsManager.ClearAllExperimentalParts();
                ResetProgressiveState();
                // Hide the configured bodies BEFORE replaying items, so received
                // Discover items re-open their bodies in ProcessAllItems.
                ApplyBodyVisibility();
                ProcessAllItems();
                // All received items (incl. contract items) are now processed —
                // offer/heal owed contracts once the scene + ContractSystem are ready.
                MarkContractsDirty(ReconcileTrigger.Connect);
                KSPArchipelagoPartsManager.ReconcileApScience(session);
            }
            else if (session != null)
            {
                ProcessNewItems();
            }

            // Goal-mode threshold watcher: report any reached "Contract
            // Threshold N" locations so the server releases the goal item(s).
            // Drained here (not in the report callback) so its own ReportLocation
            // calls can't re-enter. It may re-dirty itself via those reports;
            // that converges in a couple of frames.
            if (_thresholdsDirty && session != null)
            {
                _thresholdsDirty = false;
                Contracts.ContractThresholdWatcher.Evaluate(missionTracker);
            }

            // Self-healing reconcile gate. _contractsLoaded is normally set by KSP's
            // onContractsLoaded event, but that event is intermittent — some scene
            // loads never fire it, which wedges the gate shut and silently stops
            // offering contracts for the whole session. Fallback: once the scene has
            // been stable a moment AND KSP's ContractSystem is populated, the empty-
            // reload window is past, so mark contracts loaded ourselves.
            if (session != null && _sceneReady && !_contractsLoaded
                && ++_contractsReadyFrames > 120
                && Contracts.ApContractManager.ContractsReady())
            {
                _contractsLoaded = true;
                Debug.Log("[KSP-AP] contracts gate opened by fallback "
                        + "(onContractsLoaded did not fire this scene)");
                MarkContractsDirty(ReconcileTrigger.GateFallback);
            }

            // Event-driven contract reconcile. Runs ReconcileOffers (a contract-
            // list walk + FindObjectOfType) only when something actually changed
            // — scene/contracts ready, a contract item arrived, the gate fell
            // back, or the coarse backstop below. Replaces the old every-30-
            // frames poll that hitched the main thread ~2x/second all session.
            if (session != null && _sceneReady && _contractsLoaded)
            {
                // Backstop: if no event fired for 30s, reconcile once anyway.
                // Logged (with trigger) so a test cycle reveals whether it ever
                // finds work — which would mean we missed an event worth hooking.
                if (!_contractsDirty
                    && Time.unscaledTime - _lastReconcileTime > 30f)
                    MarkContractsDirty(ReconcileTrigger.Backstop);

                if (_contractsDirty)
                {
                    _contractsDirty = false;
                    _lastReconcileTime = Time.unscaledTime;
                    Debug.Log($"[KSP-AP] ReconcileOffers (trigger={_reconcileTrigger})");
                    Contracts.ApContractManager.ReconcileOffers();
                }
            }

            missionTracker?.Update();

            // Server fired-trap set arrived — merge on the main thread before
            // any Drain can fire (IsLoaded stays false until this lands).
            // Indexes only the local file knew get pushed back up.
            foreach (ReceivedIndexStore store in ReceivedIndexStore.All)
            {
                int[] pending = store.TakePendingServerIndices();
                if (pending == null) continue;
                int[] localOnly = store.MergeServer(pending);
                if (localOnly.Length > 0)
                    PushStoreIndices(store, localOnly);
            }

            // Push buff totals into prefabs + loaded vessels. Deferred to the
            // main thread here because item receipt can land on a ThreadPool
            // worker (HandleConnect), and PartLoader/FlightGlobals are not
            // thread-safe. Idempotent, so a spurious extra Apply is harmless.
            if (Buffs.BuffManager.NeedsApply)
                Buffs.BuffManager.Apply();

            // Fire owed traps (flight scene only; gates + pacing inside).
            Traps.TrapManager.Drain(this);

            if (!_goalSent && session != null && missionTracker != null && missionTracker.IsGoalMet())
            {
                _goalSent = true;
                session.SetGoalAchieved();
                NotifyVictory();
            }
        }

        private void NotifyVictory()
        {
            string toast = $"AP: VICTORY! {_goalDisplayName} Complete!";
            ScreenMessages.PostScreenMessage(toast, 15f, ScreenMessageStyle.UPPER_CENTER);
            Debug.Log($"[KSP-AP] {toast}");

            if (MessageSystem.Instance != null)
            {
                var msg = new MessageSystem.Message(
                    toast,
                    $"You have completed the {_goalDisplayName} goal.\n"
                    + "Your victory has been sent to the Archipelago server.",
                    MessageSystemButton.MessageButtonColor.GREEN,
                    MessageSystemButton.ButtonIcons.MESSAGE);
                MessageSystem.Instance.AddMessage(msg);
            }
        }

        /// <summary>
        /// Zero all progressive state so ProcessAllItems can rebuild from scratch.
        /// </summary>
        public void ResetProgressiveState()
        {
            RDLevel = 0;
            _progressiveCounts = new Dictionary<string, int>();
            // Buff counts are progressive state too — same double-count
            // hazard described below if they aren't zeroed before the re-walk.
            Buffs.BuffManager.ResetCounts();
            // Consumable charges: only the RECEIVED side is rebuilt here. The
            // spent side lives in ReceivedIndexStore.SpentCharges and must
            // survive, or a reconnect would refund every spent charge.
            Buffs.ChargeManager.ResetReceived();
            // Career-mode facility levels are progressive state too. If we
            // don't zero them here, ProcessAllItems re-walking
            // AllItemsReceived (called on _needsReset from VAB entry/exit
            // via onGameStateLoad) will IncrementApGrantedLevel for every
            // already-awarded item, double-counting each one. Symptom:
            // visiting the VAB after receiving a Progressive Launch Pad
            // bumps the pad to level 3 on the way back to SC.
            CareerUpgradesManager.Instance?.ResetApGrantedLevels();
        }

        /// <summary>
        /// Apply the seed's hidden-bodies config (parsed from slot_data). Runs on
        /// the main thread inside the _needsReset rebuild, before ProcessAllItems,
        /// so received Discover items re-reveal their bodies. A null map means the
        /// feature is off — reveal anything a prior connect may have hidden.
        /// </summary>
        private void ApplyBodyVisibility()
        {
            Dictionary<string, string> map;
            bool allow, deep;
            lock (sessionLock)
            {
                map = _pendingBodyItemMap;
                allow = _pendingAllowUndiscovered;
                deep = _pendingDeepHide;
            }
            // No body config in slot_data => feature off; leave the current state
            // (nothing to hide). A seed with the feature enabled always sends
            // body_item_map (empty for all-visible), taking the branch below.
            if (map == null) return;
            BodyUnlockManager.DeepHide = deep;
            BodyUnlockManager.Configure(map, allow);
            BodyUnlockManager.ApplyConfiguredHidden();
        }

        /// <summary>
        /// Iterate ALL of AllItemsReceived and rebuild state from scratch.
        /// Items already in AwardedItemIndices get silent processing (no toast/science).
        /// New items get full processing and are added to the persisted set.
        /// </summary>
        public void ProcessAllItems()
        {
            if (session == null) return;

            // ApScenarioModule is not registered for the EDITOR scene. KSP fires
            // onGameStateLoad when entering the VAB, which triggers this path with
            // Instance == null. Do NOT bail — GiveItem must still run so parts are
            // re-added after ClearAllExperimentalParts. Skip science tracking only
            // when the scenario is unavailable to avoid double-awarding on the next
            // call when it is available.
            var scenario = ApScenarioModule.Instance;
            var awarded = scenario?.AwardedItemIndices;

            var allItems = session.Items.AllItemsReceived;
            int count = allItems.Count;

            Debug.Log($"[KSP-AP] ProcessAllItems: {count} items, {awarded?.Count ?? 0} previously awarded");

            for (int i = 0; i < count; i++)
            {
                var item = allItems[i];
                if (item.ItemName == null) continue;

                bool alreadyAwarded = awarded != null && awarded.Contains(i);
                KSPArchipelagoPartsManager.GiveItem(
                    item.ItemName, item.Player?.Alias, item.LocationName,
                    showToast: !alreadyAwarded, flags: item.Flags);

                // Deliberately OUTSIDE the !alreadyAwarded guard: this rebuilds
                // the full received set that ResetProgressiveState just
                // cleared, so it has to see every copy, not only new ones.
                // Whether a copy was already SPENT is a separate question,
                // answered by ReceivedIndexStore.SpentCharges.
                Buffs.ChargeManager.NoteReceived(i, item.ItemName);

                if (!alreadyAwarded && scenario != null)
                {
                    awarded.Add(i);
                    Traps.TrapManager.NoteReceived(i, item.ItemName, item.Flags);
                    if (KSPArchipelagoPartsManager.TryGetScienceAmount(item.ItemName, out float sciAmt))
                    {
                        if (ResearchAndDevelopment.Instance != null)
                            ResearchAndDevelopment.Instance.AddScience(sciAmt, TransactionReasons.Cheating);
                        scenario.TotalApScienceAwarded += sciAmt;
                    }
                }
            }

            _lastProcessedIndex = count;
            ItemsReceivedCount = count;

            // Counts were rebuilt from scratch — re-write the pad cap into the
            // building (covers initial connect with 0 collected: cap = caps[0]).
            SyncLaunchPadMassCap();

            Debug.Log($"[KSP-AP] ProcessAllItems complete: {awarded?.Count ?? 0} total awarded");
        }

        /// <summary>
        /// Fast incremental path: check for new items beyond what we've already processed.
        /// </summary>
        private void ProcessNewItems()
        {
            if (session == null) return;

            var scenario = ApScenarioModule.Instance;
            if (scenario == null) return;

            var allItems = session.Items.AllItemsReceived;
            var awarded = scenario.AwardedItemIndices;
            int count = allItems.Count;

            if (count <= _lastProcessedIndex) return;

            for (int i = _lastProcessedIndex; i < count; i++)
            {
                var item = allItems[i];
                if (item.ItemName == null) continue;

                bool alreadyAwarded = awarded.Contains(i);
                KSPArchipelagoPartsManager.GiveItem(
                    item.ItemName, item.Player?.Alias, item.LocationName,
                    showToast: !alreadyAwarded, flags: item.Flags);

                // A contract item arrived — request a reconcile so it gets
                // offered/activated without waiting for a scene change.
                if (Contracts.ApContractManager.IsContractItem(item.ItemName))
                    MarkContractsDirty(ReconcileTrigger.ItemsReceived);

                // Bank a consumable charge. Outside the !alreadyAwarded guard
                // for the same reason as the full-replay path: this tracks
                // copies received, not copies newly awarded.
                Buffs.ChargeManager.NoteReceived(i, item.ItemName);

                if (!alreadyAwarded)
                {
                    awarded.Add(i);
                    Traps.TrapManager.NoteReceived(i, item.ItemName, item.Flags);
                    if (KSPArchipelagoPartsManager.TryGetScienceAmount(item.ItemName, out float sciAmt))
                    {
                        if (ResearchAndDevelopment.Instance != null)
                            ResearchAndDevelopment.Instance.AddScience(sciAmt, TransactionReasons.Cheating);
                        scenario.TotalApScienceAwarded += sciAmt;
                    }
                    OnItemReceived?.Invoke(item.ItemName);
                }
                ItemsReceivedCount++;
            }

            _lastProcessedIndex = count;
        }

        // Deferred error: set on background thread, shown by Update() on main thread.
        private volatile string _slotDataError = null;

        /// <summary>Append used-up received-item indexes to the server-side
        /// record (Scope.Slot DataStorage — the authoritative once-ever set).
        /// Works for any ReceivedIndexStore; the store owns its key.</summary>
        public void PushStoreIndex(ReceivedIndexStore store, int itemIndex)
            => PushStoreIndices(store, new[] { itemIndex });

        public void PushStoreIndices(ReceivedIndexStore store, int[] itemIndices)
        {
            try
            {
                if (session != null)
                    session.DataStorage[Scope.Slot, store.ServerKey] += itemIndices;
            }
            catch (Exception ex)
            {
                // Socket died mid-write: the local file still has the index,
                // so the worst case is one extra use on a machine without it.
                Debug.LogWarning(
                    $"[KSP-AP] Push {store.ServerKey}({itemIndices.Length}) failed: {ex.Message}");
            }
        }

        public void HandleConnect(ArchipelagoSession newSession, LoginSuccessful loginData, string slotName)
        {
            Debug.Log("[KSP-AP] Connected to AP server.");
            lock (sessionLock)
            {
                session = newSession;
                var sd = loginData.SlotData;

                // Career is mandatory — contracts and the hacked economy only
                // exist in Career mode. Reject a non-Career save unless the
                // sandbox-testing bypass env var is set. The connect button is
                // only enabled in SpaceCenter, so CurrentGame is reliably set.
                bool allowSandbox = !string.IsNullOrEmpty(
                    Environment.GetEnvironmentVariable("KSP_AP_ALLOW_SANDBOX"));
                if (!allowSandbox
                    && HighLogic.CurrentGame != null
                    && HighLogic.CurrentGame.Mode != Game.Modes.CAREER)
                {
                    _slotDataError = "This AP seed requires a Career save. Start a "
                        + "Career game and reconnect (or set KSP_AP_ALLOW_SANDBOX "
                        + "to test in sandbox).";
                    session = null;
                    ConnectedSlot = null;
                    return;
                }

                // --- Validate required slot_data keys up front ---
                var missing = new List<string>();

                Difficulty = sd.TryGetValue("difficulty", out object diffObj) ? Convert.ToInt32(diffObj) : -1;
                if (Difficulty < 0) missing.Add("difficulty");
                // Optional: DeathLink. Absent on old seeds -> off. Not a required key.
                bool deathLink = sd.TryGetValue("death_link", out object dlObj) && Convert.ToInt32(dlObj) != 0;
                if (sd.TryGetValue("starting_body", out object sbObj))
                    StartingBody = (string)sbObj;

                // KSC site row for an alien starting body: the chosen body's
                // landing coordinate (lat/lon/terrain alt) + map-decal flag.
                // The client no longer carries a per-body table — it
                // materialises the alien KSC from this. Required whenever
                // starting_body isn't Kerbin; Kerbin uses the stock KSC and
                // sends no row.
                bool alienStart = StartingBody != null && StartingBody != "Kerbin";
                double kscLat = 0, kscLon = 0, kscAlt = 0;
                bool kscSkipDecal = false;
                if (sd.TryGetValue("ksc_site", out object kscObj) && kscObj is JObject kscJo)
                {
                    kscLat = kscJo.Value<double>("lat");
                    kscLon = kscJo.Value<double>("lon");
                    kscAlt = kscJo.Value<double>("terrain_alt");
                    kscSkipDecal = kscJo.Value<bool?>("skip_decal") ?? false;
                }
                else if (alienStart)
                {
                    missing.Add("ksc_site");
                }

                // Part packs: HARD-fail if the seed enables an optional pack that
                // isn't available here (its backing content — a KSP expansion today,
                // mods later — isn't installed). Those parts are in the item pool
                // but can never build, so letting the connect through silently
                // bricks the run — reject it (reusing the _slotDataError path, like
                // the KK check below) so the player sees why. Stock is always
                // present; owned-but-disabled is harmless and only logged.
                if (sd.TryGetValue("enabled_part_packs", out object eppObj)
                    && eppObj is JArray eppArr)
                {
                    // pack id -> KSP expansion name (Stock = base game, omitted).
                    var expansionFor = new Dictionary<string, string>
                    {
                        { "MakingHistory", "MakingHistory" },
                        { "BreakingGround", "Serenity" },
                    };
                    var enabledPacks = new HashSet<string>();
                    foreach (var tok in eppArr) enabledPacks.Add((string)tok);
                    var missingPacks = new List<string>();
                    foreach (string pack in enabledPacks)
                    {
                        if (pack == "Stock") continue;
                        if (!expansionFor.TryGetValue(pack, out string expName))
                        {
                            Debug.LogWarning($"[KSP-AP] Seed enables unknown part "
                                + $"pack '{pack}'.");
                        }
                        else if (!Expansions.ExpansionsLoader.IsExpansionInstalled(expName))
                        {
                            missingPacks.Add(pack == expName ? pack : $"{pack} ({expName})");
                        }
                    }
                    if (missingPacks.Count > 0)
                    {
                        _slotDataError = "This seed enables part pack(s) you don't "
                            + "have installed: " + string.Join(", ", missingPacks.ToArray())
                            + ". Install what each one needs (expansion or mod), or "
                            + "regenerate the seed with them removed from the Enabled "
                            + "Part Packs option. Building with these parts would "
                            + "silently break your run.";
                        session = null;
                        ConnectedSlot = null;
                        return;
                    }
                    foreach (var kv in expansionFor)
                    {
                        if (!enabledPacks.Contains(kv.Key)
                            && Expansions.ExpansionsLoader.IsExpansionInstalled(kv.Value))
                        {
                            Debug.Log($"[KSP-AP] Expansion '{kv.Value}' is installed "
                                + $"but pack '{kv.Key}' is disabled in this seed — "
                                + $"its parts stay locked.");
                        }
                    }
                }

                // Hard-fail when the seed wants an alien starting body
                // but KK isn't installed.  Bridge.Current is null only
                // when StartingBodyHandler.Awake's KK probe found KK
                // missing and skipped SetHandler — so a non-Kerbin home
                // plus null Current means there is no materialiser to
                // place the alien KSC.  Letting the connect succeed would
                // leave the player on stock Kerbin pads with Duna-home
                // science scaling and item routing — a broken state.
                // Reject by reusing the existing _slotDataError path:
                // session=null keeps IsConnected false, so ArchipelagoUI
                // keeps the connect panel open and the KSC facility lock
                // engaged until KK is installed and KSP restarted.
                if (StartingBody != null
                    && StartingBody != "Kerbin"
                    && StartingBodyBridge.Current == null)
                {
                    _slotDataError = "starting_body=" + StartingBody
                        + " requires KerbalKonstructs (not installed). "
                        + "Install KerbalKonstructs and restart KSP.";
                    session = null;
                    ConnectedSlot = null;
                    return;
                }

                // Hand the body + its site row to KSPArchipelago.KSC.dll (if
                // installed) via the main-thread drain in Update().
                // HandleConnect runs on a ThreadPool worker, but the bridge
                // ends up calling KK → UnityEngine.Object.Internal_CloneSingle,
                // which segfaults off-thread.
                _pendingBridge = new PendingStartingBody(
                    StartingBody, kscLat, kscLon, kscAlt, kscSkipDecal);

                TechSlotsPerNode = sd.TryGetValue("tech_slots_per_node", out object tsObj)
                    ? Convert.ToInt32(tsObj) : -1;
                if (TechSlotsPerNode < 0) missing.Add("tech_slots_per_node");

                if (!KSPArchipelagoPartsManager.SetSciencePacksFromSlotData(sd))
                    missing.Add("science_packs");

                // Contracts-as-items + hacked career. Two slot_data blocks the
                // dumb client actuates verbatim:
                //   - career:    facility levels + infinite funds/rep +
                //                unlimited contracts (always emitted).
                //   - contracts: the contract manifest (item→spec); empty
                //                array is valid (a seed with no contracts).
                // Career directives are applied on the main thread in Update()
                // (they touch KSP singletons). Parse into a local and only
                // publish to _pendingCareerDirectives on the success path below,
                // so an abort on a later missing key never hacks the economy.
                CareerDirectives careerDirectives = null;
                try
                {
                    if (sd.TryGetValue("career", out object careerObj)
                        && careerObj is JObject careerJo)
                    {
                        careerDirectives = CareerDirectives.Parse(careerJo);
                    }
                    else missing.Add("career");

                    var contractsTok = sd.TryGetValue("contracts", out object cObj)
                        ? cObj as Newtonsoft.Json.Linq.JToken : null;
                    var contractSpecs = Contracts.ContractSlotSpec.ParseAll(contractsTok);
                    // Fail fast on client/server schema drift — reject the
                    // connect rather than silently strand a contract's location.
                    foreach (var spec in contractSpecs)
                        Contracts.Primitives.ContractPrimitiveRegistry.ValidateSpec(spec);
                    Contracts.ApContractManager.SetSpecs(contractSpecs);

                    // Goal-mode (count / progressive_unlock) threshold watcher.
                    // Absent in findable / starting -> no-op.
                    var thresholdsTok = sd.TryGetValue("contract_thresholds", out object tObj)
                        ? tObj as Newtonsoft.Json.Linq.JToken : null;
                    Contracts.ContractThresholdWatcher.Configure(thresholdsTok);

                    // Completed non-goal contracts needed to release the goal
                    // contract item(s); 0 under findable / starting. Read leniently
                    // (absent -> 0): it only drives a status label, so a missing
                    // value must never cost a player access to their save.
                    _contractsRequired = sd.TryGetValue("contracts_required", out object crObj)
                        ? Convert.ToInt32(crObj) : 0;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[KSP-AP] contracts slot_data parse failed: {ex}");
                    _slotDataError = "contracts slot_data parse error: " + ex.Message;
                    session = null;
                    ConnectedSlot = null;
                    return;
                }

                // Optional: home-relative science values. Absent for
                // Kerbin home (server only emits when home != Kerbin).
                // When present, the payload MUST be complete (every
                // body × every situation) — validation happens on the
                // main thread in Update() before Apply.
                _pendingScienceValues = null;
                if (sd.TryGetValue("science_values", out object svObj) && svObj is JObject svDict)
                {
                    _pendingScienceValues = new Dictionary<string, Dictionary<string, float>>();
                    foreach (var bodyEntry in svDict)
                    {
                        if (!(bodyEntry.Value is JObject fieldDict))
                        {
                            _slotDataError = $"science_values entry for '{bodyEntry.Key}' is not a JSON object";
                            session = null;
                            ConnectedSlot = null;
                            return;
                        }
                        var fields = new Dictionary<string, float>();
                        foreach (var f in fieldDict)
                            fields[f.Key] = (float)Convert.ToDouble(f.Value);
                        _pendingScienceValues[bodyEntry.Key] = fields;
                    }
                    Debug.Log($"[KSP-AP] Parsed science_values for {_pendingScienceValues.Count} bodies from slot data");
                }
                _scienceValuesPending = true;
                _stockSnapshotPending = true;

                // Optional: hidden-bodies config. Present only when the
                // BodyVisibilityMode option is enabled; absent ⇒ feature off.
                // body_item_map is { "Discover <Body>": "<bodyName>" }.
                _pendingBodyItemMap = null;
                _pendingAllowUndiscovered = true;
                _pendingDeepHide = true;
                if (sd.TryGetValue("body_item_map", out object bimObj) && bimObj is JObject bimDict)
                {
                    _pendingBodyItemMap = new Dictionary<string, string>();
                    foreach (var e in bimDict)
                        _pendingBodyItemMap[e.Key] = (string)e.Value;
                    _pendingAllowUndiscovered = !sd.TryGetValue("allow_undiscovered_bodies", out object auObj)
                                                || Convert.ToBoolean(auObj);
                    _pendingDeepHide = !sd.TryGetValue("deep_hide", out object dhObj)
                                       || Convert.ToBoolean(dhObj);
                    Debug.Log($"[KSP-AP] Parsed body_item_map: {_pendingBodyItemMap.Count} hidden bodies " +
                              $"(allowUndiscovered={_pendingAllowUndiscovered}, deep={_pendingDeepHide})");
                    // Retrieve persisted fly-there reveals asynchronously (no
                    // blocking under sessionLock); Update() applies them main-thread.
                    _flownRevealsReady = false;
                    BodyDiscoveryStore.LoadAsync(newSession, list =>
                    {
                        lock (sessionLock) { _pendingFlownReveals = list; _flownRevealsReady = true; }
                    });
                }

                NodeBands = new Dictionary<string, int>();
                if (sd.TryGetValue("node_bands", out object bandsObj) && bandsObj is JObject bandsDict)
                {
                    foreach (var kvp in bandsDict)
                        NodeBands[kvp.Key] = (int)kvp.Value;
                }
                else missing.Add("node_bands");

                // Optional: launch-pad mass-cap progression (only present when option enabled).
                if (sd.TryGetValue("progressive_launch_pad_caps", out object padObj) && padObj is JArray padArr)
                {
                    LaunchPadMassCaps = padArr.ToObject<List<float>>();
                }

                if (missing.Count > 0)
                {
                    string error = "Server slot_data missing required keys: " + string.Join(", ", missing);
                    Debug.LogError($"[KSP-AP] {error}");
                    _slotDataError = error;
                    session = null;
                    ConnectedSlot = null;
                    // Clear pending science work — aborting the connect,
                    // don't apply / snapshot from a half-parsed slot_data.
                    _pendingScienceValues = null;
                    _scienceValuesPending = false;
                    _stockSnapshotPending = false;
                    _pendingBodyItemMap = null;
                    return;
                }

                ConnectedSlot = slotName;

                // Minimal drain handler: keeps AP library internal state clean.
                // All real processing happens on the main thread via Update().
                session.Items.ItemReceived += (h) => h.DequeueItem();

                // Share the pending-names set with the tracker so ApScenarioModule
                // sees offline-queued locations when KSP saves.
                if (ApScenarioModule.Instance != null)
                    missionTracker.SetPendingNames(ApScenarioModule.Instance.PendingLocationNames);

                string trackerError = missionTracker.OnConnect(session, Difficulty, TechSlotsPerNode,
                    onLocationReported: () => { LocationsCheckedCount++; _thresholdsDirty = true; },
                    onSendFailed: reason => Console?.NotifyConnectionLost(reason),
                    onDeath: deathLink ? (Action<string>)(cause =>
                    {
                        // Fired from the tracker's crash / crew-death guards. Send is
                        // best-effort: a dropped socket throws and the reconnect logic
                        // recovers, so a lost death broadcast is acceptable.
                        var svc = _deathLink;
                        if (svc == null) return;
                        try
                        {
                            svc.SendDeathLink(new DeathLink(ConnectedSlot, ConnectedSlot + " " + cause));
                            Debug.Log($"[KSP-AP] DeathLink sent (source={ConnectedSlot}, cause={cause}).");
                        }
                        catch (ArchipelagoSocketClosedException) { }
                    }) : null,
                    slotData: sd);
                if (trackerError != null)
                {
                    Debug.LogError($"[KSP-AP] {trackerError}");
                    _slotDataError = trackerError;
                    session = null;
                    ConnectedSlot = null;
                    // Same as the missing-keys path: aborting connect, so
                    // don't apply / snapshot from a partial slot_data.
                    _pendingScienceValues = null;
                    _scienceValuesPending = false;
                    _stockSnapshotPending = false;
                    _pendingBodyItemMap = null;
                    return;
                }

                // Connect is committed past all abort paths — publish the
                // career directives for the main-thread drain in Update().
                _pendingCareerDirectives = careerDirectives;

                // Once-ever records: fired traps, and spent consumable-buff
                // charges. The AP server's DataStorage is the source of truth
                // (survives client reinstalls and the deploy pipeline wiping
                // PluginData — which really happened); the local file is a
                // secondary cache covering uses whose server write was lost to
                // a disconnect. Consumption stays blocked until BOTH are in
                // (store.IsLoaded).
                foreach (ReceivedIndexStore store in ReceivedIndexStore.All)
                {
                    ReceivedIndexStore captured = store;
                    captured.Load(
                        HighLogic.SaveFolder, newSession.RoomState?.Seed, slotName);
                    var element = newSession.DataStorage[Scope.Slot, captured.ServerKey];
                    element.Initialize(new int[0]);
                    element.GetAsync<int[]>().ContinueWith(t =>
                    {
                        // Worker thread — publish for the main-thread drain.
                        if (t.IsFaulted || t.IsCanceled)
                        {
                            Debug.LogWarning($"[KSP-AP] {captured.ServerKey} DataStorage read "
                                + "failed: " + t.Exception?.GetBaseException().Message);
                            return;   // store stays un-synced; nothing may be consumed
                        }
                        // Guard against a stale continuation from a prior session.
                        if (session == newSession)
                            captured.PublishServerIndices(t.Result ?? new int[0]);
                    });
                }

                // DeathLink: create the library service (it manages the "DeathLink"
                // connection tag + Bounce send/receive) and subscribe to incoming
                // deaths. Only when the seed enabled it. Torn down in HandleDisconnect;
                // a reconnect builds a fresh session and re-creates it here.
                if (deathLink)
                {
                    _deathLink = session.CreateDeathLinkService();
                    _deathLink.OnDeathLinkReceived += OnDeathLinkReceived;
                    _deathLink.EnableDeathLink();
                    Debug.Log("[KSP-AP] DeathLink enabled.");
                }

                // Parse goal location sentinels from slot data.
                var goalLocNames = new List<string>();
                if (sd.TryGetValue("goal_locations", out object glObj) && glObj is JArray glArr)
                {
                    foreach (var tok in glArr)
                        goalLocNames.Add((string)tok);
                }
                missionTracker.SetGoalLocations(goalLocNames);
                _goalDisplayName = sd.TryGetValue("goal_display_name", out object gdObj)
                    ? (string)gdObj : "Unknown Goal";
                _goalSent = false;
                // Evaluate thresholds once after connect — catches offline
                // contract completions / reconnects / server-side !collect.
                _thresholdsDirty = true;

                // Restore UI counters from session/tracker state.
                ItemsReceivedCount = newSession.Items.AllItemsReceived.Count;
                LocationsCheckedCount = missionTracker.CheckedCount;

                if (gameLoaded)
                {
                    // Defer to main thread — Unity APIs aren't safe here.
                    _needsReset = true;
                }
            }
        }

        public void HandleDisconnect()
        {
            lock (sessionLock)
            {
                Traps.TrapManager.OnDisconnect();
                missionTracker?.OnDisconnect();
                FindObjectOfType<TechTreeScout>()?.OnDisconnect();
                // Drop the DeathLink service with the session; a reconnect recreates
                // it. Discard any queued incoming death so it can't fire post-reconnect.
                if (_deathLink != null)
                {
                    _deathLink.OnDeathLinkReceived -= OnDeathLinkReceived;
                    _deathLink = null;
                }
                lock (_deathLock) { _hasPendingDeath = false; _pendingDeathCause = null; }
                _deathArmedVesselId = uint.MaxValue;
                _deathArmedDelay = false;
                session = null;
                ConnectedSlot = null;
                // Restore CelestialBody.scienceValues to stock so a
                // disconnect/quit returns to vanilla KSP science economy.
                // ScienceScaling.Reset is a no-op if no snapshot was taken.
                ScienceScaling.Reset();
                // Buff prefab mutation is process-global, not save state — it
                // would bleed into the next save opened this session.
                Buffs.BuffManager.Restore();
                // Reveal any hidden bodies on the main thread (see _bodyResetPending)
                // so a disconnect/quit returns to a fully-visible solar system.
                _bodyResetPending = true;
                _pendingScienceValues = null;
                _scienceValuesPending = false;
                _stockSnapshotPending = false;
                _pendingBodyItemMap = null;
                _pendingCareerDirectives = null;
                // Stop hacking funds/rep/contract-cap; player keeps what they have.
                CareerHackManager.Instance?.Reset();
            }
        }

        // Incoming DeathLink (runs on the socket thread). Coalesce to a single
        // pending kill — ten deaths received while sitting in a menu still apply
        // as one. The main-thread DrainPendingDeath() decides when/how to destroy
        // the craft (Unity APIs aren't safe off the main thread).
        private void OnDeathLinkReceived(DeathLink death)
        {
            lock (_deathLock)
            {
                _pendingDeathCause = !string.IsNullOrEmpty(death.Cause)
                    ? death.Cause
                    : ((death.Source ?? "Someone") + " died");
                _hasPendingDeath = true;
            }
            Debug.Log($"[KSP-AP] DeathLink received from '{death.Source}' (cause: {death.Cause}) — queued.");
        }

        // Apply a queued incoming death on the main thread. Called every frame from
        // Update(). Waits until the player is actually flying a craft, then:
        //   • fresh launch (PRELAUNCH)      -> destroy 30s of mission time in
        //   • switched-to existing craft    -> destroy on the next frame
        // The kill routes through VesselDestruction.Destroy with the pad's 3s RUD
        // countdown, and pre-marks the victim so the resulting crash can't rebroadcast.
        private void DrainPendingDeath()
        {
            bool pending;
            string cause;
            lock (_deathLock) { pending = _hasPendingDeath; cause = _pendingDeathCause; }
            if (!pending) return;

            if (!HighLogic.LoadedSceneIsFlight) { _deathArmedVesselId = uint.MaxValue; return; }
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) { _deathArmedVesselId = uint.MaxValue; return; }

            // Arm once per vessel: decide fresh-launch vs existing craft up front,
            // so a craft that starts PRELAUNCH keeps its 30s window even after it
            // leaves the PRELAUNCH situation.
            if (_deathArmedVesselId != v.persistentId)
            {
                _deathArmedVesselId = v.persistentId;
                _deathArmedDelay = v.situation == Vessel.Situations.PRELAUNCH;
            }

            if (_deathArmedDelay && v.missionTime < 30.0) return;   // fresh launch: wait 30s of mission time

            // Fire. Clear pending first so a death arriving mid-destruction re-arms
            // cleanly against the next craft rather than double-killing this one.
            lock (_deathLock) { _hasPendingDeath = false; _pendingDeathCause = null; }
            _deathArmedVesselId = uint.MaxValue;

            // Pre-mark BEFORE Destroy so the explosion's crash/crew-death events
            // can't broadcast a DeathLink back (anti-loop).
            missionTracker?.SuppressDeathSend(v.persistentId);
            Debug.Log($"[KSP-AP] DeathLink applying kill to '{v.vesselName}' (cause: {cause}).");
            VesselDestruction.Destroy(
                this, v,
                $"<color=red>DEATHLINK:</color> {cause}",
                delay: 3f);
        }

        private void OnGameStateLoad(ConfigNode config)
        {
            lock (sessionLock)
            {
                gameLoaded = true;
                // ApScenarioModule.OnLoad will have restored AwardedItemIndices
                // from the save file before Update() runs the rebuild.
                _needsReset = true;
            }
        }

        private void OnSceneChange(GameScenes scene)
        {
            // Abort any live trap effect before the scene tears down (the
            // pending queue is save-tied state and survives).
            Traps.TrapManager.OnSceneChange();

            // A scene load was requested — the current ContractSystem is about
            // to tear down/reload. Stop reconciling until the new scene's GUI
            // is ready AND contracts have reloaded, so we never add a contract
            // into a half-loaded system (the empty-reload-window duplicate bug).
            _sceneReady = false;
            _contractsLoaded = false;
            _contractsReadyFrames = 0;

            if (scene == GameScenes.MAINMENU && IsConnected)
            {
                Debug.Log("[KSP-AP] Returning to main menu — disconnecting.");
                Console.Disconnect();
            }
        }

        public void OnDestroy()
        {
            GameEvents.onVesselGoOffRails.Remove(Buffs.BuffManager.OnVesselGoOffRails);
            GameEvents.onGameStateLoad.Remove(new EventData<ConfigNode>.OnEvent(OnGameStateLoad));
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneChange);
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnLevelLoadedGUIReady);
            GameEvents.Contract.onContractsLoaded.Remove(OnContractsLoaded);
            GameEvents.Contract.onCancelled.Remove(OnContractCancelledOrDeclined);
            GameEvents.Contract.onDeclined.Remove(OnContractCancelledOrDeclined);
            missionTracker?.Destroy();
        }
    }
}
