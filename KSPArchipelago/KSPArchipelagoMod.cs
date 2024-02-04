using System;
using System.Windows;
using System.Runtime.InteropServices;

using UnityEngine;
using KSP.UI.Screens;
using UnityEngine.Diagnostics;



namespace KSPArchipelago
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class KSPArchipelagoMod : MonoBehaviour
    {

        private void Start()
        {
            DontDestroyOnLoad(this);

            APConsole.Initialize();
            Console.WriteLine("Setup console!");
            string cmd = Console.ReadLine();
            Console.WriteLine(cmd);
        }

        public void OnDestroy()
        {
            Debug.LogWarning("OnDestroy");
            //FreeConsole();
        }
    }
}
