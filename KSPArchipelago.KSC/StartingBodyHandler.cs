// Bridge between KSPArchipelago.dll (main, KK-free) and the
// materialiser in this assembly. Registers itself with
// StartingBodyBridge on game launch; the main mod calls back into it
// from HandleConnect once slot_data is parsed.
//
// Also doubles as the cross-scene MonoBehaviour host for the
// decoration coroutine kicked off by ApplyServerBody — SelectorBootstrap
// only lives during SpaceCenter, but DontDestroyOnLoad keeps this
// instance available regardless of the active scene.

using KerbalKonstructs.Core;
using UnityEngine;

namespace KSPArchipelago.KSC
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class StartingBodyHandler : MonoBehaviour, IStartingBodyHandler
    {
        public static StartingBodyHandler Instance { get; private set; }

        void Awake()
        {
            DontDestroyOnLoad(this);
            Instance = this;
            StartingBodyBridge.SetHandler(this);
            Debug.Log("[KSPArchipelago.KSC] Registered starting-body handler.");
        }

        public void OnStartingBodyResolved(string bodyName)
        {
            SelectorScenarioModule sm = SelectorScenarioModule.Instance;
            if (sm == null)
            {
                // No scenario module yet — typically because AP-connect
                // happened before any save loaded. Stash the choice;
                // the next SelectorScenarioModule.OnLoad consumes it.
                Debug.Log($"[KSPArchipelago.KSC] OnStartingBodyResolved({bodyName}) " +
                          "before any scenario module — stashing for next OnLoad.");
                SelectorScenarioModule.PendingBodyName = bodyName;
                return;
            }
            sm.ApplyServerBody(bodyName);
        }

        // Kick the decoration-placement coroutine.  Hosted here (not on
        // SelectorScenarioModule or SelectorBootstrap) so it survives
        // scene transitions — relevant when AP connects from a non-
        // SpaceCenter scene and we want the placement to wait out the
        // scene change rather than immediately time out.
        public void SchedulePlaceDecorations(BodySpec spec, CelestialBody body, GroupCenter gc)
        {
            StartCoroutine(Decorations.WaitAndPlace(spec, body, gc));
        }
    }
}
