using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

using Newtonsoft.Json.Linq;
using UnityEngine;
using KSP.UI.Screens;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Models;
using Archipelago.MultiClient.Net.Helpers;


namespace KSPArchipelago
{
    /// Item metadata passed from network thread to main thread.
    public struct ReceivedItem
    {
        public string ItemName;
        public string SenderName;
        public string LocationName;
    }

    public static class KSPArchipelagoPartsManager
    {
        private static readonly Dictionary<string, float> SciencePackAmounts = new Dictionary<string, float>
        {
            { "Science Pack 1",   1f },
            { "Science Pack 5",   5f },
            { "Science Pack 10",  10f },
            { "Science Pack 25",  25f },
            { "Science Pack 50",  50f },
            { "Science Pack 100", 100f },
            { "Science Pack 250", 250f },
        };

        public static void GiveItem(ReceivedItem received)
        {
            string itemName = received.ItemName;
            string toastText;

            // Science packs: add science points directly.
            if (SciencePackAmounts.TryGetValue(itemName, out float amount))
            {
                if (ResearchAndDevelopment.Instance != null)
                    ResearchAndDevelopment.Instance.AddScience(amount, TransactionReasons.Cheating);
                if (ApScenarioModule.Instance != null)
                    ApScenarioModule.Instance.TotalApScienceAwarded += amount;
                toastText = $"AP: Received {itemName} (+{amount} science)";
                ScreenMessages.PostScreenMessage(toastText, 4f, ScreenMessageStyle.UPPER_CENTER);
                PostToMessageSystem(received, toastText);
                return;
            }

            var mod = UnityEngine.Object.FindObjectOfType<KSPArchipelagoMod>();

            // Progressive R&D: upgrade the tech tree band limit.
            if (itemName == "Progressive R&D")
            {
                if (mod != null) mod.IncrementRDLevel();
                toastText = $"AP: R&D Facility Upgraded to Level {mod?.RDLevel ?? 0}!";
                ScreenMessages.PostScreenMessage(toastText, 4f, ScreenMessageStyle.UPPER_CENTER);
                PostToMessageSystem(received, toastText);
                return;
            }

            // Progressive part items: unlock 1 random part from the new tier,
            // plus any individually-received parts from that tier.
            if (mod?.ProgressiveTiers != null
                && mod.ProgressiveTiers.TryGetValue(itemName, out var tierMap))
            {
                int newLevel = mod.IncrementProgressiveCount(itemName);
                int unlocked = mod.UnlockProgressiveTier(itemName, newLevel);
                toastText = $"AP: {itemName} Tier {newLevel} ({unlocked} parts unlocked)";
                Debug.Log($"[KSP-AP] {itemName} → tier {newLevel}, {unlocked} parts");
                ScreenMessages.PostScreenMessage(toastText, 4f, ScreenMessageStyle.UPPER_CENTER);
                PostToMessageSystem(received, toastText);
                return;
            }

            // Individual part items: unlock only if the progressive tier gate
            // is satisfied (or if the part is not in any progressive chain).
            AvailablePart part = PartLoader.getPartInfoByName(itemName);
            if (part != null)
            {
                if (mod != null && mod.IsPartTierLocked(itemName))
                {
                    // Part is in a progressive chain but tier not yet unlocked.
                    // Track receipt; will unlock when the tier arrives.
                    mod.TrackReceivedPart(itemName);
                    Debug.Log($"[KSP-AP] Part '{itemName}' received but tier-locked");
                    toastText = $"AP: {part.title} (tier locked)";
                }
                else
                {
                    if (mod != null) mod.TrackReceivedPart(itemName);
                    ResearchAndDevelopment.AddExperimentalPart(part);
                    Debug.Log($"[KSP-AP] Unlocked part '{itemName}' ({part.title})");
                    toastText = $"AP: Unlocked {part.title}";
                }
                ScreenMessages.PostScreenMessage(toastText, 4f, ScreenMessageStyle.UPPER_CENTER);
                PostToMessageSystem(received, toastText);
            }
            else
            {
                Debug.LogWarning($"[KSP-AP] GiveItem: part not found '{itemName}'");
            }
        }

