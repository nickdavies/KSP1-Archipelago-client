using System;
using System.Collections.Generic;
using System.Threading;

using UnityEngine;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Models;
using Archipelago.MultiClient.Net.Helpers;


namespace KSPArchipelago
{
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

        public static void GiveItem(string itemName)
        {
            // Science packs: add science points directly.
            if (SciencePackAmounts.TryGetValue(itemName, out float amount))
            {
                if (ResearchAndDevelopment.Instance != null)
                    ResearchAndDevelopment.Instance.AddScience(amount, TransactionReasons.Cheating);
                ScreenMessages.PostScreenMessage(
                    $"AP: Received {itemName} (+{amount} science)", 4f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }

            // Other filler items: just notify.
            if (itemName == "Engineering Report" || itemName == "Cosmetic Unlock")
            {
                ScreenMessages.PostScreenMessage(
                    $"AP: Received {itemName}", 4f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }

            // Assume everything else is a part name.
            AvailablePart part = PartLoader.getPartInfoByName(itemName);
            if (part != null)
            {
                ResearchAndDevelopment.AddExperimentalPart(part);
                ScreenMessages.PostScreenMessage(
                    $"AP: Unlocked {part.title}", 4f, ScreenMessageStyle.UPPER_CENTER);
            }
            else
            {
                Debug.LogWarning($"[KSP-AP] GiveItem: unknown item '{itemName}'");
            }
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
            foreach (NetworkItem item in session.Items.AllItemsReceived)
            {
                string name = session.Items.GetItemName(item.Item);
                if (name != null)
                    receivedParts.Add(name);
            }

            foreach (AvailablePart part in PartLoader.LoadedPartsList)
            {
                if (receivedParts.Contains(part.name))
                    ResearchAndDevelopment.AddExperimentalPart(part);
                else
                    ResearchAndDevelopment.RemoveExperimentalPart(part);
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

        // Expose connection state for the UI.
        public bool IsConnected => session != null;
        public int ItemsReceivedCount { get; private set; }
        public int LocationsCheckedCount { get; private set; }
        public string ConnectedSlot { get; private set; }

        // Internal notification callback for UI.
        public event Action<string> OnItemReceived;

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
        }

        private void Update()
        {
            // All GameEvent callbacks and Update() run on the Unity main thread,
            // so no lock needed here.
            missionTracker?.Update();
        }

        public void HandleItemReceived(ReceivedItemsHelper receivedItemsHelper)
        {
            lock (sessionLock)
            {
                if (!gameLoaded) return;
                var item = receivedItemsHelper.DequeueItem();
                string itemName = session.Items.GetItemName(item.Item);
                if (itemName == null)
                {
                    Debug.LogWarning($"[KSP-AP] Received item with unknown ID: {item.Item}");
                    return;
                }
                KSPArchipelagoPartsManager.GiveItem(itemName);
                ItemsReceivedCount++;
                OnItemReceived?.Invoke(itemName);
            }
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
                ConnectedSlot = slotName;
                ItemsReceivedCount = 0;
                LocationsCheckedCount = 0;

                session.Items.ItemReceived += HandleItemReceived;

                missionTracker.Initialize(session, Difficulty, () => LocationsCheckedCount++);

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
                session = null;
                ConnectedSlot = null;
            }
        }

        private void OnGameStateLoad(ConfigNode config)
        {
            lock (sessionLock)
            {
                if (!gameLoaded)
                {
                    Debug.Log("[KSP-AP] Dumping part data to ksp_parts.json");
                    KSPPartDumper.PartDumper.DumpToFile(new System.IO.StreamWriter("ksp_parts.json"));
                }
                gameLoaded = true;
                KSPArchipelagoPartsManager.ResetParts(session);
            }
        }

        public void OnDestroy()
        {
            GameEvents.onGameStateLoad.Remove(new EventData<ConfigNode>.OnEvent(OnGameStateLoad));
            missionTracker?.Shutdown();
        }
    }
}
