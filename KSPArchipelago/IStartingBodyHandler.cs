namespace KSPArchipelago
{
    // Implemented by KSPArchipelago.KSC.dll (the optional KK-dependent
    // addon assembly). The main DLL has no KK references; this interface
    // is the only bridge through which the main mod hands the selected
    // starting body to the materialiser.
    //
    // Lifecycle: the KSC DLL's KSPAddon probes the loaded-assembly list
    // for KerbalKonstructs on Awake. If KK is present, it calls
    // StartingBodyBridge.SetHandler(this). If KK is missing, the probe
    // skips SetHandler and Destroys the addon — Current stays null,
    // which is the main mod's signal to reject any non-Kerbin
    // starting_body at AP-connect time (HandleConnect aborts via
    // _slotDataError instead of forwarding to the bridge).
    public interface IStartingBodyHandler
    {
        // Called from KSPArchipelagoMod.HandleConnect after slot_data
        // parse. Implementation must be idempotent — HandleConnect can
        // run more than once per session if the player reconnects. The
        // implementation is responsible for stashing the spec into its
        // own ScenarioModule for save-time persistence and (when a
        // scene is loaded) triggering materialisation.
        void OnStartingBodyResolved(string bodyName);
    }

    public static class StartingBodyBridge
    {
        public static IStartingBodyHandler Current { get; private set; }

        public static void SetHandler(IStartingBodyHandler h) { Current = h; }
        public static void ClearHandler() { Current = null; }
    }
}