        private static void PostToMessageSystem(ReceivedItem received, string title)
        {
            if (MessageSystem.Instance == null) return;

            string body = title;
            if (received.SenderName != null)
                body += $"\nFrom: {received.SenderName}";
            if (received.LocationName != null)
                body += $"\nLocation: {received.LocationName}";

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

        public static void SetExperimentalParts(
            ArchipelagoSession session,
            Dictionary<string, Dictionary<int, List<string>>> progressiveTiers,
            Dictionary<string, Dictionary<int, string>> progressiveRepresentatives)
        {
            if (session == null) return;

            // Build part-to-progressive reverse lookup for tier gating.
            var partProgName = new Dictionary<string, string>();
            var partProgTier = new Dictionary<string, int>();
            if (progressiveTiers != null)
            {
                foreach (var prog in progressiveTiers)
                    foreach (var tier in prog.Value)
                        foreach (string p in tier.Value)
                        {
                            partProgName[p] = prog.Key;
                            partProgTier[p] = tier.Key;
                        }
            }

            // Count progressive items and track individually received parts.
            var progressiveCounts = new Dictionary<string, int>();
            var receivedParts = new HashSet<string>();
            foreach (var item in session.Items.AllItemsReceived)
            {
                if (item.ItemName == null) continue;
                if (progressiveTiers != null && progressiveTiers.ContainsKey(item.ItemName))
                {
                    progressiveCounts.TryGetValue(item.ItemName, out int c);
                    progressiveCounts[item.ItemName] = c + 1;
                }
                else
                {
                    receivedParts.Add(item.ItemName);
                }
            }

            // Determine which parts to unlock:
            var partsToUnlock = new HashSet<string>();

            // 1. For each progressive tier unlocked, use the server-selected representative.
            if (progressiveTiers != null && progressiveRepresentatives != null)
            {
                foreach (var kvp in progressiveCounts)
                {
                    if (!progressiveRepresentatives.TryGetValue(kvp.Key, out var repMap))
                        continue;
                    for (int t = 1; t <= kvp.Value; t++)
                    {
                        if (repMap.TryGetValue(t, out string rep)
                            && PartLoader.getPartInfoByName(rep) != null)
                        {
                            partsToUnlock.Add(rep);
                        }
                    }
                }
            }

            // 2. Individually received parts: unlock if their progressive tier is unlocked.
            foreach (string partName in receivedParts)
            {
                if (partProgName.TryGetValue(partName, out string progName))
                {
                    progressiveCounts.TryGetValue(progName, out int count);
                    if (count >= partProgTier[partName])
                        partsToUnlock.Add(partName);
                    // else: tier locked, skip
                }
                else
                {
                    // Not in any progressive chain — unlock directly.
                    partsToUnlock.Add(partName);
                }
            }

            int unlocked = 0;
            foreach (AvailablePart part in PartLoader.LoadedPartsList)
            {
                if (partsToUnlock.Contains(part.name))
                {
                    ResearchAndDevelopment.AddExperimentalPart(part);
                    unlocked++;
                }
                else
                {
                    ResearchAndDevelopment.RemoveExperimentalPart(part);
                }
            }
            Debug.Log($"[KSP-AP] SetExperimentalParts: {unlocked} unlocked ({progressiveCounts.Count} progressive items, {receivedParts.Count} individual)");
        }

        public static void ResetParts(
            ArchipelagoSession session,
            Dictionary<string, Dictionary<int, List<string>>> progressiveTiers,
            Dictionary<string, Dictionary<int, string>> progressiveRepresentatives)
        {
            ScrubTechTree();
            SetExperimentalParts(session, progressiveTiers, progressiveRepresentatives);
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

            float expectedScience = 0f;
            foreach (var item in session.Items.AllItemsReceived)
            {
                if (item.ItemName != null && SciencePackAmounts.TryGetValue(item.ItemName, out float amount))
                    expectedScience += amount;
            }

            float alreadyAwarded = ApScenarioModule.Instance?.TotalApScienceAwarded ?? 0f;
            float delta = expectedScience - alreadyAwarded;

            if (delta > 0.01f)
            {
                ResearchAndDevelopment.Instance.AddScience(delta, TransactionReasons.Cheating);
                if (ApScenarioModule.Instance != null)
                    ApScenarioModule.Instance.TotalApScienceAwarded = expectedScience;
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
        private bool gameLoaded = false;
        public Archipelago.APConsole Console { get; private set; }

        // Slot data from AP server.
        public int Goal { get; private set; }
        public int Difficulty { get; private set; }
        public int TechSlotsPerNode { get; private set; }

        // Progressive R&D: current band level (0 = base, up to 3).
        public int RDLevel { get; private set; }
        // Tech node ID → required R&D band (from slot data).
        public Dictionary<string, int> NodeBands { get; private set; }

        // Progressive part tiers from slot_data: item name → tier → part cfg_names.
        public Dictionary<string, Dictionary<int, List<string>>> ProgressiveTiers { get; private set; }
        // Server-selected representative per progressive tier: item name → tier → cfg_name.
        public Dictionary<string, Dictionary<int, string>> ProgressiveRepresentatives { get; private set; }
        // Reverse lookup: part cfg_name → progressive item name.
        private Dictionary<string, string> _partProgName = new Dictionary<string, string>();
        // Reverse lookup: part cfg_name → tier number within its progressive chain.
        private Dictionary<string, int> _partProgTier = new Dictionary<string, int>();
        // Running count of progressive item copies received (reset on connect).
        private Dictionary<string, int> _progressiveCounts = new Dictionary<string, int>();
        // Individual parts received (for tier-gating: unlock when tier arrives).
        private HashSet<string> _receivedParts = new HashSet<string>();

        // Expose connection state for the UI.
        public bool IsConnected => session != null;
        internal ArchipelagoSession Session => session;
        public int ItemsReceivedCount { get; private set; }
        public int LocationsCheckedCount { get; private set; }
        public string ConnectedSlot { get; private set; }

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

        /// <summary>
        /// Unlock the server-selected representative for the given tier, plus any
        /// individually-received parts from that tier. Returns total parts unlocked.
        /// </summary>
        public int UnlockProgressiveTier(string progName, int tier)
        {
            if (ProgressiveTiers == null) return 0;
            if (!ProgressiveTiers.TryGetValue(progName, out var tierMap)) return 0;
            if (!tierMap.TryGetValue(tier, out var partNames)) return 0;

            // Use server-selected representative for this tier.
            string chosen = null;
            if (ProgressiveRepresentatives != null
                && ProgressiveRepresentatives.TryGetValue(progName, out var repMap)
                && repMap.TryGetValue(tier, out string rep))
            {
                chosen = rep;
            }

            int unlocked = 0;
            // Unlock the representative.
            if (chosen != null)
            {
                AvailablePart chosenPart = PartLoader.getPartInfoByName(chosen);
                if (chosenPart != null)
                {
                    ResearchAndDevelopment.AddExperimentalPart(chosenPart);
                    unlocked++;
                }
            }

            // Also unlock any individually-received parts from this tier.
            foreach (string p in partNames)
            {
                if (p == chosen) continue;
                if (PartLoader.getPartInfoByName(p) == null) continue;
                if (_receivedParts.Contains(p))
                {
                    AvailablePart ap = PartLoader.getPartInfoByName(p);
                    if (ap != null)
                    {
                        ResearchAndDevelopment.AddExperimentalPart(ap);
                        unlocked++;
                    }
                }
            }
            return unlocked;
        }

        /// <summary>
        /// Check if a part is in a progressive chain whose tier is not yet unlocked.
        /// </summary>
        public bool IsPartTierLocked(string partName)
        {
            if (!_partProgName.TryGetValue(partName, out string progName))
                return false;  // Not in any progressive chain — always unlockable.
            int requiredTier = _partProgTier[partName];
            _progressiveCounts.TryGetValue(progName, out int count);
            return count < requiredTier;
        }

        public void TrackReceivedPart(string partName)
        {
            _receivedParts.Add(partName);
        }

        // Internal notification callback for UI.
        public event Action<string> OnItemReceived;

        // Items received on the network thread, processed on the main thread.
        private readonly ConcurrentQueue<ReceivedItem> pendingItems = new ConcurrentQueue<ReceivedItem>();

        // Backlog suppression: items in AllItemsReceived at connect time are
        // handled in bulk by ResetParts + ReconcileApScience, so we skip
        // their individual toasts/messages.
        private int _backlogSize = 0;
        private int _backlogCounter = 0;

        // Deferred reset: HandleConnect runs on a background thread but
        // ResetParts / ReconcileApScience call Unity APIs that must run on
        // the main thread. This flag tells Update() to run them before
        // processing any pending items.
        private volatile bool _needsReset = false;

        private void Start()
        {
            DontDestroyOnLoad(this);

            WinConsole.Initialize();
            Debug.Log("[KSP-AP] Mod started.");

            missionTracker = new MissionTracker();

            Console = new Archipelago.APConsole(this);
            ThreadStart work = new ThreadStart(Console.Run);
            Thread thread = new Thread(work) { IsBackground = true };
            thread.Start();

            GameEvents.onGameStateLoad.Add(new EventData<ConfigNode>.OnEvent(OnGameStateLoad));
            GameEvents.onGameSceneLoadRequested.Add(OnSceneChange);
        }

        private void Update()
        {
            // Deferred reset from HandleConnect (runs on bg thread).
            // Must run BEFORE draining pendingItems so that ScrubTechTree
            // + SetExperimentalParts complete before GiveItem adds back
            // any items that were in the backlog race window.
            if (_needsReset)
            {
                _needsReset = false;
                KSPArchipelagoPartsManager.ResetParts(session, ProgressiveTiers, ProgressiveRepresentatives);
                KSPArchipelagoPartsManager.ReconcileApScience(session);
            }

            // Process items queued by the network thread on the main thread,
            // where Unity API calls are safe.
            while (pendingItems.TryDequeue(out ReceivedItem received))
            {
                KSPArchipelagoPartsManager.GiveItem(received);
                ItemsReceivedCount++;
                OnItemReceived?.Invoke(received.ItemName);
            }

            missionTracker?.Update();
        }

        public void HandleItemReceived(ReceivedItemsHelper receivedItemsHelper)
        {
            var item = receivedItemsHelper.DequeueItem();
            string itemName = item.ItemName;
            if (itemName == null)
            {
                Debug.LogWarning($"[KSP-AP] Received item with unknown ID: {item.ItemId}");
                return;
            }

            // Backlog items are already handled by ResetParts + ReconcileApScience.
            // Skip enqueueing to avoid duplicate toasts, messages, and science.
            _backlogCounter++;
            if (_backlogCounter <= _backlogSize)
                return;

            pendingItems.Enqueue(new ReceivedItem
            {
                ItemName = itemName,
                SenderName = item.Player?.Alias,
                LocationName = item.LocationName,
            });
        }

        public void HandleConnect(ArchipelagoSession newSession, LoginSuccessful loginData, string slotName)
        {
            Debug.Log("[KSP-AP] Connected to AP server.");
            lock (sessionLock)
            {
                session = newSession;

                // Parse slot data.
                Goal = loginData.SlotData.TryGetValue("goal", out object goalObj)
                    ? Convert.ToInt32(goalObj) : 0;
                Difficulty = loginData.SlotData.TryGetValue("difficulty", out object diffObj)
                    ? Convert.ToInt32(diffObj) : 1;
                TechSlotsPerNode = loginData.SlotData.TryGetValue("tech_slots_per_node", out object tsObj)
                    ? Convert.ToInt32(tsObj) : 4;
                ConnectedSlot = slotName;

                // Parse node_bands from slot data.
                NodeBands = new Dictionary<string, int>();
                if (loginData.SlotData.TryGetValue("node_bands", out object bandsObj)
                    && bandsObj is JObject bandsDict)
                {
                    foreach (var kvp in bandsDict)
                        NodeBands[kvp.Key] = (int)kvp.Value;
                }

                // Parse progressive_tiers from slot data.
                ProgressiveTiers = new Dictionary<string, Dictionary<int, List<string>>>();
                if (loginData.SlotData.TryGetValue("progressive_tiers", out object ptObj)
                    && ptObj is JObject ptDict)
                {
                    foreach (var prog in ptDict)
                    {
                        var tierMap = new Dictionary<int, List<string>>();
                        if (prog.Value is JObject tiers)
                        {
                            foreach (var tier in tiers)
                                tierMap[int.Parse(tier.Key)] = tier.Value.ToObject<List<string>>();
                        }
                        ProgressiveTiers[prog.Key] = tierMap;
                    }
                    Debug.Log($"[KSP-AP] Parsed {ProgressiveTiers.Count} progressive tier categories from slot data");
                }

                // Parse progressive_representatives from slot data.
                ProgressiveRepresentatives = new Dictionary<string, Dictionary<int, string>>();
                if (loginData.SlotData.TryGetValue("progressive_representatives", out object prObj)
                    && prObj is JObject prDict)
                {
                    foreach (var prog in prDict)
                    {
                        var repMap = new Dictionary<int, string>();
                        if (prog.Value is JObject reps)
                        {
                            foreach (var rep in reps)
                                repMap[int.Parse(rep.Key)] = (string)rep.Value;
                        }
                        ProgressiveRepresentatives[prog.Key] = repMap;
                    }
                    Debug.Log($"[KSP-AP] Parsed {ProgressiveRepresentatives.Count} progressive representative categories from slot data");
                }

                // Build reverse lookup: part cfg_name → progressive name + tier.
                _partProgName = new Dictionary<string, string>();
                _partProgTier = new Dictionary<string, int>();
                foreach (var prog in ProgressiveTiers)
                {
                    foreach (var tier in prog.Value)
                    {
                        foreach (string partName in tier.Value)
                        {
                            _partProgName[partName] = prog.Key;
                            _partProgTier[partName] = tier.Key;
                        }
                    }
                }

                // Restore R&D level, progressive counts, and received parts from backlog.
                RDLevel = 0;
                _progressiveCounts = new Dictionary<string, int>();
                _receivedParts = new HashSet<string>();
                foreach (var item in newSession.Items.AllItemsReceived)
                {
                    if (item.ItemName == null) continue;
                    if (item.ItemName == "Progressive R&D")
                        RDLevel++;
                    else if (ProgressiveTiers.ContainsKey(item.ItemName))
                    {
                        _progressiveCounts.TryGetValue(item.ItemName, out int c);
                        _progressiveCounts[item.ItemName] = c + 1;
                    }
                    else if (PartLoader.getPartInfoByName(item.ItemName) != null)
                    {
                        _receivedParts.Add(item.ItemName);
                    }
                }
                Debug.Log($"[KSP-AP] Restored R&D level={RDLevel}, {_progressiveCounts.Count} progressive items, {_receivedParts.Count} individual parts from {newSession.Items.AllItemsReceived.Count} received items");

                // Set backlog size before subscribing so the callback can
                // skip items that are already handled in bulk below.
                _backlogSize = newSession.Items.AllItemsReceived.Count;
                _backlogCounter = 0;

                session.Items.ItemReceived += HandleItemReceived;

                missionTracker.Initialize(session, Difficulty, TechSlotsPerNode, () => LocationsCheckedCount++);

                // Restore UI counters from session/tracker state.
                ItemsReceivedCount = newSession.Items.AllItemsReceived.Count;
                LocationsCheckedCount = missionTracker.CheckedCount;

                if (gameLoaded)
                {
                    // Defer to main thread — Unity APIs aren't safe here
                    // and AllItemsReceived may still be populating.
                    _needsReset = true;
                }
            }
        }

        public void HandleDisconnect()
        {
            lock (sessionLock)
            {
                missionTracker?.Shutdown();
                FindObjectOfType<TechTreeScout>()?.OnDisconnect();
                session = null;
                ConnectedSlot = null;
            }
        }

        private void OnGameStateLoad(ConfigNode config)
        {
            lock (sessionLock)
            {
                gameLoaded = true;
                KSPArchipelagoPartsManager.ResetParts(session, ProgressiveTiers, ProgressiveRepresentatives);
                KSPArchipelagoPartsManager.ReconcileApScience(session);
            }
        }

        private void OnSceneChange(GameScenes scene)
        {
            if (scene == GameScenes.MAINMENU && IsConnected)
            {
                Debug.Log("[KSP-AP] Returning to main menu — disconnecting.");
                HandleDisconnect();
            }
        }

        public void OnDestroy()
        {
            GameEvents.onGameStateLoad.Remove(new EventData<ConfigNode>.OnEvent(OnGameStateLoad));
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneChange);
            missionTracker?.Shutdown();
        }
    }
}
