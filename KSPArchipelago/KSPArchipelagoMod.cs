using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

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
                toastText = $"AP: Received {itemName} (+{amount} science)";
                ScreenMessages.PostScreenMessage(toastText, 4f, ScreenMessageStyle.UPPER_CENTER);
                PostToMessageSystem(received, toastText);
                return;
            }

            // Other filler items: just notify.
            if (itemName == "Engineering Report" || itemName == "Cosmetic Unlock")
            {
                toastText = $"AP: Received {itemName}";
                ScreenMessages.PostScreenMessage(toastText, 4f, ScreenMessageStyle.UPPER_CENTER);
                PostToMessageSystem(received, toastText);
                return;
            }

            // Progressive R&D: upgrade the tech tree band limit.
            if (itemName == "Progressive R&D")
            {
                var mod = UnityEngine.Object.FindObjectOfType<KSPArchipelagoMod>();
                if (mod != null) mod.IncrementRDLevel();
                toastText = $"AP: R&D Facility Upgraded to Level {mod?.RDLevel ?? 0}!";
                ScreenMessages.PostScreenMessage(toastText, 4f, ScreenMessageStyle.UPPER_CENTER);
                PostToMessageSystem(received, toastText);
                return;
            }

            // Assume everything else is a part name.
            AvailablePart part = PartLoader.getPartInfoByName(itemName);
            if (part != null)
            {
                ResearchAndDevelopment.AddExperimentalPart(part);
                Debug.Log($"[KSP-AP] Unlocked part '{itemName}' ({part.title})");
                toastText = $"AP: Unlocked {part.title}";
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

        public static void SetExperimentalParts(ArchipelagoSession session)
        {
            if (session == null) return;

            var receivedParts = new HashSet<string>();
            foreach (var item in session.Items.AllItemsReceived)
            {
                if (item.ItemName != null)
                    receivedParts.Add(item.ItemName);
            }

            int unlocked = 0;
            int notFound = 0;
            foreach (AvailablePart part in PartLoader.LoadedPartsList)
            {
                if (receivedParts.Contains(part.name))
                {
                    ResearchAndDevelopment.AddExperimentalPart(part);
                    unlocked++;
                }
                else
                {
                    ResearchAndDevelopment.RemoveExperimentalPart(part);
                }
            }
            notFound = receivedParts.Count - unlocked;
            Debug.Log($"[KSP-AP] SetExperimentalParts: {unlocked} unlocked, {notFound} not matched from {receivedParts.Count} received items");
            if (notFound > 0)
            {
                // Log which received items didn't match any loaded part.
                foreach (string name in receivedParts)
                {
                    if (PartLoader.getPartInfoByName(name) == null)
                        Debug.LogWarning($"[KSP-AP] SetExperimentalParts: no loaded part for '{name}'");
                }
            }
        }

        public static void ResetParts(ArchipelagoSession session)
        {
            ScrubTechTree();
            SetExperimentalParts(session);
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

        // Internal notification callback for UI.
        public event Action<string> OnItemReceived;

        // Items received on the network thread, processed on the main thread.
        private readonly ConcurrentQueue<ReceivedItem> pendingItems = new ConcurrentQueue<ReceivedItem>();

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
                ItemsReceivedCount = 0;
                LocationsCheckedCount = 0;

                // Parse node_bands from slot data.
                NodeBands = new Dictionary<string, int>();
                if (loginData.SlotData.TryGetValue("node_bands", out object bandsObj)
                    && bandsObj is Newtonsoft.Json.Linq.JObject bandsDict)
                {
                    foreach (var kvp in bandsDict)
                        NodeBands[kvp.Key] = (int)kvp.Value;
                }

                // Restore R&D level from already-received items on reconnect.
                RDLevel = 0;
                foreach (var item in newSession.Items.AllItemsReceived)
                {
                    if (item.ItemName == "Progressive R&D")
                        RDLevel++;
                }
                Debug.Log($"[KSP-AP] Restored R&D level to {RDLevel} from {newSession.Items.AllItemsReceived.Count} received items");

                session.Items.ItemReceived += HandleItemReceived;

                missionTracker.Initialize(session, Difficulty, TechSlotsPerNode, () => LocationsCheckedCount++);

                if (gameLoaded)
                {
                    KSPArchipelagoPartsManager.ResetParts(session);
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
                KSPArchipelagoPartsManager.ResetParts(session);
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
