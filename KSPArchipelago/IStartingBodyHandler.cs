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
        // parse, with the chosen body's KSC site row (landing coordinate +
        // map-decal flag) straight from slot_data. Implementation must be
        // idempotent — HandleConnect can run more than once per session if
        // the player reconnects. The implementation is responsible for
        // stashing the spec into its own ScenarioModule for save-time
        // persistence and (when a scene is loaded) triggering materialisation.
        void OnStartingBodyResolved(string bodyName, double lat, double lon,
                                    double terrainAlt, bool skipMapDecal);

        // Called by the main mod when the Progressive Launch Pad mass cap
        // changes (item received / item rebuild). The KK handler recomputes
        // the alien launch site's MaxCraftMass from the (now updated)
        // GetCraftMassLimit override. No-op on Kerbin starts (no alien site).
        void RefreshPadMassCap();
    }

    public static class StartingBodyBridge
    {
        public static IStartingBodyHandler Current { get; private set; }

        public static void SetHandler(IStartingBodyHandler h) { Current = h; }
        public static void ClearHandler() { Current = null; }
    }
}
