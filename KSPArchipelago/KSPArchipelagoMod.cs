using System;
using System.Threading;
using System.Windows;
using System.Runtime.InteropServices;
using System.Collections.Generic;

using UnityEngine;
using KSP.UI.Screens;
using UnityEngine.Diagnostics;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Models;
using Archipelago.MultiClient.Net.Helpers;


namespace KSPArchipelago
{
    public static class KSPArchipelagoPartsManager
    {
        public static void ScrubTechTree()
        {
            Console.WriteLine("Resetting Tech Tree");
            foreach (AvailablePart part in PartLoader.LoadedPartsList)
            {
                Console.WriteLine("Set requirements to inaccessable for: " + part.name);
                if (part.TechRequired != "inaccessable")
                {
                    part.TechRequired = "inaccessable";
                }
            }
        }

        public static void SetExperimentalParts(ArchipelagoSession session)
        {
            if (session != null)
            {
                HashSet<string> partNames = new HashSet<string>();
                foreach (NetworkItem item in session.Items.AllItemsReceived)
                {
                    string partName = session.Items.GetItemName(item.Item);
                    if (partName != null)
                    {
                        partNames.Add(partName);
                    }
                    else
                    {
                        Console.WriteLine("Error! got unknown part: " + item);
                    }
                }
                foreach (AvailablePart part in PartLoader.LoadedPartsList)
                {
                    if (partNames.Contains(part.name))
                    {
                        ResearchAndDevelopment.AddExperimentalPart(part);
                        Console.WriteLine("Bulk enabling part: " + part.name);
                    }
                    else
                    {
                        ResearchAndDevelopment.RemoveExperimentalPart(part);
                        Console.WriteLine("Bulk removing part: " + part.name);
                    }
                }
            }
        }

        public static void GivePart(string partName)
        {
            AvailablePart part = PartLoader.getPartInfoByName(partName);
            if (part != null)
            {
                ResearchAndDevelopment.AddExperimentalPart(part);
            }
            else
            {
                Console.WriteLine("Error! Asked to give unknown part " + partName);
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
        ArchipelagoSession session;
        KSPEvents.KSPEventFactory eventFactory;
        private bool gameLoaded = false;

        private void Start()
        {
            DontDestroyOnLoad(this);

            WinConsole.Initialize();
            Console.WriteLine("Setup console!");

            Archipelago.APConsole console = new Archipelago.APConsole(this);
            ThreadStart work = new ThreadStart(console.Run);
            Thread thread = new Thread(work);
            thread.Start();

            RegisterKSPEvents();
        }

        public void HandleItemReceived(ReceivedItemsHelper receivedItemsHelper)
        {
            lock (sessionLock)
            {
                if (gameLoaded)
                {
                    var item = receivedItemsHelper.DequeueItem();
                    string partName = session.Items.GetItemName(item.Item);
                    if (partName != null)
                    {
                        KSPArchipelagoPartsManager.GivePart(partName);
                    }
                    else
                    {
                        Console.WriteLine("Error! got unknown part: " + item);
                    }
                }
            }
        }

        public void HandleConnect(ArchipelagoSession newSession, LoginSuccessful loginData)
        {
            Console.WriteLine("Successfully connected!");
            lock (sessionLock)
            {
                session = newSession;

                // TODO: get from slot data
                Dictionary<string, bool> experiments = new Dictionary<string, bool>
                {
                    {"asteroidSample", true},
                    {"surfaceSample", true},
                    {"evaReport", true},
                    {"crewReport", true},
                    {"mysteryGoo", true},
                    {"mobileMaterialsLab", true},
                    {"temperatureScan", true},
                    {"barometerScan", true},
                    {"gravityScan", true},
                    {"seismicScan", true},
                    {"atmosphereAnalysis", true},
                    {"recovery", false},
                };
                Dictionary<string, bool> bodies = new Dictionary<string, bool>
                {
                    {"Kerbol", true},
                    {"Moho", true},
                    {"Eve", true},
                    {"Gilly", true},
                    {"Kerbin", true},
                    {"Mun", true},
                    {"Minmus", true},
                    {"Duna", true},
                    {"Ike", true},
                    {"Dres", true},
                    {"Jool", true},
                    {"Laythe", true},
                    {"Vall", true},
                    {"Tylo", true},
                    {"Bop", true},
                    {"Pol", true},
                    {"Eeloo", true},
                };

                this.eventFactory = new KSPEvents.KSPEventFactory(experiments, bodies);

                session.Items.ItemReceived += HandleItemReceived;
                if (gameLoaded)
                {
                    KSPArchipelagoPartsManager.ResetParts(session);
                    ResyncScience();
                }
            }
        }

        private void CompleteLocation(KSPEvents.IKSPEvent e)
        {
            if (e != null)
            {
                return;
            }
            string locationName = e.APLocation();
            string game = session.ConnectionInfo.Game;
            long locationId = session.Locations.GetLocationIdFromName(game, locationName);
            if (locationId == -1)
            {
                Console.WriteLine($"Error! Unknown location '{locationName}' in game '{game}' found");
                return;
            }
            session.Locations.CompleteLocationChecks(locationId);
        }

        private void onScienceRecieved(float amount, ScienceSubject subject, ProtoVessel vessel, bool unknown)
        {
            KSPEvents.IKSPEvent e = eventFactory.FromScienceSubject(subject);
            if (e != null)
            {
                CompleteLocation(e);
            }
        }

        private void ResyncScience()
        {
            string game = session.ConnectionInfo.Game;
            List<long> locations = new List<long>();
            foreach (ScienceSubject subject in ResearchAndDevelopment.GetSubjects())
            {
                KSPEvents.IKSPEvent e = eventFactory.FromScienceSubject(subject);
                if (e != null)
                {
                    continue;
                }
                string locationName = e.APLocation();
                long locationId = session.Locations.GetLocationIdFromName(game, locationName);
                if (locationId == -1)
                {
                    Console.WriteLine($"Error! Unknown location '{locationName}' in game '{game}' found");
                    continue;
                }
                locations.Add(locationId);
            }
            session.Locations.CompleteLocationChecks(locations.ToArray());
        }

        private void ResetParts(ConfigNode config)
        {
            lock (sessionLock)
            {
                gameLoaded = true;
                KSPArchipelagoPartsManager.ResetParts(session);
            }
        }

        private void RegisterKSPEvents()
        {
            // GameEvents.OnPartPurchased.Add(new EventData<AvailablePart>.OnEvent(this.onPartResearched));
            GameEvents.OnScienceRecieved.Add(new EventData<float, ScienceSubject, ProtoVessel, bool>.OnEvent(onScienceRecieved));
            GameEvents.onGameStateLoad.Add(new EventData<ConfigNode>.OnEvent(ResetParts));
            //GameEvents.onFlagPlant.Add();
        }
        private void UnregisterKSPEvents()
        {
            GameEvents.OnScienceRecieved.Remove(new EventData<float, ScienceSubject, ProtoVessel, bool>.OnEvent(onScienceRecieved));
            GameEvents.onGameStateLoad.Remove(new EventData<ConfigNode>.OnEvent(ResetParts));
            //GameEvents.onFlagPlant.Remove();
        }

        public void OnDestroy()
        {
            Debug.LogWarning("OnDestroy");
            UnregisterKSPEvents();
        }
    }
}
