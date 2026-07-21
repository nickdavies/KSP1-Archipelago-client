using UnityEngine;

namespace KSPArchipelago.Traps.Actuators
{
    /// <summary>
    /// One-shot throttle jolt: the throttle jumps to a random setting of at
    /// least 25%. Zero duration — the player can correct it the moment they
    /// react; the spice is WHEN it lands (docking, landing burns).
    /// </summary>
    internal sealed class StickyThrottleTrap : ITrapActuator
    {
        public string ItemName => "Trap: Sticky Throttle";
        public bool ManagesWarpItself => false;
        public bool CanFireWithoutVessel => false;

        public bool IsEligible(Vessel v) => !v.packed && v.IsControllable;

        public void Fire(MonoBehaviour host, Vessel v, TrapEffectsRunner runner)
        {
            float target = Random.Range(0.25f, 1f);
            FlightInputHandler.state.mainThrottle = target;
            ScreenMessages.PostScreenMessage(
                $"<color=orange>TRAP:</color> The throttle lever slipped to {target:P0}!",
                5f, ScreenMessageStyle.UPPER_CENTER);
        }
    }
}
