using System;
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
    public static class KSPArchipelagoPartsManager
    {
        // Tier-locked parts shown in editor as AP-icon placeholders.
        // Key = placeholder part name, Value = real part name.
        private static readonly Dictionary<string, string> _tierLockedParts = new Dictionary<string, string>();
        // Reverse lookup: real part name → placeholder part name.
        private static readonly Dictionary<string, string> _tierLockReverse = new Dictionary<string, string>();
        // Which placeholder indices are allocated for tier-lock use.
        private static readonly HashSet<int> _usedPlaceholderIndices = new HashSet<int>();
        public static Dictionary<string, string> TierLockedParts => _tierLockedParts;

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

        /// <summary>
        /// Single path for all item processing. State mutations (progressive counts,
        /// part tracking, experimental parts) always run. Toast/science only when flagged.
        /// </summary>
        public static void GiveItem(string itemName, string senderName,
                                    string locationName,
                                    bool showToast = true,
                                    bool awardScience = true)
        {
            string toastText;

            // Science packs: add science points directly.
            if (SciencePackAmounts.TryGetValue(itemName, out float amount))
            {
                if (awardScience)
                {
                    if (ResearchAndDevelopment.Instance != null)
                        ResearchAndDevelopment.Instance.AddScience(amount, TransactionReasons.Cheating);
                    if (ApScenarioModule.Instance != null)
                        ApScenarioModule.Instance.TotalApScienceAwarded += amount;
                }
                if (showToast)
                {
                    toastText = $"AP: Received {itemName} (+{amount} science)";
                    ScreenMessages.PostScreenMessage(toastText, 4f, ScreenMessageStyle.UPPER_CENTER);
                    PostToMessageSystem(senderName, locationName, toastText);
                }
                return;
            }

            var mod = UnityEngine.Object.FindObjectOfType<KSPArchipelagoMod>();

            // Progressive R&D: upgrade the tech tree band limit.
            if (itemName == "Progressive R&D")
            {
                if (mod != null) mod.IncrementRDLevel();
                if (showToast)
                {
                    toastText = $"AP: R&D Facility Upgraded to Level {mod?.RDLevel ?? 0}!";
                    ScreenMessages.PostScreenMessage(toastText, 4f, ScreenMessageStyle.UPPER_CENTER);
                    PostToMessageSystem(senderName, locationName, toastText);
                }
                return;
            }

            // Progressive part items: unlock 1 random part from the new tier,
            // plus any individually-received parts from that tier.
            if (mod?.ProgressiveTiers != null
                && mod.ProgressiveTiers.TryGetValue(itemName, out var tierMap))
            {
                int newLevel = mod.IncrementProgressiveCount(itemName);
                int unlocked = mod.UnlockProgressiveTier(itemName, newLevel);
                if (showToast)
                {
                    toastText = $"AP: {itemName} Tier {newLevel} ({unlocked} parts unlocked)";
                    Debug.Log($"[KSP-AP] {itemName} → tier {newLevel}, {unlocked} parts");
                    ScreenMessages.PostScreenMessage(toastText, 4f, ScreenMessageStyle.UPPER_CENTER);
                    PostToMessageSystem(senderName, locationName, toastText);
                }
                return;
            }

            // Individual part items: unlock only if the progressive tier gate
            // is satisfied (or if the part is not in any progressive chain).
            AvailablePart part = PartLoader.getPartInfoByName(itemName);
            if (part != null)
            {
                if (mod != null) mod.TrackReceivedPart(itemName);

                if (mod != null && mod.IsPartTierLocked(itemName))
                {
                    // Show an AP-icon placeholder in the editor instead of the
                    // real part. EditorTierLock blocks placement attempts.
                    string progName = mod.GetPartProgressiveName(itemName);
                    int reqTier = mod.GetPartRequiredTier(itemName);
                    AllocateTierLockPlaceholder(itemName, part, progName, reqTier);

                    Debug.Log($"[KSP-AP] Tier-locked part '{itemName}' ({part.title}) "
                            + $"placeholder in editor, requires {progName} Tier {reqTier}");
                    if (showToast)
                    {
                        toastText = $"AP: {part.title} (tier locked)";
                        ScreenMessages.PostScreenMessage(toastText, 4f, ScreenMessageStyle.UPPER_CENTER);
                        PostToMessageSystem(senderName, locationName, toastText);
                    }
                }
                else
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

        private static void AllocateTierLockPlaceholder(
            string realPartName, AvailablePart realPart, string progName, int reqTier)
        {
            // Skip if already allocated (idempotent on replay).
            if (_tierLockReverse.ContainsKey(realPartName)) return;

            // Find next free placeholder index.
            int idx = -1;
            for (int i = 1; i <= 250; i++)
            {
                if (!_usedPlaceholderIndices.Contains(i))
                {
                    idx = i;
                    break;
                }
            }
            if (idx < 0)
            {
                Debug.LogWarning("[KSP-AP] Ran out of placeholders for tier-lock");
                return;
            }

            string phName = $"ap.placeholder.{idx:D3}";
            AvailablePart ph = PartLoader.getPartInfoByName(phName);
            if (ph == null)
            {
                Debug.LogWarning($"[KSP-AP] Placeholder '{phName}' not found in PartLoader");
                return;
            }

            ph.title = $"{realPart.title} (tier locked)";
            ph.description = $"Requires {progName} Tier {reqTier}\n\n{realPart.description}";
            ph.category = realPart.category;
            ResearchAndDevelopment.AddExperimentalPart(ph);

            _usedPlaceholderIndices.Add(idx);
            _tierLockedParts[phName] = realPartName;
            _tierLockReverse[realPartName] = phName;
        }

        private static void FreeTierLockPlaceholder(string realPartName)
        {
            if (!_tierLockReverse.TryGetValue(realPartName, out string phName))
                return;

            AvailablePart ph = PartLoader.getPartInfoByName(phName);
            if (ph != null)
            {
                ResearchAndDevelopment.RemoveExperimentalPart(ph);
                ph.title = "AP Item";
                ph.description = "An Archipelago multiworld item.";
                ph.category = PartCategories.none;
            }

            // Parse index from name to free it.
            string suffix = phName.Substring("ap.placeholder.".Length);
            if (int.TryParse(suffix, out int idx))
                _usedPlaceholderIndices.Remove(idx);

            _tierLockedParts.Remove(phName);
            _tierLockReverse.Remove(realPartName);
            Debug.Log($"[KSP-AP] Tier-lock cleared for '{realPartName}' (freed {phName})");
        }

        /// <summary>
        /// Remove tier-lock placeholder for a part when its progressive tier arrives.
        /// </summary>
        public static void RestoreTierLockOverrides(string partName, AvailablePart ap)
        {
            FreeTierLockPlaceholder(partName);
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
            // Reset placeholder titles/descriptions and free indices.
            foreach (var kvp in _tierLockedParts)
            {
                AvailablePart ph = PartLoader.getPartInfoByName(kvp.Key);
                if (ph != null)
                {
                    ph.title = "AP Item";
                    ph.description = "An Archipelago multiworld item.";
                    ph.category = PartCategories.none;
                }
            }
            _tierLockedParts.Clear();
            _tierLockReverse.Clear();
            _usedPlaceholderIndices.Clear();

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
        // Running count of progressive item copies received (reset on rebuild).
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
                    KSPArchipelagoPartsManager.RestoreTierLockOverrides(chosen, chosenPart);
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
                        KSPArchipelagoPartsManager.RestoreTierLockOverrides(p, ap);
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

        public string GetPartProgressiveName(string partName)
        {
            return _partProgName.TryGetValue(partName, out string name) ? name : null;
        }

        public int GetPartRequiredTier(string partName)
        {
            return _partProgTier.TryGetValue(partName, out int tier) ? tier : 0;
        }

        // Internal notification callback for UI.
        public event Action<string> OnItemReceived;

        // Deferred reset: HandleConnect runs on a background thread but
        // item processing calls Unity APIs that must run on the main thread.
        // This flag tells Update() to do a full rebuild before processing.
        private volatile bool _needsReset = false;

        // Track the last processed index for fast incremental polling.
        private int _lastProcessedIndex = 0;

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
            if (_needsReset)
            {
                _needsReset = false;
                // OnLoad creates a new HashSet; re-share the reference with the tracker.
                if (missionTracker != null && ApScenarioModule.Instance != null)
                    missionTracker.SetPendingNames(ApScenarioModule.Instance.PendingLocationNames);
                KSPArchipelagoPartsManager.ScrubTechTree();
                KSPArchipelagoPartsManager.ClearAllExperimentalParts();
                ResetProgressiveState();
                ProcessAllItems();
                KSPArchipelagoPartsManager.ReconcileApScience(session);
            }
            else if (session != null)
            {
                ProcessNewItems();
            }

            missionTracker?.Update();
        }

        /// <summary>
        /// Zero all progressive state so ProcessAllItems can rebuild from scratch.
        /// </summary>
        public void ResetProgressiveState()
        {
            RDLevel = 0;
            _progressiveCounts = new Dictionary<string, int>();
            _receivedParts = new HashSet<string>();
        }

        /// <summary>
        /// Iterate ALL of AllItemsReceived and rebuild state from scratch.
        /// Items already in AwardedItemIndices get silent processing (no toast/science).
        /// New items get full processing and are added to the persisted set.
        /// </summary>
        public void ProcessAllItems()
        {
            if (session == null) return;

            var allItems = session.Items.AllItemsReceived;
            var awarded = ApScenarioModule.Instance?.AwardedItemIndices;
            int count = allItems.Count;

            Debug.Log($"[KSP-AP] ProcessAllItems: {count} items, {awarded?.Count ?? 0} previously awarded");

            for (int i = 0; i < count; i++)
            {
                var item = allItems[i];
                if (item.ItemName == null) continue;

                bool alreadyAwarded = awarded != null && awarded.Contains(i);
                KSPArchipelagoPartsManager.GiveItem(
                    item.ItemName, item.Player?.Alias, item.LocationName,
                    showToast: !alreadyAwarded,
                    awardScience: !alreadyAwarded);

                if (!alreadyAwarded)
                    awarded?.Add(i);
            }

            _lastProcessedIndex = count;
            ItemsReceivedCount = count;

            Debug.Log($"[KSP-AP] ProcessAllItems complete: {awarded?.Count ?? 0} total awarded");
        }

        /// <summary>
        /// Fast incremental path: check for new items beyond what we've already processed.
        /// </summary>
        private void ProcessNewItems()
        {
            if (session == null) return;

            var allItems = session.Items.AllItemsReceived;
            var awarded = ApScenarioModule.Instance?.AwardedItemIndices;
            int count = allItems.Count;

            if (count <= _lastProcessedIndex) return;

            for (int i = _lastProcessedIndex; i < count; i++)
            {
                var item = allItems[i];
                if (item.ItemName == null) continue;

                KSPArchipelagoPartsManager.GiveItem(
                    item.ItemName, item.Player?.Alias, item.LocationName,
                    showToast: true, awardScience: true);

                awarded?.Add(i);
                ItemsReceivedCount++;
                OnItemReceived?.Invoke(item.ItemName);
            }

            _lastProcessedIndex = count;
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

                // Minimal drain handler: keeps AP library internal state clean.
                // All real processing happens on the main thread via Update().
                session.Items.ItemReceived += (h) => h.DequeueItem();

                // Share the pending-names set with the tracker so ApScenarioModule
                // sees offline-queued locations when KSP saves.
                if (ApScenarioModule.Instance != null)
                    missionTracker.SetPendingNames(ApScenarioModule.Instance.PendingLocationNames);
                missionTracker.OnConnect(session, Difficulty, TechSlotsPerNode, () => LocationsCheckedCount++);

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
                missionTracker?.OnDisconnect();
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
                // ApScenarioModule.OnLoad will have restored AwardedItemIndices
                // from the save file before Update() runs the rebuild.
                _needsReset = true;
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
            missionTracker?.Destroy();
        }
    }
}
