using System;
using System.Threading;
using System.Windows;
using System.Runtime.InteropServices;

using UnityEngine;
using KSP.UI.Screens;
using UnityEngine.Diagnostics;




namespace KSPArchipelago
{
    [KSPAddon(KSPAddon.Startup.SpaceCentre, true)]
    public class KSPArchipelagoPartsManager : MonoBehaviour
    {
        // GameEvents.OnPartPurchased.Add(new EventData<AvailablePart>.OnEvent(this.onPartResearched));
        private void Start()
        {
            Console.WriteLine("Starting KSP part mod");
            int i = 0;
            foreach (AvailablePart part in PartLoader.LoadedPartsList)
            {
                part.TechRequired = "inaccessable";
                Console.WriteLine("Set requirements to inaccessable for: " + part.name);

                if (i % 3 == 0)
                {
                    ResearchAndDevelopment.AddExperimentalPart(part);
                    Console.WriteLine("Added experimental part: " + part.name);
                }
                i++;
            }

        }
    }

    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class KSPArchipelagoMod : MonoBehaviour
    {
        private void Start()
        {
            DontDestroyOnLoad(this);

            WinConsole.Initialize();
            Console.WriteLine("Setup console!");

            ThreadStart work = new ThreadStart(Archipelago.APConsole.Run);
            Thread thread = new Thread(work);
            thread.Start();

            RegisterKSPEvents();
        }

        private void CheckCompleted(int checkId)
        {

        }

        private void RegisterKSPEvents()
        {
            //GameEvents.onFlagPlant.Add();
        }
        private void UnregisterKSPEvents()
        {
            //GameEvents.onFlagPlant.Remove();
        }

        public void OnDestroy()
        {
            Debug.LogWarning("OnDestroy");
            UnregisterKSPEvents();
        }
    }
}
