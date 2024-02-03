using System;
//using System.IO;
using System.Windows;
using System.Runtime.InteropServices;

using UnityEngine;
using KSP.UI.Screens;



namespace KSPArchipelago
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class KSPArchipelagoMod : MonoBehaviour
    {

        [DllImport("kernel32.dll", EntryPoint = "AllocConsole", SetLastError = true, CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall)]
        private static extern int AllocConsole();
        private void Start()
        {
            Debug.LogWarning("Start");
            DontDestroyOnLoad(this);
            AllocConsole();
        }
    }
}
